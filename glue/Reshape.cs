/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Reshape: tables made of the rows or the columns of others. Distinct keeps
// the first row of each key, Append puts tables one after the other, and the
// two pivots change columns to rows and rows to columns, as tidyr's
// pivot_longer and pivot_wider.
//
// A table that cannot be made is a TableProblem, whose message
// (bjostat table) gives as an Err. Other exceptions are errors in this code.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Bjostat;

/// <summary>A table that the arguments cannot make; the message says why.</summary>
public sealed class TableProblem(string message) : Exception(message);

public static class Reshape
{
    // ---------------------------------------------------------------------
    // Groups of rows with equal keys

    /// <summary>
    /// The group of each row, by its keys: the groups are numbered in the
    /// order of their first rows. <paramref name="firsts"/> gets the first row
    /// of each group. Two nulls and two NaNs are equal keys, and 0.0 and
    /// -0.0, as in a join.
    /// </summary>
    /// <remarks>
    /// Each column gives each row an id of its value first, by the fastest
    /// way for its type (see <see cref="Ids"/>), and the ids of the columns
    /// are then put together as the digits of one number, and numbered again
    /// from 0: through an array while they are few, and a dictionary past
    /// that. When each row has a group of its own, the other keys are not
    /// read. No row's keys are hashed together, and no two rows are compared.
    /// </remarks>
    internal static int[] Groups(IArrowArray[] keys, int n, out List<int> firsts) =>
        Groups(keys, n, out firsts, out _, false);

    internal static int[] Groups(
        IArrowArray[] keys,
        int n,
        out List<int> firsts,
        out (int[] Ids, List<int> Firsts)?[] stringIds,
        bool keepStringIds = true,
        bool needGroupIds = true)
    {
        stringIds = new (int[] Ids, List<int> Firsts)?[keys.Length];
        if (keys.Length == 0)
        {
            firsts = n == 0 ? new List<int>() : new List<int> { 0 };
            return new int[n];
        }

        var cur = Ids(keys[0], out int c0, out var firsts0, out stringIds[0]);
        long card;
        if (firsts0 != null)
        {
            firsts = firsts0;
            card = firsts.Count;
        }
        else
        {
            bool needCur = needGroupIds || keys.Length > 1;
            if (needCur && keepStringIds && stringIds[0] != null) cur = (int[])cur.Clone();
            card = Compact(cur, c0, needCur, out firsts);
        }

        // Once each row has a group of its own, no key splits one more.
        for (int k = 1; k < keys.Length && card < n; k++)
        {
            bool needCur = needGroupIds || k + 1 < keys.Length;
            var ids = Ids(keys[k], out int ck, out _, out stringIds[k]);
            if (k == 1 && keepStringIds && ReferenceEquals(cur, stringIds[0]?.Ids))
            {
                var next = GC.AllocateUninitializedArray<int>(n);
                cur.AsSpan().CopyTo(next);
                cur = next;
            }
            if (card * ck <= DenseLimit)
            {
                int stride = ck;
                int totalCard = (int)(card * ck);
                if (totalCard <= ParallelCompactLimit && n >= 2 * PartRows)
                {
                    int parts = Math.Min(n / PartRows, Workers.Count);
                    int size = (n + parts - 1) / parts;
                    var curArr = cur;
                    Workers.For(parts, p =>
                    {
                        for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
                            curArr[r] = curArr[r] * stride + ids[r];
                    });
                    card = CompactParallel(cur, totalCard, parts, size, needCur, out firsts);
                }
                else
                {
                    var number = new int[totalCard];
                    System.Array.Fill(number, -1);
                    firsts = new List<int>((int)card);
                    for (int r = 0; r < n; r++)
                    {
                        ref int id = ref number[cur[r] * stride + ids[r]];
                        if (id < 0)
                        {
                            id = firsts.Count;
                            firsts.Add(r);
                        }
                        cur[r] = id;
                    }
                    card = firsts.Count;
                }
            }
            else
            {
                var number = new Dictionary<long, int>((int)card);
                firsts = new List<int>((int)card);
                for (int r = 0; r < n; r++)
                {
                    ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(number, (long)cur[r] * ck + ids[r], out bool there);
                    if (!there)
                    {
                        id = firsts.Count;
                        firsts.Add(r);
                    }
                    cur[r] = id;
                }
                card = firsts.Count;
            }
        }
        return cur;
    }

