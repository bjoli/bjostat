/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Categories: columns of strings from a small set, as R's factors. Arrow
// keeps such a column as a dictionary: the strings once, the levels, and for
// each row the number of its level, a code. A group-by, a sort or a join on
// the codes is one on small ints, with no hash of the strings.
//
// A category here is always a dictionary of int codes and string levels. The
// order of the levels is the order of a sort: the levels given, or the
// strings in the order of their code points.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Bjostat;

public static class Categories
{
    /// <summary>The type of every category column.</summary>
    public static readonly DictionaryType Type = new(Int32Type.Default, StringType.Default, false);

    /// <summary>Whether the column is a category: a dictionary of int codes and string levels.</summary>
    public static bool Is(IArrowArray a) =>
        a is DictionaryArray { Dictionary: StringArray, Indices: Int32Array };

    internal static bool IsType(IArrowType t) =>
        t is DictionaryType { IndexType: Int32Type, ValueType: StringType };

    /// <summary>
    /// The table with the column <paramref name="name"/> a category. With no
    /// levels, the levels are the distinct strings in the order of their code
    /// points; with levels, a string that is not one of them is a
    /// TableProblem. A null stays a null.
    /// </summary>
    public static RecordBatch Categorize(RecordBatch t, string name, string[] levels)
    {
        int i = Tables.IndexOf(t, name);
        if (i < 0)
            throw new TableProblem($"the table has no column '{name}'. Its columns are: "
                                   + string.Join(", ", t.Schema.FieldsList.Select(f => f.Name)) + ".");
        if (levels.Distinct(StringComparer.Ordinal).Count() != levels.Length)
            throw new TableProblem("a level is given twice.");
        var a = t.Column(i);
        IArrowArray made = a switch
        {
            StringArray s => FromStrings(s, levels, name),
            DictionaryArray d when Is(d) => levels.Length == 0 ? d : FromStrings(Strings(d), levels, name),
            _ => throw new TableProblem($"'{name}' is a column of {Reshape.TypeText(a.Data.DataType)}; "
                                        + "a category is made of strings."),
        };
        return Query.WithColumn(t, name, made);
    }

    // The category of the strings: each distinct string is found once, and
    // its code is then that of each row with it.
    private static DictionaryArray FromStrings(StringArray s, string[] given, string name)
    {
        int n = s.Length;
        StringArray? dictionary = null;
        var codeBytes = NumberToBytes(s, out _, firsts =>
        {
            var levelOf = new int[firsts.Count];
            if (given.Length == 0)
            {
                var byBytes = InByteOrder(s, firsts);
                var firstRows = new int[byBytes.Length];
                for (int l = 0; l < byBytes.Length; l++)
                {
                    int id = byBytes[l];
                    levelOf[id] = l;
                    firstRows[l] = firsts[id];
                }
                dictionary = TakeStrings(s, firstRows);
            }
            else
            {
                var index = new Dictionary<string, int>(given.Length, StringComparer.Ordinal);
                for (int l = 0; l < given.Length; l++) index[given[l]] = l;
                for (int id = 0; id < firsts.Count; id++)
                {
                    var text = s.GetString(firsts[id]);
                    if (!index.TryGetValue(text, out levelOf[id]))
                        throw new TableProblem($"'{text}' in row {firsts[id]} of '{name}' is not one of the levels.");
                }
                dictionary = new StringArray.Builder().AppendRange(given).Build();
            }
            return levelOf;
        });
        var data = new ArrayData(Type, n, s.NullCount, 0, new[] { Validity(s), new ArrowBuffer(codeBytes) }, null, dictionary!.Data);
        return new DictionaryArray(data);
    }

    /// <summary>
    /// The strings of the column as a category whose levels are the distinct
    /// strings in the order of their first rows, not sorted: the code of a
    /// row is the number of its string, from <see cref="StringIds"/>, so the
    /// strings are numbered once, in parts on the workers. A null stays a
    /// null. What (bjostat query)'s string-groups gives the groups and the
    /// group of each row from.
    /// </summary>
    public static IArrowArray FirstSeen(IArrowArray a)
    {
        var s = (StringArray)a;
        int n = s.Length;
        var codeBytes = NumberToBytes(s, out var firsts, null);
        var dictionary = TakeStrings(s, CollectionsMarshal.AsSpan(firsts));
        var data = new ArrayData(Type, n, s.NullCount, 0, new[] { Validity(s), new ArrowBuffer(codeBytes) }, null, dictionary.Data);
        return new DictionaryArray(data);
    }

