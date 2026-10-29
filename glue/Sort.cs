/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Sort: the rows of a table in the order of one or more of its columns.
//
// The order is that of R's `order`: by the first key, then by the next among
// the rows the first key does not tell apart, and so on. Rows that no key
// tells apart keep the order they had (the sort is stable). A null comes
// after every value, in both directions, as R puts NA last. A NaN comes after
// every number and before the nulls. Strings are in the order of their UTF-8
// bytes, which is the order of their code points.
//
// The rows are sorted by the last key first, then by the one before it, and
// so on: each pass is stable, so a pass keeps the order of the passes after
// it among the rows its key does not tell apart. Each pass is a radix sort of
// 64-bit keys whose unsigned order is the order of the values, with the rows
// beside them; a radix sort is stable and needs no comparisons. A string key
// is first made the rank of each string among the distinct strings. The rows
// are found as a permutation, and Query.Take copies them.

using System.Numerics;
using System.Runtime.InteropServices;
using Apache.Arrow;

namespace Bjostat;

public static class Sorting
{
    /// <summary>
    /// The name of the type of a column that <see cref="SortBy"/> cannot
    /// sort by, or the empty string when it can.
    /// </summary>
    public static string Unsortable(IArrowArray a) =>
        a is DoubleArray or FloatArray or Int64Array or Int32Array or Int16Array or UInt8Array
            or StringArray or BooleanArray or Date32Array or TimestampArray
        || Categories.Is(a)
            ? ""
            : a.Data.DataType.Name;

    /// <summary>
    /// The table with its rows in the order of the columns at
    /// <paramref name="columns"/>, each in the direction that
    /// <paramref name="descending"/> says.
    /// </summary>
    public static RecordBatch SortBy(RecordBatch t, int[] columns, bool[] descending) =>
        Query.Take(t, Order(columns.Select(t.Column).ToArray(), descending, t.Length), t.Length);

    /// <summary>
    /// The rows, 0 to n - 1, in the order of the columns, each in the
    /// direction that <paramref name="descending"/> says.
    /// </summary>
    internal static int[] Order(IArrowArray[] columns, bool[] descending, int n)
    {
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        for (int k = columns.Length - 1; k >= 0 && n > 1; k--)
            Pass(order, columns[k], descending[k]);
        return order;
    }

    // One stable pass: `order` in the order of the column, and among equal
    // values in the order it had.
    private static void Pass(int[] order, IArrowArray a, bool descending)
    {
        switch (a)
        {
            case DoubleArray d: PassNumbers(order, d, descending); break;
            case FloatArray f: PassNumbers(order, f, descending); break;
            case Int64Array l: PassNumbers(order, l, descending); break;
            case Int32Array i: PassNumbers(order, i, descending); break;
            case Int16Array s: PassNumbers(order, s, descending); break;
            case UInt8Array b: PassNumbers(order, b, descending); break;
            case Date32Array d: PassNumbers(order, d, descending); break;
            case TimestampArray ts: PassNumbers(order, ts, descending); break;
            case StringArray s: PassStrings(order, s, descending); break;
            case BooleanArray b: PassBools(order, b, descending); break;
            // A category is in the order of its levels: that of its codes.
            case DictionaryArray d when Categories.Is(d): PassNumbers(order, (Int32Array)d.Indices, descending); break;
            default:
                throw new ArgumentException($"(bjostat table): a column of {a.Data.DataType.Name} cannot be sorted by");
        }
    }

    private static void PassNumbers<T>(int[] order, PrimitiveArray<T> a, bool descending)
        where T : struct, INumber<T>, IEquatable<T>
    {
        var values = a.Values;
        bool nulls = a.NullCount != 0;
        // The rows with numbers, then those with a NaN, then the nulls, each
        // group in the order it had.
        var rows = new int[order.Length];
        var keys = new T[order.Length];
        var nans = new List<int>();
        var empty = new List<int>();
        int count = 0;
        foreach (int r in order)
        {
            if (nulls && !a.IsValid(r)) empty.Add(r);
            else if (T.IsNaN(values[r])) nans.Add(r);
            else
            {
                rows[count] = r;
                keys[count] = values[r];
                count++;
            }
        }
        SortRows(rows.AsSpan(0, count), keys.AsSpan(0, count), descending);
        rows.AsSpan(0, count).CopyTo(order);
        CollectionsMarshal.AsSpan(nans).CopyTo(order.AsSpan(count));
        CollectionsMarshal.AsSpan(empty).CopyTo(order.AsSpan(count + nans.Count));
    }