    // The ids numbered again from 0, in the order of their first rows, so
    // that there are as many as there are distinct ones. Answers how many.
    private static int Compact(int[] ids, long card, bool needIds, out List<int> firsts)
    {
        int n = ids.Length;
        if (card <= ParallelCompactLimit && n >= 2 * PartRows)
        {
            int parts = Math.Min(n / PartRows, Workers.Count);
            return CompactParallel(ids, (int)card, parts, (n + parts - 1) / parts, needIds, out firsts);
        }
        var number = new int[card];
        System.Array.Fill(number, -1);
        firsts = new List<int>();
        for (int r = 0; r < n; r++)
        {
            ref int id = ref number[ids[r]];
            if (id < 0)
            {
                id = firsts.Count;
                firsts.Add(r);
            }
            if (needIds) ids[r] = id;
        }
        return firsts.Count;
    }

    private static int CompactParallel(int[] ids, int card, int parts, int size, bool needIds, out List<int> firsts)
    {
        int n = ids.Length;
        var firstSeen = GC.AllocateUninitializedArray<int>(card);
        if (card >= PartRows)
        {
            int fillSize = (card + parts - 1) / parts;
            Workers.For(parts, p =>
            {
                int from = p * fillSize, to = Math.Min(card, from + fillSize);
                if (from < to) firstSeen.AsSpan(from, to - from).Fill(int.MaxValue);
            });
        }
        else
        {
            System.Array.Fill(firstSeen, int.MaxValue);
        }
        Workers.For(parts, p =>
        {
            for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
            {
                int v = ids[r];
                if (r < firstSeen[v]) InterlockedMin(ref firstSeen[v], r);
            }
        });
        var partCounts = new int[parts];
        Workers.For(parts, p =>
        {
            int c = 0;
            for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
                if (firstSeen[ids[r]] == r) c++;
            partCounts[p] = c;
        });
        int count = 0;
        for (int p = 0; p < parts; p++)
        {
            int c = partCounts[p];
            partCounts[p] = count;
            count += c;
        }
        var firstList = new List<int>(count);
        CollectionsMarshal.SetCount(firstList, count);
        Workers.For(parts, p =>
        {
            var firstSpan = CollectionsMarshal.AsSpan(firstList);
            int id = partCounts[p];
            if (needIds)
            {
                for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
                {
                    int v = ids[r];
                    if (firstSeen[v] == r)
                    {
                        firstSpan[id] = r;
                        firstSeen[v] = id;
                        id++;
                    }
                }
            }
            else
            {
                for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
                    if (firstSeen[ids[r]] == r)
                        firstSpan[id++] = r;
            }
        });
        if (needIds)
        {
            Workers.For(parts, p =>
            {
                for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
                    ids[r] = firstSeen[ids[r]];
            });
        }
        firsts = firstList;
        return count;
    }