    // The strings at the rows of s, copied directly as UTF-8 bytes without
    // making a C# string of each.
    private static StringArray TakeStrings(StringArray s, ReadOnlySpan<int> rows)
    {
        int k = rows.Length;
        var offsets = s.ValueOffsets;
        var bytes = s.ValueBuffer.Span;
        var newOffsetBytes = GC.AllocateUninitializedArray<byte>((k + 1) * sizeof(int));
        var newOffsets = MemoryMarshal.Cast<byte, int>(newOffsetBytes.AsSpan());
        int total = 0;
        newOffsets[0] = 0;
        for (int i = 0; i < k; i++)
        {
            int r = rows[i];
            total += offsets[r + 1] - offsets[r];
            newOffsets[i + 1] = total;
        }
        var newBytes = GC.AllocateUninitializedArray<byte>(total);
        for (int i = 0; i < k; i++)
        {
            int r = rows[i];
            int start = offsets[r];
            bytes.Slice(start, offsets[r + 1] - start).CopyTo(newBytes.AsSpan(newOffsets[i]));
        }
        return new StringArray(k, new ArrowBuffer(newOffsetBytes), new ArrowBuffer(newBytes), ArrowBuffer.Empty, 0);
    }

    /// <summary>
    /// A number for each distinct string, from 0 in the order of their first
    /// rows, and the id of each row; 0 at a null. <paramref name="firsts"/>
    /// gets the first row of each id. The table is open addressing over the
    /// strings' hashes, each kept, with no call through an interface. A long
    /// column is numbered in parts on the compute threads.
    /// </summary>
    internal static int[] StringIds(StringArray s, out List<int> firsts)
    {
        int n = s.Length;
        var ids = s.NullCount == 0 ? GC.AllocateUninitializedArray<int>(n) : new int[n];
        NumberCore(s, ids, null, out firsts, null);
        return ids;
    }

    /// <summary>
    /// A number for each distinct string in the order of their UTF-8 bytes
    /// (0 to <paramref name="card"/> - 1, or with nulls at <paramref name="card"/> - 1),
    /// numbered in a single pass over the rows.
    /// </summary>
    internal static int[] SortedStringIds(StringArray s, out int card)
    {
        int n = s.Length;
        var ids = GC.AllocateUninitializedArray<int>(n);
        int distinct = 0;
        NumberCore(s, ids, null, out _, firsts =>
        {
            distinct = firsts.Count;
            var byBytes = InByteOrder(s, firsts);
            var rank = new int[byBytes.Length];
            for (int l = 0; l < byBytes.Length; l++) rank[byBytes[l]] = l;
            return rank;
        }, nullValue: -1);
        card = s.NullCount == 0 ? distinct : distinct + 1;
        return ids;
    }

    private static byte[] NumberToBytes(StringArray s, out List<int> firsts, Func<List<int>, int[]>? remap)
    {
        int n = s.Length;
        var bytes = s.NullCount == 0 ? GC.AllocateUninitializedArray<byte>(n * sizeof(int)) : new byte[n * sizeof(int)];
        NumberCore(s, null, bytes, out firsts, remap);
        return bytes;
    }

    private static void NumberCore(
        StringArray s,
        int[]? intIds,
        byte[]? byteIds,
        out List<int> firsts,
        Func<List<int>, int[]>? remap,
        int nullValue = 0)
    {
        int n = s.Length;
        int parts = n < 2 * PartRows ? 1 : Math.Min(n / PartRows, Workers.Count);
        if (parts < 2)
        {
            var span = intIds != null ? intIds.AsSpan() : MemoryMarshal.Cast<byte, int>(byteIds.AsSpan());
            var one = new StringIdTable(s);
            one.Number(0, n, span);
            firsts = new List<int>(one.Entries.Count);
            foreach (ref readonly var e in CollectionsMarshal.AsSpan(one.Entries)) firsts.Add(e.First);
            int nv = nullValue < 0 ? firsts.Count : nullValue;
            if (remap != null)
            {
                var levelOf = remap(firsts);
                var valid = s.NullCount == 0 ? ReadOnlySpan<byte>.Empty : s.NullBitmapBuffer.Span;
                int off = s.Offset;
                for (int r = 0; r < n; r++)
                {
                    int bit = off + r;
                    span[r] = !valid.IsEmpty && (valid[bit >> 3] & (1 << (bit & 7))) == 0 ? nv : levelOf[span[r]];
                }
            }
            return;
        }
        if (ManyDistinct(s))
        {
            NumberByHash(s, intIds, byteIds, out firsts, remap, nullValue, parts);
            return;
        }
        // Few distinct strings: each part numbers its rows on a worker; then
        // the parts' strings are numbered for the whole column, the parts in
        // order, so that the numbers are in the order of the strings' first
        // rows; then each part's rows get those numbers, on the workers
        // again. Each part's table has each string the part has, so with
        // many distinct strings each string is numbered once for each part
        // and again in the merge: see NumberByHash.
        int size = (n + parts - 1) / parts;
        var tables = new StringIdTable[parts];
        Workers.For(parts, p =>
        {
            var span = intIds != null ? intIds.AsSpan() : MemoryMarshal.Cast<byte, int>(byteIds.AsSpan());
            var t = new StringIdTable(s);
            t.Number(p * size, Math.Min(n, (p + 1) * size), span);
            tables[p] = t;
        });
        var maps = Merged(s, tables, out firsts);
        int nullCode = nullValue < 0 ? firsts.Count : nullValue;
        if (remap != null)
        {
            var levelOf = remap(firsts);
            for (int p = 0; p < parts; p++)
            {
                var map = maps[p];
                for (int k = 0; k < map.Length; k++) map[k] = levelOf[map[k]];
            }
        }
        int offset = s.Offset;
        Workers.For(parts, p =>
        {
            var span = intIds != null ? intIds.AsSpan() : MemoryMarshal.Cast<byte, int>(byteIds.AsSpan());
            var map = maps[p];
            var valid = s.NullCount == 0 ? ReadOnlySpan<byte>.Empty : s.NullBitmapBuffer.Span;
            if (valid.IsEmpty)
            {
                for (int r = p * size, to = Math.Min(n, (p + 1) * size); r < to; r++)
                    span[r] = map[span[r]];
            }
            else
            {
                for (int r = p * size, to = Math.Min(n, (p + 1) * size); r < to; r++)
                {
                    int bit = offset + r;
                    span[r] = (valid[bit >> 3] & (1 << (bit & 7))) == 0 ? nullCode : map[span[r]];
                }
            }
        });
    }

