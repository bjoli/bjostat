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
using System.Runtime.CompilerServices;
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
            or UInt16Array or UInt32Array or UInt64Array
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
    internal static int[] Order(IArrowArray[] columns, bool[] descending, int n) =>
        OrderRows(columns, descending, ReadOnlySpan<int>.Empty, null, n);

    /// <summary>
    /// The indices 0 to n - 1 in the order of the columns at the rows in
    /// <paramref name="rowMap"/> (or at 0 to n - 1 when <paramref name="rowMap"/>
    /// is empty). When a string column's ids from <see cref="Categories.StringIds"/>
    /// are already known, <paramref name="stringIds"/> gives them so the column
    /// is not numbered again.
    /// </summary>
    internal static int[] OrderRows(
        IArrowArray[] columns,
        bool[] descending,
        ReadOnlySpan<int> rowMap,
        (int[] Ids, List<int> Firsts)?[]? stringIds,
        int n)
    {
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        for (int k = columns.Length - 1; k >= 0 && n > 1; k--)
            Pass(order, rowMap, columns[k], descending[k], stringIds?[k]);
        return order;
    }

    // One stable pass: `order` in the order of the column, and among equal
    // values in the order it had.
    private static void Pass(
        int[] order,
        ReadOnlySpan<int> rowMap,
        IArrowArray a,
        bool descending,
        (int[] Ids, List<int> Firsts)? stringIds)
    {
        switch (a)
        {
            case DoubleArray d: PassNumbers(order, rowMap, d, descending); break;
            case FloatArray f: PassNumbers(order, rowMap, f, descending); break;
            case Int64Array l: PassNumbers(order, rowMap, l, descending); break;
            case Int32Array i: PassNumbers(order, rowMap, i, descending); break;
            case Int16Array s: PassNumbers(order, rowMap, s, descending); break;
            case UInt8Array b: PassNumbers(order, rowMap, b, descending); break;
            case UInt16Array s: PassNumbers(order, rowMap, s, descending); break;
            case UInt32Array u: PassNumbers(order, rowMap, u, descending); break;
            case UInt64Array l: PassNumbers(order, rowMap, l, descending); break;
            case Date32Array d: PassNumbers(order, rowMap, d, descending); break;
            case TimestampArray ts: PassNumbers(order, rowMap, ts, descending); break;
            case StringArray s: PassStrings(order, rowMap, s, descending, stringIds); break;
            case BooleanArray b: PassBools(order, rowMap, b, descending); break;
            // A category is in the order of its levels: that of its codes.
            case DictionaryArray d when Categories.Is(d):
                PassNumbers(order, rowMap, (Int32Array)d.Indices, descending);
                break;
            default:
                throw new ArgumentException($"(bjostat table): a column of {a.Data.DataType.Name} cannot be sorted by");
        }
    }

    private static void PassNumbers<T>(int[] order, ReadOnlySpan<int> rowMap, PrimitiveArray<T> a, bool descending)
        where T : struct, INumber<T>, IEquatable<T>
    {
        var values = a.Values;
        // The rows with numbers, then those with a NaN, then the nulls, each
        // group in the order it had. The nulls are read from the bitmap
        // itself, not through IsValid, and the rows that are not numbers go
        // to the end of `others`, NaNs first, nulls from the back.
        var valid = a.NullCount == 0 ? ReadOnlySpan<byte>.Empty : a.NullBitmapBuffer.Span;
        int offset = a.Offset;
        int n = order.Length;
        bool mayHaveNaN = typeof(T) == typeof(double) || typeof(T) == typeof(float);
        if (valid.IsEmpty && !mayHaveNaN && rowMap.IsEmpty)
        {
            var bits = new ulong[n];
            ulong first = Ordered(values[order[0]]);
            if (descending) first = ~first;
            ulong diff = 0;
            for (int i = 0; i < n; i++)
            {
                ulong k = Ordered(values[order[i]]);
                if (descending) k = ~k;
                bits[i] = k;
                diff |= k ^ first;
            }
            RadixSort(bits, n, order, diff);
            return;
        }
        var rows = new int[n];
        var bits2 = new ulong[n];
        int[]? others = null;
        int count = 0, nanCount = 0, nullCount = 0;
        ulong firstKey = 0, diffKeys = 0;
        foreach (int r in order)
        {
            int row = rowMap.IsEmpty ? r : rowMap[r];
            int bit = offset + row;
            if (!valid.IsEmpty && (valid[bit >> 3] & (1 << (bit & 7))) == 0)
            {
                others ??= new int[n];
                others[n - 1 - nullCount++] = r;
            }
            else if (mayHaveNaN && T.IsNaN(values[row]))
            {
                others ??= new int[n];
                others[nanCount++] = r;
            }
            else
            {
                ulong k = Ordered(values[row]);
                if (descending) k = ~k;
                if (count == 0) firstKey = k;
                else diffKeys |= k ^ firstKey;
                rows[count] = r;
                bits2[count] = k;
                count++;
            }
        }
        if (others == null)
        {
            RadixSort(bits2, n, order, diffKeys);
            return;
        }
        RadixSort(bits2, count, rows, diffKeys);
        rows.AsSpan(0, count).CopyTo(order);
        others.AsSpan(0, nanCount).CopyTo(order.AsSpan(count));
        // The nulls were put from the back; they go in the order they had.
        for (int i = 0; i < nullCount; i++) order[count + nanCount + i] = others[n - 1 - i];
    }

    // Strings become their rank among the distinct strings, which keeps their
    // order and is a number to sort by.
    private static void PassStrings(
        int[] order,
        ReadOnlySpan<int> rowMap,
        StringArray a,
        bool descending,
        (int[] Ids, List<int> Firsts)? knownIds)
    {
        var (ids, firsts) = knownIds ?? NumberStrings(a);
        // The rank of each distinct string.
        var byBytes = Categories.InByteOrder(a, firsts);
        var rank = new int[byBytes.Length];
        for (int i = 0; i < byBytes.Length; i++) rank[byBytes[i]] = i;

        int n = order.Length;
        var valid = a.NullCount == 0 ? ReadOnlySpan<byte>.Empty : a.NullBitmapBuffer.Span;
        int offset = a.Offset;
        if (valid.IsEmpty && rowMap.IsEmpty)
        {
            var bits = new ulong[n];
            ulong first = (uint)rank[ids[order[0]]];
            if (descending) first = ~first;
            ulong diff = 0;
            for (int i = 0; i < n; i++)
            {
                ulong k = (uint)rank[ids[order[i]]];
                if (descending) k = ~k;
                bits[i] = k;
                diff |= k ^ first;
            }
            RadixSort(bits, n, order, diff);
            return;
        }
        var rows = new int[n];
        var bits2 = new ulong[n];
        int[]? empty = null;
        int count = 0, nullCount = 0;
        ulong firstKey = 0, diffKeys = 0;
        foreach (int r in order)
        {
            int row = rowMap.IsEmpty ? r : rowMap[r];
            int bit = offset + row;
            if (!valid.IsEmpty && (valid[bit >> 3] & (1 << (bit & 7))) == 0)
            {
                empty ??= new int[n];
                empty[nullCount++] = r;
            }
            else
            {
                ulong k = (uint)rank[ids[row]];
                if (descending) k = ~k;
                if (count == 0) firstKey = k;
                else diffKeys |= k ^ firstKey;
                rows[count] = r;
                bits2[count] = k;
                count++;
            }
        }
        if (empty == null)
        {
            RadixSort(bits2, n, order, diffKeys);
            return;
        }
        RadixSort(bits2, count, rows, diffKeys);
        rows.AsSpan(0, count).CopyTo(order);
        empty.AsSpan(0, nullCount).CopyTo(order.AsSpan(count));
    }

    private static (int[] Ids, List<int> Firsts) NumberStrings(StringArray a)
    {
        var ids = Categories.StringIds(a, out var firsts);
        return (ids, firsts);
    }

    private static void PassBools(int[] order, ReadOnlySpan<int> rowMap, BooleanArray a, bool descending)
    {
        int n = order.Length;
        var valid = a.NullCount == 0 ? ReadOnlySpan<byte>.Empty : a.NullBitmapBuffer.Span;
        var values = a.ValueBuffer.Span;
        int offset = a.Offset;
        var rest = new int[n];
        int firstCount = 0, secondCount = 0, nullCount = 0;
        foreach (int r in order)
        {
            int row = rowMap.IsEmpty ? r : rowMap[r];
            int bit = offset + row;
            if (!valid.IsEmpty && (valid[bit >> 3] & (1 << (bit & 7))) == 0)
            {
                rest[n - 1 - nullCount++] = r;
            }
            else
            {
                bool b = (values[bit >> 3] & (1 << (bit & 7))) != 0;
                if (b == descending) order[firstCount++] = r;
                else rest[secondCount++] = r;
            }
        }
        rest.AsSpan(0, secondCount).CopyTo(order.AsSpan(firstCount));
        for (int i = 0; i < nullCount; i++) order[firstCount + secondCount + i] = rest[n - 1 - i];
    }

    // A 64-bit key whose unsigned order is the order of the number. -0.0 is
    // made 0.0 first, as the two are equal numbers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Ordered<T>(T x) where T : struct, INumber<T>
    {
        const ulong sign = 0x8000_0000_0000_0000UL;
        if (typeof(T) == typeof(double))
        {
            double d = double.CreateTruncating(x);
            if (d == 0.0) d = 0.0;
            ulong u = BitConverter.DoubleToUInt64Bits(d);
            return (u & sign) != 0 ? ~u : u | sign;
        }
        if (typeof(T) == typeof(float))
        {
            float f = float.CreateTruncating(x);
            if (f == 0.0f) f = 0.0f;
            uint u = BitConverter.SingleToUInt32Bits(f);
            return (u & 0x8000_0000U) != 0 ? ~u : (u | 0x8000_0000U);
        }
        if (typeof(T) == typeof(byte)) return byte.CreateTruncating(x);
        if (typeof(T) == typeof(ushort)) return ushort.CreateTruncating(x);
        if (typeof(T) == typeof(short)) return (ushort)((ushort)short.CreateTruncating(x) ^ 0x8000);
        if (typeof(T) == typeof(uint)) return uint.CreateTruncating(x);
        if (typeof(T) == typeof(int)) return (uint)int.CreateTruncating(x) ^ 0x8000_0000U;
        if (typeof(T) == typeof(ulong)) return ulong.CreateTruncating(x);
        return (ulong)long.CreateTruncating(x) ^ sign;
    }

    // A least significant digit first radix sort, 11 bits a pass, of the
    // keys with the rows beside them: 6 passes for 64 bits, where 8 bits a
    // pass need 8. It is stable. A digit that is the same in every key needs
    // no pass. Many keys are sorted in parts on bjostat's workers.
    private const int DigitBits = 11;
    private const int Buckets = 1 << DigitBits;
    private const int Digits = (64 + DigitBits - 1) / DigitBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Digit(ulong k, int d) => (int)((k >> (DigitBits * d)) & (Buckets - 1));

    private static void RadixSort(ulong[] keys, int n, int[] rows, ulong diff)
    {
        if (n < 2 || diff == 0) return;
        int parts = Math.Min(n / PartKeys, Workers.Count);
        if (parts >= 2) RadixSortParts(keys, n, rows, parts, diff);
        else RadixSortOne(keys, n, rows, diff);
    }

    // The fewest keys of a part of a sort.
    private const int PartKeys = 1 << 16;

    // The sort in parts, each pass in three steps: each part counts the
    // digits of its keys; the counts give each part where each of its digits
    // goes, after the keys of the smaller digits and after the same digit's
    // keys of the parts before it, so that the sort stays stable; then each
    // part moves its keys there. The counts are taken again at each pass, as
    // a pass moves the keys between the parts.
    private static void RadixSortParts(ulong[] keys, int n, int[] rows, int parts, ulong diff)
    {
        int lastDigit = Digits - 1;
        while (lastDigit > 0 && Digit(diff, lastDigit) == 0) lastDigit--;

        int size = (n + parts - 1) / parts;
        ulong[]? keys2 = null;
        var rows2 = new int[n];
        ulong[] fromKeys = keys, toKeys = keys;
        int[] fromRows = rows, toRows = rows2;
        var counts = new int[parts][];
        for (int p = 0; p < parts; p++) counts[p] = new int[Buckets];
        for (int d = 0; d <= lastDigit; d++)
        {
            if (Digit(diff, d) == 0) continue;
            int digit = d;
            var from = fromKeys;
            Workers.For(parts, p =>
            {
                var c = counts[p];
                System.Array.Clear(c);
                for (int i = p * size, to = Math.Min(n, i + size); i < to; i++) c[Digit(from[i], digit)]++;
            });
            int sum = 0;
            for (int b = 0; b < Buckets; b++)
                for (int p = 0; p < parts; p++)
                {
                    int c = counts[p][b];
                    counts[p][b] = sum;
                    sum += c;
                }
            var (fk, fr, tr) = (fromKeys, fromRows, toRows);
            if (d == lastDigit)
            {
                Workers.For(parts, p =>
                {
                    var at = counts[p];
                    for (int i = p * size, to = Math.Min(n, i + size); i < to; i++)
                        tr[at[Digit(fk[i], digit)]++] = fr[i];
                });
            }
            else
            {
                var tk = ReferenceEquals(toKeys, fromKeys) ? (keys2 ??= new ulong[n]) : toKeys;
                toKeys = tk;
                Workers.For(parts, p =>
                {
                    var at = counts[p];
                    for (int i = p * size, to = Math.Min(n, i + size); i < to; i++)
                    {
                        ulong k = fk[i];
                        int j = at[Digit(k, digit)]++;
                        tk[j] = k;
                        tr[j] = fr[i];
                    }
                });
            }
            (fromKeys, toKeys) = (toKeys, fromKeys);
            (fromRows, toRows) = (toRows, fromRows);
        }
        if (!ReferenceEquals(fromRows, rows))
            fromRows.AsSpan(0, n).CopyTo(rows);
    }

    private static void RadixSortOne(ulong[] keys, int n, int[] rows, ulong diff)
    {
        int lastDigit = Digits - 1;
        while (lastDigit > 0 && Digit(diff, lastDigit) == 0) lastDigit--;

        var count = new int[Buckets];
        ulong[]? keys2 = null;
        var rows2 = new int[n];
        ulong[] fromKeys = keys, toKeys = keys;
        int[] fromRows = rows, toRows = rows2;
        for (int d = 0; d <= lastDigit; d++)
        {
            if (Digit(diff, d) == 0) continue;
            System.Array.Clear(count);
            for (int i = 0; i < n; i++) count[Digit(fromKeys[i], d)]++;
            int sum = 0;
            for (int b = 0; b < Buckets; b++)
            {
                int c = count[b];
                count[b] = sum;
                sum += c;
            }
            if (d == lastDigit)
            {
                for (int i = 0; i < n; i++)
                    toRows[count[Digit(fromKeys[i], d)]++] = fromRows[i];
            }
            else
            {
                if (ReferenceEquals(toKeys, fromKeys)) toKeys = keys2 ??= new ulong[n];
                for (int i = 0; i < n; i++)
                {
                    ulong k = fromKeys[i];
                    int at = count[Digit(k, d)]++;
                    toKeys[at] = k;
                    toRows[at] = fromRows[i];
                }
            }
            (fromKeys, toKeys) = (toKeys, fromKeys);
            (fromRows, toRows) = (toRows, fromRows);
        }
        if (!ReferenceEquals(fromRows, rows))
            fromRows.AsSpan(0, n).CopyTo(rows);
    }
}