    private static void InterlockedMin(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value < current)
        {
            int prev = Interlocked.CompareExchange(ref target, value, current);
            if (prev == current) break;
            current = prev;
        }
    }

    // The most ids put together that index an array: 16 MB of ints.
    internal const long DenseLimit = 1 << 22;
    private const int ParallelCompactLimit = 1 << 22;
    private const int PartRows = 1 << 14;

    /// <summary>
    /// For <see cref="Counts.Count"/>: an id for each row's value in the
    /// column, from 0 to <paramref name="card"/> - 1 in <c>table-sort</c>
    /// order (with nulls at <paramref name="card"/> - 1), or null when the
    /// column's values are not dense or are floats.
    /// </summary>
    internal static int[]? TrySortedIds(IArrowArray a, out int card)
    {
        int n = a.Length;
        int[]? ids;
        bool nullsDone = false;
        switch (a)
        {
            case DictionaryArray d when Categories.Is(d):
                ids = ((Int32Array)d.Indices).Values.ToArray();
                card = d.Dictionary.Length;
                break;
            case BooleanArray b:
            {
                ids = new int[n];
                var bits = b.ValueBuffer.Span;
                int off = b.Offset;
                for (int r = 0; r < n; r++)
                {
                    int bit = off + r;
                    ids[r] = (bits[bit >> 3] >> (bit & 7)) & 1;
                }
                card = 2;
                break;
            }
            case StringArray s:
                return Categories.SortedStringIds(s, out card);
            case Int64Array l: ids = TryDenseIntegers(l.Values, a, out card, out nullsDone); break;
            case Int32Array i: ids = TryDenseIntegers(i.Values, a, out card, out nullsDone); break;
            case Int16Array i: ids = TryDenseIntegers(i.Values, a, out card, out nullsDone); break;
            case UInt8Array i: ids = TryDenseIntegers(i.Values, a, out card, out nullsDone); break;
            case UInt16Array i: ids = TryDenseIntegers(i.Values, a, out card, out nullsDone); break;
            case UInt32Array i: ids = TryDenseIntegers(i.Values, a, out card, out nullsDone); break;
            case UInt64Array i: ids = TryDenseIntegers(i.Values, a, out card, out nullsDone); break;
            case Date32Array d: ids = TryDenseIntegers(d.Values, a, out card, out nullsDone); break;
            case TimestampArray t: ids = TryDenseIntegers(t.Values, a, out card, out nullsDone); break;
            default:
                card = 0;
                return null;
        }
        if (ids == null) return null;
        if (a.NullCount != 0 && !nullsDone)
        {
            var valid = a.Data.Buffers[0].Span;
            int off = a.Offset;
            for (int r = 0; r < n; r++)
            {
                int bit = off + r;
                if ((valid[bit >> 3] & (1 << (bit & 7))) == 0) ids[r] = card;
            }
            card++;
        }
        return ids;
    }

    /// <summary>
    /// An id for each row's value in the column, from 0 to
    /// <paramref name="card"/> - 1, equal for equal values; the nulls have one
    /// id. A category's id is its code, a bool's 0 or 1, a string's from
    /// Categories.StringIds; an integer's its distance from the least when
    /// that range is small, and a number from a dictionary otherwise.
    /// </summary>
    private static int[] Ids(
        IArrowArray a,
        out int card,
        out List<int>? firsts,
        out (int[] Ids, List<int> Firsts)? stringIds)
    {
        int n = a.Length;
        int[] ids;
        firsts = null;
        stringIds = null;
        bool nullsDone = false;
        switch (a)
        {
            case DictionaryArray d when Categories.Is(d):
                ids = ((Int32Array)d.Indices).Values.ToArray();
                card = d.Dictionary.Length;
                break;
            case BooleanArray b:
            {
                ids = new int[n];
                var bits = b.ValueBuffer.Span;
                int off = b.Offset;
                for (int r = 0; r < n; r++)
                {
                    int bit = off + r;
                    ids[r] = (bits[bit >> 3] >> (bit & 7)) & 1;
                }
                card = 2;
                break;
            }
            case StringArray s:
                ids = Categories.StringIds(s, out var strFirsts);
                card = strFirsts.Count;
                stringIds = (ids, strFirsts);
                if (s.NullCount == 0) firsts = strFirsts;
                break;
            case Int64Array l: ids = Integers(l.Values, a, out card, out firsts, out nullsDone); break;
            case Int32Array i: ids = Integers(i.Values, a, out card, out firsts, out nullsDone); break;
            case Int16Array i: ids = Integers(i.Values, a, out card, out firsts, out nullsDone); break;
            case UInt8Array i: ids = Integers(i.Values, a, out card, out firsts, out nullsDone); break;
            case UInt16Array i: ids = Integers(i.Values, a, out card, out firsts, out nullsDone); break;
            case UInt32Array i: ids = Integers(i.Values, a, out card, out firsts, out nullsDone); break;
            case UInt64Array i: ids = Integers(i.Values, a, out card, out firsts, out nullsDone); break;
            case Date32Array d: ids = Integers(d.Values, a, out card, out firsts, out nullsDone); break;
            case TimestampArray t: ids = Integers(t.Values, a, out card, out firsts, out nullsDone); break;
            case DoubleArray d: ids = Numbered(d.Values, a, out card, out firsts); break;
            case FloatArray f: ids = Numbered(f.Values, a, out card, out firsts); break;
            default:
                throw new ArgumentException($"(bjostat table): a column of {a.Data.DataType.Name} cannot be a key");
        }
        // The nulls: one id after the others.
        if (a.NullCount != 0 && !nullsDone)
        {
            var valid = a.Data.Buffers[0].Span;
            int off = a.Offset;
            for (int r = 0; r < n; r++)
            {
                int bit = off + r;
                if ((valid[bit >> 3] & (1 << (bit & 7))) == 0) ids[r] = card;
            }
            card++;
            firsts = null;
        }
        return ids;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LongValue<T>(T x) where T : struct, INumber<T> =>
        typeof(T) == typeof(ulong) ? unchecked((long)ulong.CreateTruncating(x)) : long.CreateTruncating(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long KeyBits<T>(T x) where T : struct, INumber<T>
    {
        if (typeof(T) == typeof(double))
        {
            double d = double.CreateTruncating(x);
            return double.IsNaN(d) ? 0x7FF8_0000_0000_0001L : BitConverter.DoubleToInt64Bits(d == 0.0 ? 0.0 : d);
        }
        if (typeof(T) == typeof(float))
        {
            float f = float.CreateTruncating(x);
            return float.IsNaN(f) ? 0x7FC0_0001L : BitConverter.SingleToInt32Bits(f == 0.0f ? 0.0f : f);
        }
        return LongValue(x);
    }

    // The distance of each value from the least, when the values span fewer
    // than DenseLimit and fewer than four times the rows; otherwise numbers
    // from a dictionary. The values under nulls are left out of the range.
    private static int[] Integers<T>(
        ReadOnlySpan<T> values,
        IArrowArray a,
        out int card,
        out List<int>? firsts,
        out bool nullsDone)
        where T : struct, INumber<T>
    {
        firsts = null;
        var dense = TryDenseIntegers(values, a, out card, out nullsDone);
        if (dense != null) return dense;
        return Numbered(values, a, out card, out firsts);
    }

    private static int[]? TryDenseIntegers<T>(
        ReadOnlySpan<T> values,
        IArrowArray a,
        out int card,
        out bool nullsDone)
        where T : struct, INumber<T>
    {
        int n = values.Length;
        nullsDone = false;
        long least = long.MaxValue, most = long.MinValue;
        int parts = n < 2 * PartRows ? 1 : Math.Min(n / PartRows, Workers.Count);
        if (parts >= 2)
        {
            int size = (n + parts - 1) / parts;
            var mins = new long[parts];
            var maxs = new long[parts];
            var valMem = a.Data.Buffers[1].Memory;
            int valOff = a.Data.Offset;
            var validMem = a.NullCount == 0 ? ReadOnlyMemory<byte>.Empty : a.Data.Buffers[0].Memory;
            Workers.For(parts, p =>
            {
                var span = MemoryMarshal.Cast<byte, T>(valMem.Span).Slice(valOff);
                var valid = validMem.Span;
                long lo = long.MaxValue, hi = long.MinValue;
                int from = p * size, to = Math.Min(n, from + size);
                if (valid.IsEmpty)
                {
                    for (int r = from; r < to; r++)
                    {
                        long v = LongValue(span[r]);
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
                }
                else
                {
                    for (int r = from; r < to; r++)
                    {
                        int bit = valOff + r;
                        if ((valid[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                        long v = LongValue(span[r]);
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
                }
                mins[p] = lo;
                maxs[p] = hi;
            });
            for (int p = 0; p < parts; p++)
            {
                if (mins[p] < least) least = mins[p];
                if (maxs[p] > most) most = maxs[p];
            }
        }
        else if (a.NullCount == 0)
        {
            for (int r = 0; r < n; r++)
            {
                long v = LongValue(values[r]);
                if (v < least) least = v;
                if (v > most) most = v;
            }
        }
        else
        {
            var valid = a.Data.Buffers[0].Span;
            int off = a.Offset;
            for (int r = 0; r < n; r++)
            {
                int bit = off + r;
                if ((valid[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                long v = LongValue(values[r]);
                if (v < least) least = v;
                if (v > most) most = v;
            }
        }
        if (least > most)
        {
            card = 0;
            return new int[n];
        }
        if ((ulong)(most - least) < (ulong)Math.Min(DenseLimit, 4L * n + 64))
        {
            int c = (int)(most - least + 1);
            var ids = GC.AllocateUninitializedArray<int>(n);
            if (parts >= 2)
            {
                int size = (n + parts - 1) / parts;
                var valMem = a.Data.Buffers[1].Memory;
                int valOff = a.Data.Offset;
                var validMem = a.NullCount == 0 ? ReadOnlyMemory<byte>.Empty : a.Data.Buffers[0].Memory;
                long baseVal = least;
                Workers.For(parts, p =>
                {
                    var span = MemoryMarshal.Cast<byte, T>(valMem.Span).Slice(valOff);
                    var valid = validMem.Span;
                    int from = p * size, to = Math.Min(n, from + size);
                    if (valid.IsEmpty)
                    {
                        for (int r = from; r < to; r++)
                            ids[r] = unchecked((int)(LongValue(span[r]) - baseVal));
                    }
                    else
                    {
                        for (int r = from; r < to; r++)
                        {
                            int bit = valOff + r;
                            ids[r] = (valid[bit >> 3] & (1 << (bit & 7))) == 0
                                ? c
                                : unchecked((int)(LongValue(span[r]) - baseVal));
                        }
                    }
                });
                nullsDone = a.NullCount != 0;
                card = nullsDone ? c + 1 : c;
                return ids;
            }
            for (int r = 0; r < n; r++)
                ids[r] = unchecked((int)(LongValue(values[r]) - least));
            card = c;
            return ids;
        }
        card = 0;
        return null;
    }

    // A number for each distinct value, in the order of their first rows.
    private static int[] Numbered<T>(ReadOnlySpan<T> values, IArrowArray a, out int card, out List<int>? firsts)
        where T : struct, INumber<T>
    {
        int n = values.Length;
        var ids = new int[n];
        var number = new Dictionary<long, int>();
        var firstRows = new List<int>();
        if (a.NullCount == 0)
        {
            for (int r = 0; r < n; r++)
            {
                ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(number, KeyBits(values[r]), out bool there);
                if (!there)
                {
                    id = firstRows.Count;
                    firstRows.Add(r);
                }
                ids[r] = id;
            }
            firsts = firstRows;
        }
        else
        {
            var valid = a.Data.Buffers[0].Span;
            int off = a.Offset;
            for (int r = 0; r < n; r++)
            {
                int bit = off + r;
                if ((valid[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(number, KeyBits(values[r]), out bool there);
                if (!there) id = number.Count - 1;
                ids[r] = id;
            }
            firsts = null;
        }
        card = number.Count;
        return ids;
    }

    internal static int Column(RecordBatch t, string name)
    {
        int i = Tables.IndexOf(t, name);
        if (i < 0)
            throw new TableProblem($"the table has no column '{name}'. Its columns are: "
                                   + string.Join(", ", t.Schema.FieldsList.Select(f => f.Name)) + ".");
        return i;
    }

    // The name of a type as (bjostat table) prints it.
    internal static string TypeText(IArrowType t) => ColumnTypes.ArrowName(t);

    // ---------------------------------------------------------------------
    // Columns put one after the other

    // The cells of the columns, one column after the other. A copy of each
    // buffer, rather than one of each cell, as ArrowArrayConcatenator makes.
    // The columns are of the type.
    internal static IArrowArray Concat(List<IArrowArray> pieces, IArrowType type)
    {
        long total = pieces.Sum(p => (long)p.Length);
        if (total > int.MaxValue) throw new TableProblem($"{total} rows are too many for one column.");
        int n = (int)total;
        int nulls = pieces.Sum(p => p.NullCount);
        var validity = nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(ConcatBits(pieces, 0, n));
        ArrayData data = type switch
        {
            StringType => ConcatStrings(pieces, type, n, nulls, validity),
            BooleanType => new ArrayData(type, n, nulls, 0, new[] { validity, new ArrowBuffer(ConcatBits(pieces, 1, n)) }),
            // Before FixedWidthType, which a DictionaryType is.
            _ when Categories.IsType(type) => Categories.Concat(pieces, n, nulls, validity).Data,
            FixedWidthType fw when fw.BitWidth % 8 == 0 => new ArrayData(type, n, nulls, 0,
                new[] { validity, new ArrowBuffer(ConcatFixed(pieces, fw.BitWidth / 8, n)) }),
            _ => ArrowArrayConcatenator.Concatenate(pieces).Data,
        };
        return ArrowArrayFactory.BuildArray(data);
    }

    private static byte[] ConcatFixed(List<IArrowArray> pieces, int width, int n)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(n * width);
        int at = 0;
        foreach (var p in pieces)
        {
            var d = p.Data;
            d.Buffers[1].Span.Slice(d.Offset * width, d.Length * width).CopyTo(bytes.AsSpan(at));
            at += d.Length * width;
        }
        return bytes;
    }

    // The bits of buffer `which` of the pieces: 0 is the validity, where a
    // piece with no bitmap is all valid, and 1 the values of booleans.
    private static byte[] ConcatBits(List<IArrowArray> pieces, int which, int n)
    {
        var bits = new byte[(n + 7) / 8];
        int at = 0;
        foreach (var p in pieces)
        {
            var d = p.Data;
            var from = d.Buffers[which];
            if (which == 0 && (d.NullCount == 0 || from.IsEmpty))
            {
                int i = 0;
                if ((at & 7) == 0 && d.Length >= 8)
                {
                    int wholeBytes = d.Length >> 3;
                    bits.AsSpan(at >> 3, wholeBytes).Fill(0xFF);
                    i = wholeBytes << 3;
                    at += i;
                }
                for (; i < d.Length; i++, at++) bits[at >> 3] |= (byte)(1 << (at & 7));
            }
            else
            {
                var span = from.Span;
                int i = 0;
                if ((at & 7) == 0 && (d.Offset & 7) == 0 && d.Length >= 8)
                {
                    int wholeBytes = d.Length >> 3;
                    span.Slice(d.Offset >> 3, wholeBytes).CopyTo(bits.AsSpan(at >> 3));
                    i = wholeBytes << 3;
                    at += i;
                }
                for (; i < d.Length; i++, at++)
                {
                    int j = d.Offset + i;
                    bits[at >> 3] |= (byte)(((span[j >> 3] >> (j & 7)) & 1) << (at & 7));
                }
            }
        }
        return bits;
    }

    private static ArrayData ConcatStrings(List<IArrowArray> pieces, IArrowType type, int n, int nulls, ArrowBuffer validity)
    {
        long size = 0;
        foreach (var p in pieces)
        {
            var o = MemoryMarshal.Cast<byte, int>(p.Data.Buffers[1].Span);
            size += o[p.Data.Offset + p.Length] - o[p.Data.Offset];
        }
        if (size > int.MaxValue) throw new TableProblem($"{size} bytes of strings are too many for one column.");
        var offsetBytes = GC.AllocateUninitializedArray<byte>((n + 1) * sizeof(int));
        var offsets = MemoryMarshal.Cast<byte, int>(offsetBytes.AsSpan());
        var bytes = GC.AllocateUninitializedArray<byte>((int)size);
        int at = 0, end = 0;
        foreach (var p in pieces)
        {
            var d = p.Data;
            var o = MemoryMarshal.Cast<byte, int>(d.Buffers[1].Span).Slice(d.Offset, d.Length + 1);
            int first = o[0], length = o[d.Length] - first, delta = end - first;
            if (delta == 0)
                o.Slice(0, d.Length).CopyTo(offsets.Slice(at));
            else
                for (int i = 0; i < d.Length; i++) offsets[at + i] = o[i] + delta;
            d.Buffers[2].Span.Slice(first, length).CopyTo(bytes.AsSpan(end));
            at += d.Length;
            end += length;
        }
        offsets[n] = end;
        return new ArrayData(type, n, nulls, 0,
            new[] { validity, new ArrowBuffer(offsetBytes), new ArrowBuffer(bytes) });
    }

    // A column of the names, all of them for each of `times` rows.
    // The names, one after the other, `times` times, as a column of strings.
    // The bytes are the names once, copied in blocks that double; the offset
    // of name j of time i is i times the bytes of all the names and the
    // offset of name j in them, made in parts on the workers.
    public static IArrowArray Repeated(string[] names, int times)
    {
        var encoded = names.Select(System.Text.Encoding.UTF8.GetBytes).ToArray();
        int period = encoded.Sum(e => e.Length), k = names.Length;
        long size = (long)period * times;
        if (size > int.MaxValue) throw new TableProblem($"{size} bytes of names are too many for one column.");
        int n = k * times;
        var bytes = GC.AllocateUninitializedArray<byte>((int)size);
        var within = new int[k];
        for (int j = 0, end = 0; j < k; j++)
        {
            within[j] = end;
            if (times > 0) encoded[j].CopyTo(bytes, end);
            end += encoded[j].Length;
        }
        for (long done = period; done < size; done *= 2)
        {
            int length = (int)Math.Min(done, size - done);
            System.Array.Copy(bytes, 0, bytes, done, length);
        }
        var offsetBytes = GC.AllocateUninitializedArray<byte>((n + 1) * sizeof(int));
        int parts = Math.Max(1, times / InterleavePart);
        Workers.For(parts, p =>
        {
            var offsets = MemoryMarshal.Cast<byte, int>(offsetBytes.AsSpan());
            int from = (int)((long)times * p / parts), to = (int)((long)times * (p + 1) / parts);
            for (int i = from; i < to; i++)
            {
                int start = i * period, at = i * k;
                for (int j = 0; j < k; j++) offsets[at + j] = start + within[j];
            }
        });
        MemoryMarshal.Cast<byte, int>(offsetBytes.AsSpan())[n] = (int)size;
        return new StringArray(n, new ArrowBuffer(offsetBytes),
                               new ArrowBuffer(bytes), ArrowBuffer.Empty, 0);
    }

    // The rows of a part of an interleaved copy: a multiple of 8, so that
    // each part writes whole bytes of the valid bitmap.
    private const int InterleavePart = 1 << 14;

    // Whether a column of the type is copied by Interleaved: numbers, dates
    // and timestamps, a value in a fixed number of whole bytes.
    public static bool Interleavable(IArrowType t) =>
        t is FixedWidthType fw && t is not BooleanType && t is not DictionaryType && fw.BitWidth % 8 == 0
        && fw.BitWidth is 8 or 16 or 32 or 64;

    /// <summary>
    /// The columns of n rows each, as one column of n times their count rows:
    /// row i of column j at i * k + j, its null too. In parts of rows on the
    /// workers. The columns are of one type that <see cref="Interleavable"/>
    /// takes.
    /// </summary>
    public static IArrowArray Interleaved(IArrowArray[] cols, int n)
    {
        var type = cols[0].Data.DataType;
        int k = cols.Length, m = n * k;
        int width = ((FixedWidthType)type).BitWidth / 8;
        var values = GC.AllocateUninitializedArray<byte>(m * width);
        int nulls = cols.Sum(c => c.NullCount);
        var valid = nulls == 0 ? null : new byte[(m + 7) / 8];
        int parts = Math.Max(1, (n + InterleavePart - 1) / InterleavePart);
        Workers.For(parts, p =>
        {
            int from = p * InterleavePart, to = Math.Min(n, from + InterleavePart);
            for (int j = 0; j < k; j++)
            {
                var data = cols[j].Data;
                var source = data.Buffers[1].Span.Slice(data.Offset * width);
                switch (width)
                {
                    case 8: Strided(MemoryMarshal.Cast<byte, long>(source), MemoryMarshal.Cast<byte, long>(values.AsSpan()), from, to, k, j); break;
                    case 4: Strided(MemoryMarshal.Cast<byte, int>(source), MemoryMarshal.Cast<byte, int>(values.AsSpan()), from, to, k, j); break;
                    case 2: Strided(MemoryMarshal.Cast<byte, short>(source), MemoryMarshal.Cast<byte, short>(values.AsSpan()), from, to, k, j); break;
                    default: Strided(source, values.AsSpan(), from, to, k, j); break;
                }
                if (valid != null)
                {
                    if (data.NullCount == 0 || data.Buffers[0].IsEmpty)
                    {
                        for (int i = from; i < to; i++)
                        {
                            int bit = i * k + j;
                            valid[bit >> 3] |= (byte)(1 << (bit & 7));
                        }
                    }
                    else
                    {
                        var srcValid = data.Buffers[0].Span;
                        int off = data.Offset;
                        for (int i = from; i < to; i++)
                        {
                            int srcBit = off + i;
                            if ((srcValid[srcBit >> 3] & (1 << (srcBit & 7))) != 0)
                            {
                                int bit = i * k + j;
                                valid[bit >> 3] |= (byte)(1 << (bit & 7));
                            }
                        }
                    }
                }
            }
        });
        var buffers = new[] { valid == null ? ArrowBuffer.Empty : new ArrowBuffer(valid), new ArrowBuffer(values) };
        return ArrowArrayFactory.BuildArray(new ArrayData(type, m, nulls, 0, buffers));
    }

    private static void Strided<T>(ReadOnlySpan<T> source, Span<T> into, int from, int to, int k, int j)
    {
        for (int i = from; i < to; i++) into[i * k + j] = source[i];
    }

    // The table of the columns at the indices, without a copy.
    public static RecordBatch Select(RecordBatch t, int[] indices)
    {
        var schema = new Schema.Builder();
        foreach (int i in indices) schema.Field(t.Schema.GetFieldByIndex(i));
        return new RecordBatch(schema.Build(), indices.Select(t.Column).ToList(), t.Length);
    }
}

/// <summary>
/// The parts of the reshaping of (bjostat table) that Bjolang calls, where a
/// method of <see cref="Reshape"/> takes or returns a List.
/// </summary>
public static class ReshapeOps
{
    /// <summary>
    /// The first row of each group of equal keys, in the order of the rows;
    /// empty when each row is a group of its own, so that a caller keeps the
    /// rows as they are without a copy of n row numbers.
    /// </summary>
    public static int[] Firsts(IArrowArray[] keys, int n)
    {
        Reshape.Groups(keys, n, out var firsts, out _, false, needGroupIds: false);
        return firsts.Count == n ? System.Array.Empty<int>() : firsts.ToArray();
    }

    /// <summary>The group of each row, and the first row of each group.</summary>
    public static int[] Groups(IArrowArray[] keys, int n, out int[] firsts)
    {
        var groups = Reshape.Groups(keys, n, out var f);
        firsts = f.ToArray();
        return groups;
    }

    /// <summary>The cells of the columns, one column after the other.</summary>
    public static IArrowArray Concat(IArrowArray[] pieces, IArrowType type) => Reshape.Concat(pieces.ToList(), type);

    /// <summary>The number of each distinct string, in the order they first come, and the first row of each.</summary>
    public static int[] StringIds(StringArray s, out int[] firsts)
    {
        var ids = Categories.StringIds(s, out var f);
        firsts = f.ToArray();
        return ids;
    }

    /// <summary>
    /// The group of each row, the first row of each group, and the groups in
    /// the order that table-sort gives their keys, ascending, a null last.
    /// </summary>
    public static int[] GroupsInOrder(IArrowArray[] keys, int n, out int[] firsts, out int[] order)
    {
        var groups = Reshape.Groups(keys, n, out var f, out var stringIds);
        order = Sorting.OrderRows(keys, new bool[keys.Length], CollectionsMarshal.AsSpan(f), stringIds, f.Count);
        firsts = f.ToArray();
        return groups;
    }

    /// <summary>
    /// An id for each row's value in the column, from 0 in the order of
    /// table-sort, a null last, and the number of ids; None when the values
    /// are not dense or are floats. A new array, which the caller may change.
    /// </summary>
    public static BjolangRuntime.Option<(int[], int)> SortedIds(IArrowArray a)
    {
        var ids = Reshape.TrySortedIds(a, out int card);
        return ids == null ? BjolangRuntime.None<(int[], int)>() : BjolangRuntime.Some((ids, card));
    }

    /// <summary>The most ids put together that index an array.</summary>
    public static long DenseLimit => Reshape.DenseLimit;

    /// <summary>The number of levels of a category, without their strings.</summary>
    public static int LevelCount(IArrowArray category) => ((DictionaryArray)category).Dictionary.Length;
}
