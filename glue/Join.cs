/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Join: the rows of two tables whose keys are equal, as dplyr's joins give
// them.
//
// The rows of the right table are put in a hash table by their keys. Each row
// of the left table then finds the right rows with the same keys. The result
// has the rows of the left table in their order, and for each, the right
// rows it matches in their order. Two nulls are equal keys, as in dplyr and
// R's merge, and so are two NaNs.
//
// The columns are the keys, from the left table, then the other columns of
// the left table, then those of the right. A name that both tables have for
// columns that are not keys gets ".x" in the left one and ".y" in the right.

using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Bjostat;

public static class Joins
{
    /// <summary>What a join keeps: 0 inner, 1 left, 2 semi, 3 anti.</summary>
    public static RecordBatch Join(RecordBatch left, RecordBatch right, string[] keys, int kind)
    {
        var lk = keys.Select(k => left.Column(left.Schema.GetFieldIndex(k))).ToArray();
        var rk = keys.Select(k => right.Column(right.Schema.GetFieldIndex(k))).ToArray();

        // The right rows by the hash of their keys: the first row of each hash,
        // and the next row with the same hash. The rows are put in from the
        // last, each before the ones there, so each chain is in the order of
        // the rows.
        int rn = right.Length;
        var next = new int[rn];
        var heads = new Dictionary<ulong, int>();
        var rh = Hashes(rk, rn);
        for (int r = rn - 1; r >= 0; r--)
        {
            ref int head = ref CollectionsMarshal.GetValueRefOrAddDefault(heads, rh[r], out bool there);
            next[r] = there ? head : -1;
            head = r;
        }
        var same = lk.Select((a, k) => Equality(a, rk[k])).ToArray();

        int ln = left.Length;
        var lh = Hashes(lk, ln);
        var leftRows = new List<int>(ln);
        var rightRows = new List<int>(kind == 0 || kind == 1 ? ln : 0);
        for (int l = 0; l < ln; l++)
        {
            bool found = false;
            if (heads.TryGetValue(lh[l], out int r))
            {
                for (; r >= 0; r = next[r])
                {
                    if (!SameKeys(same, l, r)) continue;
                    found = true;
                    if (kind >= 2) break;
                    leftRows.Add(l);
                    rightRows.Add(r);
                }
            }
            if (kind == 1 && !found)
            {
                leftRows.Add(l);
                rightRows.Add(-1);
            }
            else if ((kind == 2 && found) || (kind == 3 && !found))
                leftRows.Add(l);
        }

        int n = leftRows.Count;
        var lrows = leftRows.ToArray();
        if (kind >= 2) return Query.Take(left, lrows, n);

        var rrows = rightRows.ToArray();
        var fields = new List<Field>();
        var columns = new List<IArrowArray>();
        var keySet = new HashSet<string>(keys);
        var leftNames = left.Schema.FieldsList.Select(f => f.Name).Where(x => !keySet.Contains(x)).ToHashSet();
        var rightNames = right.Schema.FieldsList.Select(f => f.Name).Where(x => !keySet.Contains(x)).ToHashSet();
        var takenLeft = Query.Take(left, lrows, n);
        foreach (var k in keys)
        {
            int i = left.Schema.GetFieldIndex(k);
            fields.Add(left.Schema.GetFieldByIndex(i));
            columns.Add(takenLeft.Column(i));
        }
        for (int i = 0; i < left.ColumnCount; i++)
        {
            var f = left.Schema.GetFieldByIndex(i);
            if (keySet.Contains(f.Name)) continue;
            fields.Add(rightNames.Contains(f.Name) ? new Field(f.Name + ".x", f.DataType, true) : f);
            columns.Add(takenLeft.Column(i));
        }
        var takenRight = TakeOrNull(right, rrows);
        for (int i = 0; i < right.ColumnCount; i++)
        {
            var f = right.Schema.GetFieldByIndex(i);
            if (keySet.Contains(f.Name)) continue;
            fields.Add(leftNames.Contains(f.Name)
                ? new Field(f.Name + ".y", f.DataType, true)
                : new Field(f.Name, f.DataType, true));
            columns.Add(takenRight[i]);
        }
        var schema = new Schema.Builder();
        foreach (var f in fields) schema.Field(f);
        return new RecordBatch(schema.Build(), columns, n);
    }