    // The rows that ManyDistinct numbers, and the distinct strings among them
    // above which a column is numbered by NumberByHash.
    // The rows that ManyDistinct numbers, and the distinct strings among them
    // above which a column is numbered by NumberByHash. Measured on 1M rows
    // and 23 parts: with 5 000 strings, 4 026 of them in the first 8 192
    // rows, NumberCore's parts took 4.5 ms and NumberByHash 4.9 to 5.1; with
    // 20 000 strings of the wide file's kind, 5 192 in the first rows, 10.7
    // to 11.6 against 6.8 to 7.2. Near the line the two cost about the
    // same, so a column on the wrong side of it loses little.
    private const int SampleRows = 8192;
    private const int SampleDistinct = 4608;

    /// <summary>
    /// Whether the column has many distinct strings: more than
    /// <see cref="SampleDistinct"/> in its first <see cref="SampleRows"/>
    /// rows, numbered on the calling thread. The count stops when it is
    /// above, so a column of many strings is decided early, and one of a few
    /// is all hits in a small table.
    /// </summary>
    private static bool ManyDistinct(StringArray s)
    {
        Span<int> ids = stackalloc int[SampleRows];
        var t = new StringIdTable(s);
        int rows = Math.Min(SampleRows, s.Length);
        for (int from = 0; from < rows; from += 1024)
        {
            t.Number(from, Math.Min(rows, from + 1024), ids);
            if (t.Entries.Count > SampleDistinct) return true;
        }
        return false;
    }

    // The buckets of NumberByHash, by the top 8 bits of a string's hash.
    private const int HashBuckets = 256;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashBucket(int hash) => (int)((uint)hash >> 24);