    // Strings become their rank among the distinct strings, which keeps their
    // order and is a number to sort by.
    private static void PassStrings(int[] order, StringArray a, bool descending)
    {
        var ids = Categories.StringIds(a, out var firsts);
        bool nulls = a.NullCount != 0;
        // The rank of each distinct string.
        var byBytes = firsts.ToArray();
        System.Array.Sort(byBytes, (x, y) => a.GetBytes(x).SequenceCompareTo(a.GetBytes(y)));
        var rank = new int[byBytes.Length];
        var place = new Dictionary<int, int>(firsts.Count);
        for (int i = 0; i < firsts.Count; i++) place[firsts[i]] = i;
        for (int i = 0; i < byBytes.Length; i++) rank[place[byBytes[i]]] = i;

        var rows = new int[order.Length];
        var keys = new int[order.Length];
        var empty = new List<int>();
        int count = 0;
        foreach (int r in order)
        {
            if (nulls && !a.IsValid(r)) empty.Add(r);
            else
            {
                rows[count] = r;
                keys[count] = rank[ids[r]];
                count++;
            }
        }
        SortRows(rows.AsSpan(0, count), keys.AsSpan(0, count), descending);
        rows.AsSpan(0, count).CopyTo(order);
        CollectionsMarshal.AsSpan(empty).CopyTo(order.AsSpan(count));
    }

    private static void PassBools(int[] order, BooleanArray a, bool descending)
    {
        var rows = new int[order.Length];
        var keys = new int[order.Length];
        var empty = new List<int>();
        int count = 0;
        foreach (int r in order)
        {
            if (a.GetValue(r) is bool b)
            {
                rows[count] = r;
                keys[count] = b ? 1 : 0;
                count++;
            }
            else empty.Add(r);
        }
        SortRows(rows.AsSpan(0, count), keys.AsSpan(0, count), descending);
        rows.AsSpan(0, count).CopyTo(order);
        CollectionsMarshal.AsSpan(empty).CopyTo(order.AsSpan(count));
    }