    /// <summary>
    /// The text of the first problem with the keys, or the empty string: a key
    /// that a table does not have, a key of other types in the two tables, a
    /// type that cannot be a key, or a name that is a key twice.
    /// </summary>
    public static string KeyProblem(RecordBatch left, RecordBatch right, string[] keys)
    {
        if (keys.Length == 0) return "a join needs at least one key.";
        if (keys.Distinct().Count() != keys.Length) return "a key is named twice.";
        foreach (var k in keys)
        {
            int li = left.Schema.GetFieldIndex(k), ri = right.Schema.GetFieldIndex(k);
            if (li < 0) return $"the left table has no column '{k}'.";
            if (ri < 0) return $"the right table has no column '{k}'.";
            var lt = left.Schema.GetFieldByIndex(li).DataType;
            var rt = right.Schema.GetFieldByIndex(ri).DataType;
            if (!KeyType(lt)) return $"'{k}' is a column of {lt.Name}, which cannot be a key.";
            if (!SameType(lt, rt)) return $"'{k}' is a column of {lt.Name} in the left table and of {rt.Name} in the right.";
        }
        return "";
    }

    private static bool KeyType(IArrowType t) =>
        t is DoubleType or FloatType or Int64Type or Int32Type or Int16Type or UInt8Type
            or StringType or BooleanType or Date32Type or TimestampType;

    private static bool SameType(IArrowType a, IArrowType b) =>
        a.TypeId == b.TypeId
        && (a is not TimestampType ta || b is TimestampType tb && ta.Unit == tb.Unit && ta.Timezone == tb.Timezone);

    // The hash of each row's keys. A null and a NaN each have one hash, and
    // -0.0 has that of 0.0, since they are equal keys.
    private static ulong[] Hashes(IArrowArray[] keys, int n)
    {
        var h = new ulong[n];
        System.Array.Fill(h, 14695981039346656037UL);
        foreach (var a in keys)
        {
            switch (a)
            {
                case DoubleArray d: Mix(h, d.Values, x => double.IsNaN(x) ? 0x7FF8_0000_0000_0001UL : BitConverter.DoubleToUInt64Bits(x == 0.0 ? 0.0 : x)); break;
                case FloatArray f: Mix(h, f.Values, x => float.IsNaN(x) ? 0x7FC0_0001UL : BitConverter.SingleToUInt32Bits(x == 0.0f ? 0.0f : x)); break;
                case Int64Array l: Mix(h, l.Values, x => (ulong)x); break;
                case Int32Array i: Mix(h, i.Values, x => (ulong)x); break;
                case Int16Array s: Mix(h, s.Values, x => (ulong)x); break;
                case UInt8Array b: Mix(h, b.Values, x => x); break;
                case Date32Array d: Mix(h, d.Values, x => (ulong)x); break;
                case TimestampArray t: Mix(h, t.Values, x => (ulong)x); break;
                case BooleanArray b:
                    for (int i = 0; i < n; i++) Step(ref h[i], b.GetValue(i) == true ? 1UL : 2UL);
                    break;
                case StringArray s:
                    for (int i = 0; i < n; i++)
                    {
                        var hc = new HashCode();
                        hc.AddBytes(s.GetBytes(i));
                        Step(ref h[i], (ulong)(uint)hc.ToHashCode() * 0x9E3779B97F4A7C15UL);
                    }
                    break;
                default:
                    throw new ArgumentException($"(bjostat table): a column of {a.Data.DataType.Name} cannot be a key");
            }
            // A null has one hash, whatever value is under it.
            if (a.NullCount != 0)
                for (int i = 0; i < n; i++)
                    if (a.IsNull(i)) Step(ref h[i], 0x9E3779B97F4A7C15UL);
        }
        return h;
    }

    private static void Mix<T>(ulong[] h, ReadOnlySpan<T> values, Func<T, ulong> value)
    {
        for (int i = 0; i < h.Length; i++) Step(ref h[i], value(values[i]));
    }

    private static void Step(ref ulong h, ulong v)
    {
        h = (h ^ v) * 1099511628211UL;
        h ^= h >> 29;
    }

    private static bool SameKeys(Func<int, int, bool>[] same, int l, int r)
    {
        foreach (var f in same)
            if (!f(l, r)) return false;
        return true;
    }

