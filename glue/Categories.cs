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
        int i = t.Schema.GetFieldIndex(name);
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
        var ids = StringIds(s, out var firsts);

        // The level of each distinct string.
        string[] levels;
        var levelOf = new int[firsts.Count];
        if (given.Length == 0)
        {
            var byBytes = InByteOrder(s, firsts);
            levels = byBytes.Select(id => s.GetString(firsts[id])).ToArray();
            for (int l = 0; l < byBytes.Length; l++) levelOf[byBytes[l]] = l;
        }
        else
        {
            levels = given;
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int l = 0; l < given.Length; l++) index[given[l]] = l;
            for (int id = 0; id < firsts.Count; id++)
            {
                var text = s.GetString(firsts[id]);
                if (!index.TryGetValue(text, out levelOf[id]))
                    throw new TableProblem($"'{text}' in row {firsts[id]} of '{name}' is not one of the levels.");
            }
        }

        // The code of each row, 0 at a null, in parts on the workers, the
        // null bits read from the bitmap itself.
        var codes = new byte[n * sizeof(int)];
        int offset = s.Offset;
        Workers.For((n + PartRows - 1) / PartRows, p =>
        {
            var span = MemoryMarshal.Cast<byte, int>(codes.AsSpan());
            var valid = s.NullCount == 0 ? ReadOnlySpan<byte>.Empty : s.NullBitmapBuffer.Span;
            for (int r = p * PartRows, to = Math.Min(n, r + PartRows); r < to; r++)
            {
                int bit = offset + r;
                span[r] = !valid.IsEmpty && (valid[bit >> 3] & (1 << (bit & 7))) == 0 ? 0 : levelOf[ids[r]];
            }
        });
        return Make(new ArrowBuffer(codes), Validity(s), s.NullCount, n, levels);
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
        var ids = StringIds(s, out var firsts);
        var levels = new string[firsts.Count];
        for (int k = 0; k < levels.Length; k++) levels[k] = s.GetString(firsts[k]);
        var codes = MemoryMarshal.AsBytes(ids.AsSpan()).ToArray();
        return Make(new ArrowBuffer(codes), Validity(s), s.NullCount, n, levels);
    }

    /// <summary>
    /// A number for each distinct string, from 0 in the order of their first
    /// rows, and the id of each row; 0 at a null. <paramref name="firsts"/>
    /// gets the first row of each id. The table is open addressing over the
    /// strings' hashes, each kept, with no call through an interface. A long
    /// column is numbered in parts on bjostat's workers.
    /// </summary>
    internal static int[] StringIds(StringArray s, out List<int> firsts)
    {
        int n = s.Length;
        var ids = new int[n];
        int parts = Math.Min(n / PartRows, 4 * Workers.Count);
        if (parts < 2)
        {
            var one = new StringIdTable(s);
            one.Number(0, n, ids);
            firsts = one.Firsts;
            return ids;
        }
        // Each part numbers its rows on a worker; then the parts' strings are
        // numbered for the whole column, the parts in order, so that the
        // numbers are in the order of the strings' first rows; then each
        // part's rows get those numbers, on the workers again.
        int size = (n + parts - 1) / parts;
        var tables = new StringIdTable[parts];
        Workers.For(parts, p =>
        {
            var t = new StringIdTable(s);
            t.Number(p * size, Math.Min(n, (p + 1) * size), ids);
            tables[p] = t;
        });
        var maps = Merged(s, tables, out firsts);
        bool nulls = s.NullCount != 0;
        Workers.For(parts, p =>
        {
            var map = maps[p];
            for (int r = p * size, to = Math.Min(n, (p + 1) * size); r < to; r++)
                if (!nulls || s.IsValid(r)) ids[r] = map[ids[r]];
        });
        return ids;
    }

    /// <summary>
    /// The numbers of the strings whose first rows are <paramref name="firsts"/>,
    /// in the order of the strings' bytes.
    /// </summary>
    /// <remarks>
    /// Each string is first a number of its first 8 bytes, the first byte
    /// highest and zeros after a shorter string, whose order is that of the
    /// bytes; two strings with the same 8 bytes are compared by all their
    /// bytes, read from the column's buffer. A comparison that asked Arrow
    /// for both strings' bytes each time took 10 ms of the 32 of a
    /// categorize of 20 000 regions.
    /// </remarks>
    private static int[] InByteOrder(StringArray s, List<int> firsts)
    {
        int k = firsts.Count;
        var offsets = s.ValueOffsets;
        var bytes = s.ValueBuffer.Span;
        var start = new int[k];
        var length = new int[k];
        var keys = new (ulong Prefix, int Id)[k];
        for (int id = 0; id < k; id++)
        {
            int r = firsts[id];
            start[id] = offsets[r];
            length[id] = offsets[r + 1] - offsets[r];
            ulong prefix = 0;
            int take = Math.Min(8, length[id]);
            for (int i = 0; i < take; i++) prefix |= (ulong)bytes[start[id] + i] << (56 - 8 * i);
            keys[id] = (prefix, id);
        }
        var memory = s.ValueBuffer.Memory;
        System.Array.Sort(keys, (a, b) =>
        {
            int c = a.Prefix.CompareTo(b.Prefix);
            if (c != 0) return c;
            var all = memory.Span;
            return all.Slice(start[a.Id], length[a.Id]).SequenceCompareTo(all.Slice(start[b.Id], length[b.Id]));
        });
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
    /// <remarks>
    /// The strings of all the parts are put in buckets by their hashes, and
    /// each bucket is merged on a worker, the parts in their order, so that
    /// the first time a string is met is its first row: the parts are in the
    /// order of the rows, and a part's strings are in the order of their first
    /// rows. A string is in one bucket, so each bucket writes other places of
    /// the maps. Then the strings of all the buckets are numbered in the
    /// order of their first rows. Merged on one thread, the strings of 20 000
    /// regions in 15 parts took 10.5 ms of the 32 of a categorize.
    /// </remarks>
    private static int[][] Merged(StringArray s, StringIdTable[] tables, out List<int> firsts)
    {
        int parts = tables.Length;
        var maps = new int[parts][];
        // The strings of each part in each bucket, in the part's order.
        var inBucket = new List<int>[parts][];
        Workers.For(parts, p =>
        {
            var t = tables[p];
            maps[p] = new int[t.Firsts.Count];
            var lists = new List<int>[MergeBuckets];
            for (int b = 0; b < MergeBuckets; b++) lists[b] = new List<int>();
            for (int k = 0; k < t.Hashes.Count; k++) lists[Bucket(t.Hashes[k])].Add(k);
            inBucket[p] = lists;
        });
        var merged = new StringIdTable[MergeBuckets];
        Workers.For(MergeBuckets, b =>
        {
            var table = new StringIdTable(s);
            for (int p = 0; p < parts; p++)
            {
                var t = tables[p];
                var map = maps[p];
                foreach (int k in inBucket[p][b]) map[k] = table.IdOf(t.Firsts[k], t.Hashes[k]);
            }
            merged[b] = table;
        });
        // The strings of all the buckets in the order of their first rows,
        // each row once: that order gives their numbers.
        int count = 0;
        foreach (var table in merged) count += table.Firsts.Count;
        var order = new long[count];
        var places = new int[count];
        for (int b = 0, i = 0; b < MergeBuckets; b++)
            for (int e = 0; e < merged[b].Firsts.Count; e++, i++)
            {
                order[i] = merged[b].Firsts[e];
                places[i] = (b << 24) | e;
            }
        System.Array.Sort(order, places);
        var number = new int[MergeBuckets][];
        for (int b = 0; b < MergeBuckets; b++) number[b] = new int[merged[b].Firsts.Count];
        firsts = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            number[places[i] >> 24][places[i] & 0xFF_FFFF] = i;
            firsts.Add((int)order[i]);
        }
        Workers.For(parts, p =>
        {
            var t = tables[p];
            var map = maps[p];
            for (int k = 0; k < map.Length; k++) map[k] = number[Bucket(t.Hashes[k])][map[k]];
        });
        return maps;
    }

    private static int Bucket(int hash) => (int)((uint)hash >> 26);

    // The fewest rows of a part when the strings of a column are numbered.
    private const int PartRows = 1 << 16;

    /// <summary>
    /// Numbers for the distinct strings of a column, by open addressing over
    /// the strings' hashes. A string's number is the count of distinct
    /// strings before its first row.
    /// </summary>
    private sealed class StringIdTable(StringArray s)
    {
        public readonly List<int> Firsts = new();
        public readonly List<int> Hashes = new();
        private int _mask = 63;
        private int[] _table = Filled(64);

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

        public static int Hash(ReadOnlySpan<byte> v)
        {
            var hc = new HashCode();
            hc.AddBytes(v);
            return hc.ToHashCode();
        }

        /// <summary>Numbers the rows from..to into ids; a null is left as it is.</summary>
        public void Number(int from, int to, int[] ids)
        {
            var offsets = Offsets;
            var bytes = _bytes.Span;
            var valid = s.NullCount == 0 ? ReadOnlySpan<byte>.Empty : s.NullBitmapBuffer.Span;
            for (int r = from; r < to; r++)
            {
                if (!valid.IsEmpty)
                {
                    int bit = _offset + r;
                    if ((valid[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                }
                int start = offsets[r];
                var v = bytes.Slice(start, offsets[r + 1] - start);
                ids[r] = IdOf(r, Hash(v), v, offsets, bytes);
            }
        }

        /// <summary>The number of the string of row r, whose hash is h; a new one when it is new.</summary>
        public int IdOf(int r, int h)
        {
            var offsets = Offsets;
            var bytes = _bytes.Span;
            int start = offsets[r];
            return IdOf(r, h, bytes.Slice(start, offsets[r + 1] - start), offsets, bytes);
        }

        private int IdOf(int r, int h, ReadOnlySpan<byte> v, ReadOnlySpan<int> offsets, ReadOnlySpan<byte> bytes)
        {
            int i = h & _mask, id;
            while ((id = _table[i]) >= 0)
            {
                if (Hashes[id] == h)
                {
                    int first = Firsts[id], start = offsets[first];
                    if (bytes.Slice(start, offsets[first + 1] - start).SequenceEqual(v)) return id;
                }
                i = (i + 1) & _mask;
            }
            id = Firsts.Count;
            Firsts.Add(r);
            Hashes.Add(h);
            _table[i] = id;
            if (Firsts.Count * 2 > _mask)
            {
                // Twice the room, and each id where its hash puts it.
                _mask = _mask * 2 + 1;
                _table = Filled(_mask + 1);
                for (int k = 0; k < Hashes.Count; k++)
                {
                    int j = Hashes[k] & _mask;
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
        var bits = new byte[(a.Length + 7) / 8];
        for (int r = 0; r < a.Length; r++)
            if (a.IsValid(r)) bits[r >> 3] |= (byte)(1 << (r & 7));
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