    /// <summary>
    /// NumberCore for a column with many distinct strings. Each string is
    /// numbered by one table, once, and no two threads write to one array's
    /// same rows:
    /// <list type="bullet">
    /// <item>Each part hashes its rows and counts its rows in each bucket.</item>
    /// <item>The rows are put bucket by bucket into one array, a bucket's
    /// rows part by part, so in row order.</item>
    /// <item>Each bucket's rows are numbered by a table of its own, into the
    /// bucket's place in a second array: a table's numbers are in the order
    /// of its strings' first rows.</item>
    /// <item>Each part goes through its rows in order, with a place in each
    /// bucket, which gives each row its number in its bucket: three times,
    /// to count the part's first rows, to give the strings their numbers
    /// for the column (the first rows before), and to give each row the
    /// number of its string, with <paramref name="remap"/> applied.</item>
    /// </list>
    /// A row's number is not written by the bucket's thread, because the
    /// rows of a bucket are spread over the column: the threads would write
    /// next to each other's rows, in the same cache lines.
    /// </summary>
    private static void NumberByHash(
        StringArray s,
        int[]? intIds,
        byte[]? byteIds,
        out List<int> firsts,
        Func<List<int>, int[]>? remap,
        int nullValue,
        int parts)
    {
        int n = s.Length;
        int size = (n + parts - 1) / parts;
        const int B = HashBuckets;
        var valid = s.NullCount == 0 ? ArrowBuffer.Empty : s.NullBitmapBuffer;
        int offset = s.Offset;

        var hashes = GC.AllocateUninitializedArray<int>(n);
        var counts = new int[parts * B];
        Workers.For(parts, p =>
            StringIdTable.HashRows(s, p * size, Math.Min(n, (p + 1) * size), hashes, counts.AsSpan(p * B, B)));

        // Where each part's rows of each bucket start: bucket by bucket, and
        // in a bucket part by part.
        var bucketStart = new int[B + 1];
        var place = new int[parts * B];
        int at = 0;
        for (int b = 0; b < B; b++)
        {
            bucketStart[b] = at;
            for (int p = 0; p < parts; p++)
            {
                place[p * B + b] = at;
                at += counts[p * B + b];
            }
        }
        bucketStart[B] = at;
        var rows = GC.AllocateUninitializedArray<int>(at);
        Workers.For(parts, p =>
        {
            Span<int> next = stackalloc int[B];
            place.AsSpan(p * B, B).CopyTo(next);
            var bits = valid.Span;
            for (int r = p * size, to = Math.Min(n, (p + 1) * size); r < to; r++)
            {
                if (!bits.IsEmpty)
                {
                    int bit = offset + r;
                    if ((bits[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                }
                rows[next[HashBucket(hashes[r])]++] = r;
            }
        });

        var local = GC.AllocateUninitializedArray<int>(at);
        var tables = new StringIdTable[B];
        Workers.For(B, b =>
        {
            int from = bucketStart[b], len = bucketStart[b + 1] - from;
            var table = new StringIdTable(s);
            table.NumberRows(rows.AsSpan(from, len), local.AsSpan(from, len));
            tables[b] = table;
        });

        // The first rows of each part.
        var before = new int[parts + 1];
        Workers.For(parts, p =>
        {
            Span<int> next = stackalloc int[B];
            place.AsSpan(p * B, B).CopyTo(next);
            var bits = valid.Span;
            int c = 0;
            for (int r = p * size, to = Math.Min(n, (p + 1) * size); r < to; r++)
            {
                if (!bits.IsEmpty)
                {
                    int bit = offset + r;
                    if ((bits[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                }
                int b = HashBucket(hashes[r]);
                if (tables[b].Entries[local[next[b]++]].First == r) c++;
            }
            before[p + 1] = c;
        });
        for (int p = 0; p < parts; p++) before[p + 1] += before[p];
        int count = before[parts];

        // Each string's number for the column, and its first row.
        var number = new int[B][];
        for (int b = 0; b < B; b++) number[b] = new int[tables[b].Entries.Count];
        var firstRows = new List<int>(count);
        CollectionsMarshal.SetCount(firstRows, count);
        Workers.For(parts, p =>
        {
            Span<int> next = stackalloc int[B];
            place.AsSpan(p * B, B).CopyTo(next);
            var bits = valid.Span;
            var all = CollectionsMarshal.AsSpan(firstRows);
            int c = before[p];
            for (int r = p * size, to = Math.Min(n, (p + 1) * size); r < to; r++)
            {
                if (!bits.IsEmpty)
                {
                    int bit = offset + r;
                    if ((bits[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                }
                int b = HashBucket(hashes[r]);
                int k = local[next[b]++];
                if (tables[b].Entries[k].First == r)
                {
                    number[b][k] = c;
                    all[c++] = r;
                }
            }
        });
        firsts = firstRows;

        if (remap != null)
        {
            var levelOf = remap(firstRows);
            Workers.For(B, b =>
            {
                var map = number[b];
                for (int k = 0; k < map.Length; k++) map[k] = levelOf[map[k]];
            });
        }
        int nullCode = nullValue < 0 ? count : nullValue;
        Workers.For(parts, p =>
        {
            var span = intIds != null ? intIds.AsSpan() : MemoryMarshal.Cast<byte, int>(byteIds.AsSpan());
            Span<int> next = stackalloc int[B];
            place.AsSpan(p * B, B).CopyTo(next);
            var bits = valid.Span;
            for (int r = p * size, to = Math.Min(n, (p + 1) * size); r < to; r++)
            {
                if (!bits.IsEmpty)
                {
                    int bit = offset + r;
                    if ((bits[bit >> 3] & (1 << (bit & 7))) == 0)
                    {
                        span[r] = nullCode;
                        continue;
                    }
                }
                int b = HashBucket(hashes[r]);
                span[r] = number[b][local[next[b]++]];
            }
        });
    }

    private readonly struct ByteKey(ulong p0, ulong p1, int len, int id) : IComparable<ByteKey>
    {
        public readonly ulong P0 = p0;
        public readonly ulong P1 = p1;
        public readonly int Len = len;
        public readonly int Id = id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(ByteKey other)
        {
            int c = P0.CompareTo(other.P0);
            if (c != 0) return c;
            c = P1.CompareTo(other.P1);
            if (c != 0) return c;
            return Len.CompareTo(other.Len);
        }
    }

    /// <summary>
    /// The numbers of the strings whose first rows are <paramref name="firsts"/>,
    /// in the order of the strings' bytes.
    /// </summary>
    internal static int[] InByteOrder(StringArray s, List<int> firsts)
    {
        int k = firsts.Count;
        var offsets = s.ValueOffsets;
        var bytes = s.ValueBuffer.Span;
        ref byte byteRef = ref MemoryMarshal.GetReference(bytes);
        int safeEnd = bytes.Length - 16;
        var keys = new ByteKey[k];
        int maxLen = 0;
        for (int id = 0; id < k; id++)
        {
            int r = firsts[id];
            int st = offsets[r];
            int len = offsets[r + 1] - st;
            if (len > maxLen) maxLen = len;
            ulong p0 = 0, p1 = 0;
            if (st <= safeEnd)
            {
                ulong raw0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref byteRef, st)));
                if (len < 8)
                {
                    p0 = len == 0 ? 0 : raw0 & (~0UL << ((8 - len) * 8));
                }
                else
                {
                    p0 = raw0;
                    ulong raw1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref byteRef, st + 8)));
                    p1 = len >= 16 ? raw1 : (len == 8 ? 0 : raw1 & (~0UL << ((16 - len) * 8)));
                }
            }
            else
            {
                int take0 = Math.Min(8, len);
                for (int i = 0; i < take0; i++) p0 |= (ulong)bytes[st + i] << (56 - 8 * i);
                int take1 = Math.Min(16, len);
                for (int i = 8; i < take1; i++) p1 |= (ulong)bytes[st + i] << (56 - 8 * (i - 8));
            }
            keys[id] = new ByteKey(p0, p1, len, id);
        }
        if (maxLen <= 16)
        {
            keys.AsSpan().Sort();
        }
        else
        {
            var offMem = s.ValueOffsetsBuffer.Memory;
            int offSlice = s.Offset;
            var memory = s.ValueBuffer.Memory;
            System.Array.Sort(keys, (a, b) =>
            {
                int c = a.P0.CompareTo(b.P0);
                if (c != 0) return c;
                c = a.P1.CompareTo(b.P1);
                if (c != 0) return c;
                if (a.Len <= 16 && b.Len <= 16) return a.Len.CompareTo(b.Len);
                var offs = MemoryMarshal.Cast<byte, int>(offMem.Span).Slice(offSlice);
                var all = memory.Span;
                int ra = firsts[a.Id], rb = firsts[b.Id];
                int sa = offs[ra], sb = offs[rb];
                return all.Slice(sa, a.Len).SequenceCompareTo(all.Slice(sb, b.Len));
            });
        }
        var order = new int[k];
        for (int i = 0; i < k; i++) order[i] = keys[i].Id;
        return order;
    }

    // The buckets of a merge of the parts' numbers, by the top bits of a
    // string's hash.
    private const int MergeBuckets = 64;

    /// <summary>
    /// The number for the column of each part's string: maps[p][k] is that
    /// of string k of part p. <paramref name="firsts"/> gets the first row of
    /// each number. Numbered so, the strings are in the order of their first
    /// rows.
    /// </summary>
    private static int[][] Merged(StringArray s, StringIdTable[] tables, out List<int> firsts)
    {
        int parts = tables.Length;
        var maps = new int[parts][];
        // The strings of each part in each bucket, in the part's order.
        var inBucket = new List<int>[parts][];
        Workers.For(parts, p =>
        {
            var entries = CollectionsMarshal.AsSpan(tables[p].Entries);
            maps[p] = new int[entries.Length];
            var lists = new List<int>[MergeBuckets];
            for (int b = 0; b < MergeBuckets; b++) lists[b] = new List<int>();
            for (int k = 0; k < entries.Length; k++) lists[Bucket(entries[k].Hash)].Add(k);
            inBucket[p] = lists;
        });
        var merged = new StringIdTable[MergeBuckets];
        Workers.For(MergeBuckets, b =>
        {
            var table = new StringIdTable(s);
            for (int p = 0; p < parts; p++)
            {
                var entries = CollectionsMarshal.AsSpan(tables[p].Entries);
                var map = maps[p];
                var bucketList = CollectionsMarshal.AsSpan(inBucket[p][b]);
                for (int i = 0; i < bucketList.Length; i++)
                {
                    int k = bucketList[i];
                    map[k] = table.IdOf(in entries[k]);
                }
            }
            merged[b] = table;
        });
        // The strings of all the buckets in the order of their first rows,
        // each row once: that order gives their numbers.
        int count = 0;
        foreach (var table in merged) count += table.Entries.Count;
        var packed = GC.AllocateUninitializedArray<ulong>(count);
        for (int b = 0, i = 0; b < MergeBuckets; b++)
        {
            var entries = CollectionsMarshal.AsSpan(merged[b].Entries);
            for (int e = 0; e < entries.Length; e++, i++)
                packed[i] = ((ulong)(uint)entries[e].First << 32) | (uint)((b << 24) | e);
        }
        packed.AsSpan().Sort();
        var number = new int[MergeBuckets][];
        for (int b = 0; b < MergeBuckets; b++) number[b] = new int[merged[b].Entries.Count];
        firsts = new List<int>(count);
        CollectionsMarshal.SetCount(firsts, count);
        var firstSpan = CollectionsMarshal.AsSpan(firsts);
        for (int i = 0; i < count; i++)
        {
            ulong v = packed[i];
            int place = (int)(uint)v;
            number[place >> 24][place & 0xFF_FFFF] = i;
            firstSpan[i] = (int)(v >> 32);
        }
        Workers.For(parts, p =>
        {
            var entries = CollectionsMarshal.AsSpan(tables[p].Entries);
            var map = maps[p];
            for (int k = 0; k < map.Length; k++) map[k] = number[Bucket(entries[k].Hash)][map[k]];
        });
        return maps;
    }

    private static int Bucket(int hash) => (int)((uint)hash >> 26);

    // The fewest rows of a part when the strings of a column are numbered.
    private const int PartRows = 1 << 14;

    /// <summary>
    /// Numbers for the distinct strings of a column, by open addressing over
    /// the strings' hashes. A string's number is the count of distinct
    /// strings before its first row.
    /// </summary>
    private sealed class StringIdTable(StringArray s)
    {
        public readonly struct Entry(int first, int hash, int len, ulong w0, ulong w1)
        {
            public readonly int First = first;
            public readonly int Hash = hash;
            public readonly int Len = len;
            public readonly ulong W0 = w0;
            public readonly ulong W1 = w1;
        }

        public readonly List<Entry> Entries = new();
        // 256 slots at first, 1 KB: two of a few strings then share a home
        // slot less often (4 strings: about 2 % of hashes against 9 % in
        // 64), and a few strings are most of what a column of categories
        // has. Each row of such a column is a hit, so a shared slot costs a
        // probe more on a part of every column's rows.
        private int _mask = 255;
        private int[] _table = Filled(256);

        // The column's offsets, bytes and valid bits, read directly: through
        // Arrow's GetBytes and IsNull each row was a call that also tested
        // the row's bit.
        private readonly ReadOnlyMemory<byte> _offsets = s.ValueOffsetsBuffer.Memory;
        private readonly ReadOnlyMemory<byte> _bytes = s.ValueBuffer.Memory;
        private readonly int _offset = s.Offset;

        private ReadOnlySpan<int> Offsets => MemoryMarshal.Cast<byte, int>(_offsets.Span).Slice(_offset);

        private static int[] Filled(int n)
        {
            var t = new int[n];
            System.Array.Fill(t, -1);
            return t;
        }

        // Two 8-byte words that cover all bytes of a string of at most 16
        // bytes (its first 8 and last 8), and a fast multiply-xor hash of
        // them: a hit on a string of at most 16 bytes compares the two words
        // in registers without reading the column's offsets or bytes again.
        //
        // A product carries a change of a bit only to higher bits, and the
        // bytes that differ between strings such as "region-12345" are the
        // high bytes of w1. So w1's product is rotated before the two words
        // meet, and the sum is multiplied once more after its high half is
        // folded in. The low bits pick the slot and the top 6 bits the
        // bucket of a merge (Bucket), so both have to depend on every byte.
        // Before, w1 was xored in before one multiply only: 20 000 strings
        // "region-N" took 218 of 65 536 slots, 49 probes a row, and a
        // categorize of the wide file took 124 ms on one thread.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (int Hash, ulong W0, ulong W1) Key(ref byte p, int len)
        {
            ulong w0, w1;
            if (len >= 8)
            {
                w0 = Unsafe.ReadUnaligned<ulong>(ref p);
                w1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, len - 8));
            }
            else if (len >= 4)
            {
                w0 = Unsafe.ReadUnaligned<uint>(ref p) | ((ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, len - 4)) << 32);
                w1 = 0;
            }
            else if (len > 0)
            {
                w0 = p | ((ulong)Unsafe.Add(ref p, len >> 1) << 8) | ((ulong)Unsafe.Add(ref p, len - 1) << 16);
                w1 = 0;
            }
            else
            {
                w0 = 0;
                w1 = 0;
            }
            ulong h = ((w0 ^ (ulong)len) * 0x9E3779B97F4A7C15UL)
                      ^ System.Numerics.BitOperations.RotateLeft(w1 * 0xBF58476D1CE4E5B9UL, 32);
            h = (h ^ (h >> 32)) * 0x9E3779B97F4A7C15UL;
            return ((int)(h ^ (h >> 32)), w0, w1);
        }

        /// <summary>Numbers the rows from..to into ids; a null is left as it is.</summary>
        public void Number(int from, int to, Span<int> ids)
        {
            var offsets = Offsets;
            var bytes = _bytes.Span;
            ref int offRef = ref MemoryMarshal.GetReference(offsets);
            ref byte byteRef = ref MemoryMarshal.GetReference(bytes);
            var valid = s.NullCount == 0 ? ReadOnlySpan<byte>.Empty : s.NullBitmapBuffer.Span;
            if (valid.IsEmpty)
            {
                for (int r = from; r < to; r++)
                {
                    int start = Unsafe.Add(ref offRef, r);
                    int len = Unsafe.Add(ref offRef, r + 1) - start;
                    var (h, w0, w1) = Key(ref Unsafe.Add(ref byteRef, start), len);
                    ids[r] = IdOf(r, h, len, w0, w1, start, ref offRef, bytes);
                }
            }
            else
            {
                for (int r = from; r < to; r++)
                {
                    int bit = _offset + r;
                    if ((valid[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                    int start = Unsafe.Add(ref offRef, r);
                    int len = Unsafe.Add(ref offRef, r + 1) - start;
                    var (h, w0, w1) = Key(ref Unsafe.Add(ref byteRef, start), len);
                    ids[r] = IdOf(r, h, len, w0, w1, start, ref offRef, bytes);
                }
            }
        }

        /// <summary>
        /// The hash of each valid row from..to into <paramref name="hashes"/>,
        /// and the count of those rows in each bucket (<see cref="HashBucket"/>)
        /// into <paramref name="counts"/>.
        /// </summary>
        public static void HashRows(StringArray s, int from, int to, int[] hashes, Span<int> counts)
        {
            var offsets = MemoryMarshal.Cast<byte, int>(s.ValueOffsetsBuffer.Span).Slice(s.Offset);
            ref int offRef = ref MemoryMarshal.GetReference(offsets);
            ref byte byteRef = ref MemoryMarshal.GetReference(s.ValueBuffer.Span);
            var valid = s.NullCount == 0 ? ReadOnlySpan<byte>.Empty : s.NullBitmapBuffer.Span;
            int offset = s.Offset;
            for (int r = from; r < to; r++)
            {
                if (!valid.IsEmpty)
                {
                    int bit = offset + r;
                    if ((valid[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                }
                int start = Unsafe.Add(ref offRef, r);
                int len = Unsafe.Add(ref offRef, r + 1) - start;
                int h = Key(ref Unsafe.Add(ref byteRef, start), len).Hash;
                hashes[r] = h;
                counts[HashBucket(h)]++;
            }
        }

        /// <summary>
        /// Numbers the rows given, which are valid, in their order: the
        /// number of rows[i] into ids[i].
        /// </summary>
        public void NumberRows(ReadOnlySpan<int> rows, Span<int> ids)
        {
            var offsets = Offsets;
            var bytes = _bytes.Span;
            ref int offRef = ref MemoryMarshal.GetReference(offsets);
            ref byte byteRef = ref MemoryMarshal.GetReference(bytes);
            for (int i = 0; i < rows.Length; i++)
            {
                int r = rows[i];
                int start = Unsafe.Add(ref offRef, r);
                int len = Unsafe.Add(ref offRef, r + 1) - start;
                var (h, w0, w1) = Key(ref Unsafe.Add(ref byteRef, start), len);
                ids[i] = IdOf(r, h, len, w0, w1, start, ref offRef, bytes);
            }
        }

        /// <summary>The number of the string of <paramref name="entry"/>; a new one when it is new.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IdOf(in Entry entry)
        {
            int i = entry.Hash & _mask, id;
            var entries = CollectionsMarshal.AsSpan(Entries);
            while ((id = _table[i]) >= 0)
            {
                ref readonly var e = ref entries[id];
                if (e.Len == entry.Len && e.W0 == entry.W0 && e.W1 == entry.W1)
                {
                    if (entry.Len <= 16 || SameMiddle(e.First, entry.First, entry.Len)) return id;
                }
                i = (i + 1) & _mask;
            }
            return Insert(i, entry);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int IdOf(int r, int h, int len, ulong w0, ulong w1, int start, ref int offRef, ReadOnlySpan<byte> bytes)
        {
            int i = h & _mask, id;
            var entries = CollectionsMarshal.AsSpan(Entries);
            while ((id = _table[i]) >= 0)
            {
                ref readonly var e = ref entries[id];
                if (e.Len == len && e.W0 == w0 && e.W1 == w1)
                {
                    if (len <= 16) return id;
                    int firstStart = Unsafe.Add(ref offRef, e.First);
                    if (bytes.Slice(firstStart + 8, len - 16).SequenceEqual(bytes.Slice(start + 8, len - 16)))
                        return id;
                }
                i = (i + 1) & _mask;
            }
            return Insert(i, new Entry(r, h, len, w0, w1));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool SameMiddle(int firstA, int firstB, int len)
        {
            var offsets = Offsets;
            var bytes = _bytes.Span;
            return bytes.Slice(offsets[firstA] + 8, len - 16).SequenceEqual(bytes.Slice(offsets[firstB] + 8, len - 16));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int Insert(int slot, Entry entry)
        {
            int id = Entries.Count;
            Entries.Add(entry);
            _table[slot] = id;
            if (Entries.Count * 2 > _mask)
            {
                // Twice the room, and each id where its hash puts it.
                _mask = _mask * 2 + 1;
                _table = Filled(_mask + 1);
                var entries = CollectionsMarshal.AsSpan(Entries);
                for (int k = 0; k < entries.Length; k++)
                {
                    int j = entries[k].Hash & _mask;
                    while (_table[j] >= 0) j = (j + 1) & _mask;
                    _table[j] = k;
                }
            }
            return id;
        }
    }

    // The validity bitmap of the column from row 0, or empty with no nulls.
    private static ArrowBuffer Validity(IArrowArray a)
    {
        if (a.NullCount == 0) return ArrowBuffer.Empty;
        if (a.Offset == 0 && !a.Data.Buffers[0].IsEmpty) return a.Data.Buffers[0];
        var valid = a.Data.Buffers[0].Span;
        int offset = a.Offset;
        var bits = new byte[(a.Length + 7) / 8];
        for (int r = 0; r < a.Length; r++)
        {
            int bit = offset + r;
            if ((valid[bit >> 3] & (1 << (bit & 7))) != 0) bits[r >> 3] |= (byte)(1 << (r & 7));
        }
        return new ArrowBuffer(bits);
    }

    internal static DictionaryArray Make(ArrowBuffer codes, ArrowBuffer validity, int nulls, int n, IReadOnlyList<string> levels)
    {
        var dictionary = new StringArray.Builder().AppendRange(levels).Build();
        var data = new ArrayData(Type, n, nulls, 0, new[] { validity, codes }, null, dictionary.Data);
        return new DictionaryArray(data);
    }

    /// <summary>The strings of a category, as a column of strings.</summary>
    private static StringArray Strings(DictionaryArray d)
    {
        var levels = (StringArray)d.Dictionary;
        var codes = ((Int32Array)d.Indices).Values;
        var b = new StringArray.Builder();
        for (int r = 0; r < d.Length; r++)
        {
            if (d.IsNull(r)) b.AppendNull();
            else b.Append(levels.GetString(codes[r]));
        }
        return b.Build();
    }

    /// <summary>The levels of a category, in order.</summary>
    public static string[] Levels(IArrowArray a)
    {
        var levels = (StringArray)((DictionaryArray)a).Dictionary;
        return Enumerable.Range(0, levels.Length).Select(i => levels.GetString(i)).ToArray();
    }

    /// <summary>The codes of a category: a column of ints, with its nulls.</summary>
    public static IArrowArray Codes(IArrowArray a) => ((DictionaryArray)a).Indices;

    /// <summary>The level of row <paramref name="i"/>, which is not null.</summary>
    internal static ReadOnlySpan<byte> LevelBytes(DictionaryArray d, int i) =>
        ((StringArray)d.Dictionary).GetBytes(((Int32Array)d.Indices).Values[i]);

    /// <summary>
    /// The categories one after the other. When they have one set of levels,
    /// the codes are copied; otherwise the levels are those of the first, then
    /// the new ones of each next column, in order, as R's rbind of factors
    /// gives them, and each code is that of its level there.
    /// </summary>
    internal static IArrowArray Concat(List<IArrowArray> pieces, int n, int nulls, ArrowBuffer validity)
    {
        var dicts = pieces.Select(p => (StringArray)((DictionaryArray)p).Dictionary).ToList();
        var codes = new byte[n * sizeof(int)];
        var span = MemoryMarshal.Cast<byte, int>(codes.AsSpan());
        List<string> levels;
        if (dicts.All(d => SameLevels(d, dicts[0])))
        {
            levels = Enumerable.Range(0, dicts[0].Length).Select(i => dicts[0].GetString(i)).ToList();
            int at = 0;
            foreach (var p in pieces)
            {
                ((Int32Array)((DictionaryArray)p).Indices).Values.CopyTo(span.Slice(at));
                at += p.Length;
            }
        }
        else
        {
            levels = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            int at = 0;
            for (int k = 0; k < pieces.Count; k++)
            {
                var d = dicts[k];
                var map = new int[d.Length];
                for (int l = 0; l < d.Length; l++)
                {
                    var text = d.GetString(l);
                    if (!index.TryGetValue(text, out map[l]))
                    {
                        map[l] = levels.Count;
                        index[text] = levels.Count;
                        levels.Add(text);
                    }
                }
                var p = (DictionaryArray)pieces[k];
                var from = ((Int32Array)p.Indices).Values;
                for (int r = 0; r < p.Length; r++, at++)
                    span[at] = p.IsNull(r) ? 0 : map[from[r]];
            }
        }
        return Make(new ArrowBuffer(codes), validity, nulls, n, levels);
    }

    private static bool SameLevels(StringArray a, StringArray b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a.GetBytes(i).SequenceEqual(b.GetBytes(i))) return false;
        return true;
    }

    /// <summary>A category of n nulls, with no levels.</summary>
    internal static IArrowArray Nulls(int n) =>
        Make(new ArrowBuffer(new byte[n * sizeof(int)]), new ArrowBuffer(new byte[(n + 7) / 8]), n, n, System.Array.Empty<string>());
}