    // Whether row i of a and row j of b have the same key, two nulls and two
    // NaNs being the same.
    private static Func<int, int, bool> Equality(IArrowArray a, IArrowArray b)
    {
        Func<int, int, bool> values = (a, b) switch
        {
            (DoubleArray x, DoubleArray y) => (i, j) => x.Values[i] == y.Values[j] || (double.IsNaN(x.Values[i]) && double.IsNaN(y.Values[j])),
            (FloatArray x, FloatArray y) => (i, j) => x.Values[i] == y.Values[j] || (float.IsNaN(x.Values[i]) && float.IsNaN(y.Values[j])),
            (Int64Array x, Int64Array y) => (i, j) => x.Values[i] == y.Values[j],
            (Int32Array x, Int32Array y) => (i, j) => x.Values[i] == y.Values[j],
            (Int16Array x, Int16Array y) => (i, j) => x.Values[i] == y.Values[j],
            (UInt8Array x, UInt8Array y) => (i, j) => x.Values[i] == y.Values[j],
            (Date32Array x, Date32Array y) => (i, j) => x.Values[i] == y.Values[j],
            (TimestampArray x, TimestampArray y) => (i, j) => x.Values[i] == y.Values[j],
            (BooleanArray x, BooleanArray y) => (i, j) => x.GetValue(i) == y.GetValue(j),
            (StringArray x, StringArray y) => (i, j) => x.GetBytes(i).SequenceEqual(y.GetBytes(j)),
            _ => throw new ArgumentException($"(bjostat table): a column of {a.Data.DataType.Name} cannot be a key"),
        };
        if (a.NullCount == 0 && b.NullCount == 0) return values;
        return (i, j) =>
        {
            bool na = a.IsNull(i), nb = b.IsNull(j);
            return na || nb ? na && nb : values(i, j);
        };
    }

    // The columns of `t` at the rows; a row of -1 is a null in each column.
    private static IArrowArray[] TakeOrNull(RecordBatch t, int[] rows)
    {
        int n = rows.Length;
        bool missing = rows.Any(r => r < 0);
        if (!missing) return Enumerable.Range(0, t.ColumnCount).Select(Query.Take(t, rows, n).Column).ToArray();

        // The rows that are there, at their places; a missing row takes row 0,
        // whose value is then hidden by a null.
        var columns = new IArrowArray[t.ColumnCount];
        if (t.Length == 0)
        {
            for (int c = 0; c < columns.Length; c++) columns[c] = Nulls(t.Schema.GetFieldByIndex(c).DataType, n);
            return columns;
        }
        var fixedRows = rows.Select(r => r < 0 ? 0 : r).ToArray();
        var taken = Query.Take(t, fixedRows, n);
        for (int c = 0; c < columns.Length; c++)
            columns[c] = HideMissing(taken.Column(c), rows);
        return columns;
    }

    // The column with a null at each place whose row is -1.
    private static IArrowArray HideMissing(IArrowArray a, int[] rows)
    {
        var data = a.Data;
        var bits = new byte[(rows.Length + 7) / 8];
        int nulls = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] >= 0 && a.IsValid(i)) bits[i >> 3] |= (byte)(1 << (i & 7));
            else nulls++;
        }
        var buffers = data.Buffers.ToArray();
        buffers[0] = new ArrowBuffer(bits);
        return ArrowArrayFactory.BuildArray(new ArrayData(data.DataType, data.Length, nulls, data.Offset, buffers, data.Children, data.Dictionary));
    }

    // A column of n nulls of the type.
    private static IArrowArray Nulls(IArrowType type, int n)
    {
        var bits = new ArrowBuffer(new byte[(n + 7) / 8]);
        ArrayData data = type switch
        {
            StringType or BinaryType => new ArrayData(type, n, n, 0,
                new[] { bits, new ArrowBuffer(new byte[(n + 1) * sizeof(int)]), ArrowBuffer.Empty }),
            BooleanType => new ArrayData(type, n, n, 0, new[] { bits, new ArrowBuffer(new byte[(n + 7) / 8]) }),
            FixedWidthType fw => new ArrayData(type, n, n, 0, new[] { bits, new ArrowBuffer(new byte[n * fw.BitWidth / 8]) }),
            _ => throw new ArgumentException($"(bjostat table): a column of {type.Name} cannot be made of nulls"),
        };
        return ArrowArrayFactory.BuildArray(data);
    }
}
