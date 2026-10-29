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
    internal static int[] Groups(IArrowArray[] keys, int n, out List<int> firsts)
    {
        int[] cur;
        long card;
        if (keys.Length == 0)
        {
            cur = new int[n];
            card = 1;
        }
        else
        {
            cur = Ids(keys[0], out int c0);
            card = Compact(cur, c0);
            // Once each row has a group of its own, no key splits one more.
            for (int k = 1; k < keys.Length && card < n; k++)
            {
                var ids = Ids(keys[k], out int ck);
                if (card * ck <= DenseLimit)
                {
                    for (int r = 0; r < n; r++) cur[r] = (int)(cur[r] * ck + ids[r]);
                    card = Compact(cur, card * ck);
                }
                else
                {
                    var number = new Dictionary<long, int>();
                    for (int r = 0; r < n; r++)
                    {
                        ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(number, (long)cur[r] * ck + ids[r], out bool there);
                        if (!there) id = number.Count - 1;
                        cur[r] = id;
                    }
                    card = number.Count;
                }
            }
        }

        // The groups in the order of their first rows.
        firsts = new List<int>();
        var group = new int[card];
        System.Array.Fill(group, -1);
        for (int r = 0; r < n; r++)
        {
            ref int g = ref group[cur[r]];
            if (g < 0)
            {
                g = firsts.Count;
                firsts.Add(r);
            }
            cur[r] = g;
        }
        return cur;
    }

    // The ids numbered again from 0, in the order of their first rows, so
    // that there are as many as there are distinct ones. Answers how many.
    private static int Compact(int[] ids, long card)
    {
        var number = new int[card];
        System.Array.Fill(number, -1);
        int count = 0;
        for (int r = 0; r < ids.Length; r++)
        {
            ref int id = ref number[ids[r]];
            if (id < 0) id = count++;
            ids[r] = id;
        }
        return count;
    }

    // The most ids put together that index an array: 16 MB of ints.
    private const long DenseLimit = 1 << 22;

    /// <summary>
    /// An id for each row's value in the column, from 0 to
    /// <paramref name="card"/> - 1, equal for equal values; the nulls have one
    /// id. A category's id is its code, a bool's 0 or 1, a string's from
    /// Categories.StringIds; an integer's its distance from the least when
    /// that range is small, and a number from a dictionary otherwise.
    /// </summary>
    private static int[] Ids(IArrowArray a, out int card)
    {
        int n = a.Length;
        int[] ids;
        switch (a)
        {
            case DictionaryArray d when Categories.Is(d):
                ids = ((Int32Array)d.Indices).Values.ToArray();
                card = d.Dictionary.Length;
                break;
            case BooleanArray b:
                ids = new int[n];
                for (int r = 0; r < n; r++) ids[r] = b.GetValue(r) == true ? 1 : 0;
                card = 2;
                break;
            case StringArray s:
                ids = Categories.StringIds(s, out var firsts);
                card = firsts.Count;
                break;
            case Int64Array l: ids = Integers(l.Values, a, x => x, out card); break;
            case Int32Array i: ids = Integers(i.Values, a, x => x, out card); break;
            case Int16Array i: ids = Integers(i.Values, a, x => x, out card); break;
            case UInt8Array i: ids = Integers(i.Values, a, x => x, out card); break;
            case Date32Array d: ids = Integers(d.Values, a, x => x, out card); break;
            case TimestampArray t: ids = Integers(t.Values, a, x => x, out card); break;
            case DoubleArray d:
                ids = Numbered(d.Values, a, x => double.IsNaN(x) ? 0x7FF8_0000_0000_0001L : BitConverter.DoubleToInt64Bits(x == 0.0 ? 0.0 : x), out card);
                break;
            case FloatArray f:
                ids = Numbered(f.Values, a, x => float.IsNaN(x) ? 0x7FC0_0001L : BitConverter.SingleToInt32Bits(x == 0.0f ? 0.0f : x), out card);
                break;
            default:
                throw new ArgumentException($"(bjostat table): a column of {a.Data.DataType.Name} cannot be a key");
        }
        // The nulls: one id after the others.
        if (a.NullCount != 0)
        {
            for (int r = 0; r < n; r++)
                if (a.IsNull(r)) ids[r] = card;
            card++;
        }
        return ids;
    }

    // The distance of each value from the least, when the values span fewer
    // than DenseLimit and fewer than four times the rows; otherwise numbers
    // from a dictionary. The values under nulls are left out of the range.
    private static int[] Integers<T>(ReadOnlySpan<T> values, IArrowArray a, Func<T, long> value, out int card)
    {
        int n = values.Length;
        bool nulls = a.NullCount != 0;
        long least = long.MaxValue, most = long.MinValue;
        for (int r = 0; r < n; r++)
        {
            if (nulls && a.IsNull(r)) continue;
            long v = value(values[r]);
            if (v < least) least = v;
            if (v > most) most = v;
        }
        if (least > most)
        {
            card = 0;
            return new int[n];
        }
        if ((ulong)(most - least) < (ulong)Math.Min(DenseLimit, 4L * n + 64))
        {
            var ids = new int[n];
            for (int r = 0; r < n; r++)
                ids[r] = nulls && a.IsNull(r) ? 0 : (int)(value(values[r]) - least);
            card = (int)(most - least + 1);
            return ids;
        }
        return Numbered(values, a, value, out card);
    }

    // A number for each distinct value, in the order of their first rows.
    private static int[] Numbered<T>(ReadOnlySpan<T> values, IArrowArray a, Func<T, long> value, out int card)
    {
        int n = values.Length;
        bool nulls = a.NullCount != 0;
        var ids = new int[n];
        var number = new Dictionary<long, int>();
        for (int r = 0; r < n; r++)
        {
            if (nulls && a.IsNull(r)) continue;
            ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(number, value(values[r]), out bool there);
            if (!there) id = number.Count - 1;
            ids[r] = id;
        }
        card = number.Count;
        return ids;
    }

    // The columns of the names, or a TableProblem for a name the table does
    // not have, a name given twice, or a column that cannot be a key.
    internal static int[] KeyColumns(RecordBatch t, string[] names)
    {
        if (names.Distinct().Count() != names.Length) throw new TableProblem("a key is named twice.");
        return names.Select(name =>
        {
            int i = Column(t, name);
            var type = t.Schema.GetFieldByIndex(i).DataType;
            if (!Joins.KeyType(type))
                throw new TableProblem($"'{name}' is a column of {TypeText(type)}, which cannot be a key.");
            return i;
        }).ToArray();
    }

    internal static int Column(RecordBatch t, string name)
    {
        int i = t.Schema.GetFieldIndex(name);
        if (i < 0)
            throw new TableProblem($"the table has no column '{name}'. Its columns are: "
                                   + string.Join(", ", t.Schema.FieldsList.Select(f => f.Name)) + ".");
        return i;
    }

    // The name of a type as (bjostat table) prints it.
    internal static string TypeText(IArrowType t) => t switch
    {
        DoubleType => "double",
        FloatType => "float",
        Int64Type => "long",
        Int32Type => "int",
        StringType => "string",
        Date32Type => "date",
        TimestampType { Timezone: not (null or "") } => "instant",
        TimestampType => "datetime",
        BooleanType => "bool",
        _ when Categories.IsType(t) => "category",
        _ => t.Name,
    };

    // ---------------------------------------------------------------------
    // Distinct

    /// <summary>
    /// The first row of each distinct value of the key columns, in the order
    /// of the rows, with all columns. With no key names, all columns are the
    /// key.
    /// </summary>
    public static RecordBatch Distinct(RecordBatch t, string[] keys)
    {
        var names = keys.Length == 0 ? t.Schema.FieldsList.Select(f => f.Name).ToArray() : keys;
        var columns = KeyColumns(t, names).Select(t.Column).ToArray();
        Groups(columns, t.Length, out var firsts);
        if (firsts.Count == t.Length) return t;
        return Query.Take(t, firsts.ToArray(), firsts.Count);
    }

    // ---------------------------------------------------------------------
    // Append

    /// <summary>
    /// The rows of the tables, one table after the other, as dplyr's
    /// bind_rows. A column is matched by its name: the columns are those of
    /// the first table, then the new ones of each next table, and a table
    /// without a column has nulls in it. A column of one name has to have one
    /// type in all the tables.
    /// </summary>
    public static RecordBatch Append(RecordBatch[] tables)
    {
        var fields = new List<Field>();
        var index = new Dictionary<string, int>();
        foreach (var t in tables)
            foreach (var f in t.Schema.FieldsList)
            {
                if (!index.TryGetValue(f.Name, out int i))
                {
                    index[f.Name] = fields.Count;
                    fields.Add(new Field(f.Name, f.DataType, true));
                }
                else if (!Joins.SameType(fields[i].DataType, f.DataType))
                    throw new TableProblem($"'{f.Name}' is a column of {TypeText(fields[i].DataType)} in one table "
                                           + $"and of {TypeText(f.DataType)} in another.");
            }

        int n = tables.Sum(t => t.Length);
        var columns = fields.Select(f =>
        {
            var pieces = tables
                .Where(t => t.Length > 0)
                .Select(t =>
                {
                    int i = t.Schema.GetFieldIndex(f.Name);
                    return i < 0 ? Joins.Nulls(f.DataType, t.Length) : t.Column(i);
                })
                .ToList();
            return pieces.Count switch
            {
                0 => Joins.Nulls(f.DataType, 0),
                1 => pieces[0],
                _ => Concat(pieces, f.DataType),
            };
        }).ToList();
        var schema = new Schema.Builder();
        foreach (var f in fields) schema.Field(f);
        return new RecordBatch(schema.Build(), columns, n);
    }

    // ---------------------------------------------------------------------
    // Pivots

    /// <summary>
    /// Columns to rows, as tidyr's pivot_longer: each row becomes one row for
    /// each of the columns <paramref name="names"/>, with the column's name
    /// under <paramref name="namesTo"/> and its value under
    /// <paramref name="valuesTo"/>. The other columns are repeated. The
    /// columns have to be of one type; a null stays a null.
    /// </summary>
    public static RecordBatch PivotLonger(RecordBatch t, string[] names, string namesTo, string valuesTo)
    {
        if (names.Length == 0) throw new TableProblem("pivot-longer needs at least one column.");
        if (names.Distinct().Count() != names.Length) throw new TableProblem("a column is named twice.");
        var cols = names.Select(name => Column(t, name)).ToArray();
        var type = t.Schema.GetFieldByIndex(cols[0]).DataType;
        foreach (int c in cols)
        {
            var other = t.Schema.GetFieldByIndex(c).DataType;
            if (!Joins.SameType(type, other))
                throw new TableProblem($"'{names[0]}' is a column of {TypeText(type)} and "
                                       + $"'{t.Schema.GetFieldByIndex(c).Name}' of {TypeText(other)}: "
                                       + "the values of one column have to be of one type.");
        }
        var moved = new HashSet<int>(cols);
        var kept = Enumerable.Range(0, t.ColumnCount).Where(i => !moved.Contains(i)).ToArray();
        var keptNames = kept.Select(i => t.Schema.GetFieldByIndex(i).Name).ToHashSet();
        if (namesTo == valuesTo) throw new TableProblem($"the names and the values are both to go in '{namesTo}'.");
        foreach (var name in new[] { namesTo, valuesTo })
            if (keptNames.Contains(name)) throw new TableProblem($"the table has a column '{name}' already.");

        int n = t.Length, k = cols.Length;
        long total = (long)n * k;
        if (total > int.MaxValue) throw new TableProblem($"{n} rows of {k} columns are too many rows.");
        int m = (int)total;
        // Row i, column j of the result is at i * k + j: the kept cells of
        // row i, and the cell of column j, which is at j * n + i of the
        // columns put one after the other.
        var keptRows = new int[m];
        var valueRows = new int[m];
        for (int i = 0, at = 0; i < n; i++)
            for (int j = 0; j < k; j++, at++)
            {
                keptRows[at] = i;
                valueRows[at] = j * n + i;
            }

        var fields = new List<Field>();
        var columns = new List<IArrowArray>();
        var keptTable = Query.Take(Select(t, kept), keptRows, m);
        for (int i = 0; i < kept.Length; i++)
        {
            fields.Add(keptTable.Schema.GetFieldByIndex(i));
            columns.Add(keptTable.Column(i));
        }

        fields.Add(new Field(namesTo, StringType.Default, true));
        columns.Add(Repeated(names, n));

        var values = k == 1 ? t.Column(cols[0]) : Concat(cols.Select(t.Column).ToList(), type);
        var valueTable = new RecordBatch(new Schema.Builder().Field(new Field(valuesTo, type, true)).Build(),
                                         new[] { values }, values.Length);
        fields.Add(new Field(valuesTo, type, true));
        columns.Add(Query.Take(valueTable, valueRows, m).Column(0));

        var schema = new Schema.Builder();
        foreach (var f in fields) schema.Field(f);
        return new RecordBatch(schema.Build(), columns, m);
    }

    /// <summary>
    /// Rows to columns, as tidyr's pivot_wider: one row for each distinct
    /// value of the other columns, in the order of their first rows, and one
    /// column for each distinct string of <paramref name="namesFrom"/>, in
    /// the order they first come, with the values of
    /// <paramref name="valuesFrom"/>. A cell that no row gives is a null. Two
    /// rows of the same id and name are a TableProblem, since a cell holds one
    /// value.
    /// </summary>
    public static RecordBatch PivotWider(RecordBatch t, string namesFrom, string valuesFrom)
    {
        if (namesFrom == valuesFrom) throw new TableProblem($"the names and the values are both from '{namesFrom}'.");
        int nc = Column(t, namesFrom), vc = Column(t, valuesFrom);
        // The name of each row, null at a null.
        Func<int, string?> nameAt = t.Column(nc) switch
        {
            StringArray s => r => s.IsNull(r) ? null : s.GetString(r),
            DictionaryArray d when Categories.Is(d) =>
                r => d.IsNull(r) ? null : System.Text.Encoding.UTF8.GetString(Categories.LevelBytes(d, r)),
            _ => throw new TableProblem($"'{namesFrom}' is a column of {TypeText(t.Schema.GetFieldByIndex(nc).DataType)}; "
                                        + "the names of columns have to be strings."),
        };
        var ids = Enumerable.Range(0, t.ColumnCount).Where(i => i != nc && i != vc).ToArray();
        foreach (int i in ids)
        {
            var f = t.Schema.GetFieldByIndex(i);
            if (!Joins.KeyType(f.DataType))
                throw new TableProblem($"'{f.Name}' is a column of {TypeText(f.DataType)}, which cannot be a key.");
        }

        int n = t.Length;
        var groupOf = Groups(ids.Select(t.Column).ToArray(), n, out var firsts);
        int rows = firsts.Count;

        // The row of each cell of each new column, -1 where no row gives one.
        var nameIndex = new Dictionary<string, int>();
        var cellRows = new List<int[]>();
        var newNames = new List<string>();
        for (int r = 0; r < n; r++)
        {
            var name = nameAt(r)
                ?? throw new TableProblem($"'{namesFrom}' has a null in row {r}, and a column needs a name.");
            if (!nameIndex.TryGetValue(name, out int c))
            {
                c = cellRows.Count;
                nameIndex[name] = c;
                newNames.Add(name);
                var cells = new int[rows];
                System.Array.Fill(cells, -1);
                cellRows.Add(cells);
            }
            ref int cell = ref cellRows[c][groupOf[r]];
            if (cell >= 0)
                throw new TableProblem($"rows {cell} and {r} have the same values in the other columns, "
                                       + $"and both '{name}' in '{namesFrom}': a cell holds one value.");
            cell = r;
        }

        var idNames = ids.Select(i => t.Schema.GetFieldByIndex(i).Name).ToHashSet();
        foreach (var name in newNames)
            if (idNames.Contains(name)) throw new TableProblem($"the table has a column '{name}' already.");

        var fields = new List<Field>();
        var columns = new List<IArrowArray>();
        var idTable = Query.Take(Select(t, ids), firsts.ToArray(), rows);
        for (int i = 0; i < ids.Length; i++)
        {
            fields.Add(idTable.Schema.GetFieldByIndex(i));
            columns.Add(idTable.Column(i));
        }
        var valueTable = Select(t, new[] { vc });
        var type = t.Schema.GetFieldByIndex(vc).DataType;
        for (int c = 0; c < newNames.Count; c++)
        {
            fields.Add(new Field(newNames[c], type, true));
            columns.Add(Joins.TakeOrNull(valueTable, cellRows[c])[0]);
        }
        var schema = new Schema.Builder();
        foreach (var f in fields) schema.Field(f);
        return new RecordBatch(schema.Build(), columns, rows);
    }

    // ---------------------------------------------------------------------
    // Columns put one after the other

    // The cells of the columns, one column after the other. A copy of each
    // buffer, rather than one of each cell, as ArrowArrayConcatenator makes.
    // The columns are of the type.
    private static IArrowArray Concat(List<IArrowArray> pieces, IArrowType type)
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
                for (int i = 0; i < d.Length; i++, at++) bits[at >> 3] |= (byte)(1 << (at & 7));
            else
            {
                var span = from.Span;
                for (int i = 0; i < d.Length; i++, at++)
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
            int first = o[0], length = o[d.Length] - first;
            for (int i = 0; i < d.Length; i++) offsets[at + i] = end + o[i] - first;
            d.Buffers[2].Span.Slice(first, length).CopyTo(bytes.AsSpan(end));
            at += d.Length;
            end += length;
        }
        offsets[n] = end;
        return new ArrayData(type, n, nulls, 0,
            new[] { validity, new ArrowBuffer(offsetBytes), new ArrowBuffer(bytes) });
    }

    // A column of the names, all of them for each of `times` rows.
    private static IArrowArray Repeated(string[] names, int times)
    {
        var encoded = names.Select(System.Text.Encoding.UTF8.GetBytes).ToArray();
        long size = (long)encoded.Sum(e => e.Length) * times;
        if (size > int.MaxValue) throw new TableProblem($"{size} bytes of names are too many for one column.");
        int n = names.Length * times;
        var offsetBytes = GC.AllocateUninitializedArray<byte>((n + 1) * sizeof(int));
        var offsets = MemoryMarshal.Cast<byte, int>(offsetBytes.AsSpan());
        var bytes = GC.AllocateUninitializedArray<byte>((int)size);
        int at = 0, end = 0;
        for (int i = 0; i < times; i++)
            foreach (var e in encoded)
            {
                offsets[at++] = end;
                e.CopyTo(bytes, end);
                end += e.Length;
            }
        offsets[n] = end;
        return new StringArray(n, new ArrowBuffer(offsetBytes),
                               new ArrowBuffer(bytes), ArrowBuffer.Empty, 0);
    }

    // The table of the columns at the indices, without a copy.
    internal static RecordBatch Select(RecordBatch t, int[] indices)
    {
        var schema = new Schema.Builder();
        foreach (int i in indices) schema.Field(t.Schema.GetFieldByIndex(i));
        return new RecordBatch(schema.Build(), indices.Select(t.Column).ToList(), t.Length);
    }
}