    // `rows` in the order of `keys`, the key of rows[i] being keys[i], and
    // among equal keys in the order they had.
    private static void SortRows<T>(Span<int> rows, Span<T> keys, bool descending)
        where T : struct, INumber<T>
    {
        int n = rows.Length;
        if (n < 2) return;
        var bits = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            ulong k = Ordered(keys[i]);
            bits[i] = descending ? ~k : k;
        }
        RadixSort(bits, rows);
    }

    // A 64-bit key whose unsigned order is the order of the number. -0.0 is
    // made 0.0 first, as the two are equal numbers.
    private static ulong Ordered<T>(T x) where T : struct, INumber<T>
    {
        const ulong sign = 0x8000_0000_0000_0000UL;
        if (typeof(T) == typeof(double) || typeof(T) == typeof(float))
        {
            double d = double.CreateTruncating(x);
            if (d == 0.0) d = 0.0;
            ulong u = BitConverter.DoubleToUInt64Bits(d);
            return (u & sign) != 0 ? ~u : u | sign;
        }
        if (typeof(T) == typeof(byte)) return byte.CreateTruncating(x);
        return (ulong)long.CreateTruncating(x) ^ sign;
    }

    // A least significant digit first radix sort, 8 bits a pass, of the keys
    // with the rows beside them. It is stable. A digit that is the same in
    // every key needs no pass. Many keys are sorted in parts on bjostat's
    // workers.
    private static void RadixSort(ulong[] keys, Span<int> rows)
    {
        int parts = Math.Min(keys.Length / PartKeys, Workers.Count);
        if (parts >= 2) RadixSortParts(keys, rows, parts);
        else RadixSortOne(keys, rows);
    }

    // The fewest keys of a part of a sort.
    private const int PartKeys = 1 << 16;

    // The sort in parts, each pass in three steps: each part counts the
    // digits of its keys; the counts give each part where each of its digits
    // goes, after the keys of the smaller digits and after the same digit's
    // keys of the parts before it, so that the sort stays stable; then each
    // part moves its keys there. The counts are taken again at each pass, as
    // a pass moves the keys between the parts.
    private static void RadixSortParts(ulong[] keys, Span<int> rows, int parts)
    {
        int n = keys.Length;
        int size = (n + parts - 1) / parts;
        // Whether a digit tells the keys apart: the counts of all the keys.
        var all = new int[parts][];
        Workers.For(parts, p =>
        {
            var c = new int[8 * 256];
            for (int i = p * size, to = Math.Min(n, i + size); i < to; i++)
            {
                ulong k = keys[i];
                for (int d = 0; d < 8; d++) c[d * 256 + (int)((k >> (8 * d)) & 0xFF)]++;
            }
            all[p] = c;
        });
        var keys2 = new ulong[n];
        var rows1 = rows.ToArray();
        var rows2 = new int[n];
        ulong[] fromKeys = keys, toKeys = keys2;
        int[] fromRows = rows1, toRows = rows2;
        var counts = new int[parts][];
        for (int p = 0; p < parts; p++) counts[p] = new int[256];
        for (int d = 0; d < 8; d++)
        {
            int first = (int)((keys[0] >> (8 * d)) & 0xFF), same = 0;
            for (int p = 0; p < parts; p++) same += all[p][d * 256 + first];
            if (same == n) continue;
            int shift = 8 * d;
            var from = fromKeys;
            Workers.For(parts, p =>
            {
                var c = counts[p];
                System.Array.Clear(c);
                for (int i = p * size, to = Math.Min(n, i + size); i < to; i++) c[(int)((from[i] >> shift) & 0xFF)]++;
            });
            int sum = 0;
            for (int b = 0; b < 256; b++)
                for (int p = 0; p < parts; p++)
                {
                    int c = counts[p][b];
                    counts[p][b] = sum;
                    sum += c;
                }
            var (fk, tk, fr, tr) = (fromKeys, toKeys, fromRows, toRows);
            Workers.For(parts, p =>
            {
                var at = counts[p];
                for (int i = p * size, to = Math.Min(n, i + size); i < to; i++)
                {
                    ulong k = fk[i];
                    int j = at[(int)((k >> shift) & 0xFF)]++;
                    tk[j] = k;
                    tr[j] = fr[i];
                }
            });
            (fromKeys, toKeys) = (toKeys, fromKeys);
            (fromRows, toRows) = (toRows, fromRows);
        }
        fromRows.CopyTo(rows);
    }

    private static void RadixSortOne(ulong[] keys, Span<int> rows)
    {
        int n = keys.Length;
        var counts = new int[8 * 256];
        foreach (ulong k in keys)
            for (int d = 0; d < 8; d++)
                counts[d * 256 + (int)((k >> (8 * d)) & 0xFF)]++;
        var keys2 = new ulong[n];
        var rows1 = rows.ToArray();
        var rows2 = new int[n];
        ulong[] fromKeys = keys, toKeys = keys2;
        int[] fromRows = rows1, toRows = rows2;
        for (int d = 0; d < 8; d++)
        {
            var count = counts.AsSpan(d * 256, 256);
            if (count[(int)((keys[0] >> (8 * d)) & 0xFF)] == n) continue;
            int sum = 0;
            for (int b = 0; b < 256; b++)
            {
                int c = count[b];
                count[b] = sum;
                sum += c;
            }
            int shift = 8 * d;
            for (int i = 0; i < n; i++)
            {
                ulong k = fromKeys[i];
                int at = count[(int)((k >> shift) & 0xFF)]++;
                toKeys[at] = k;
                toRows[at] = fromRows[i];
            }
            (fromKeys, toKeys) = (toKeys, fromKeys);
            (fromRows, toRows) = (toRows, fromRows);
        }
        fromRows.CopyTo(rows);
    }
}
