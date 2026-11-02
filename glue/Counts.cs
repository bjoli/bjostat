/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Counts: how many rows have each value of some columns. Count is dplyr's
// count, a row for each distinct value; Crosstab is R's table of two
// columns, a row for each value of one and a column for each value of the
// other. The rows of equal keys are found as in Reshape.Distinct.

using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Bjostat;

public static class Counts
{
    /// <summary>
    /// A row for each distinct value of the key columns, with the number of
    /// rows that have it in the column <paramref name="name"/>, in the order
    /// of the keys as table-sort gives it. A null is a value, as in dplyr.
    /// With no keys, one row of the number of rows.
    /// </summary>
    public static RecordBatch Count(RecordBatch t, string[] keys, string name)
    {
        if (keys.Contains(name)) throw new TableProblem($"the count and a key are both named '{name}'.");
        var columns = Reshape.KeyColumns(t, keys);
        int n = t.Length;
        if (keys.Length == 0)
        {
            var emptySchema = new Schema.Builder().Field(new Field(name, Int64Type.Default, true)).Build();
            return new RecordBatch(emptySchema, new[] { Longs(new[] { (long)n }) }, 1);
        }

        var keyCols = columns.Select(t.Column).ToArray();
        var groupOf = Reshape.Groups(keyCols, n, out var firsts, out var stringIds);
        int rows = firsts.Count;
        var counts = new long[rows];
        foreach (int g in groupOf) counts[g]++;

        var firstSpan = CollectionsMarshal.AsSpan(firsts);
        var order = Sorting.OrderRows(keyCols, new bool[keys.Length], firstSpan, stringIds, rows);
        var sortedFirsts = new int[rows];
        var sortedCounts = new long[rows];
        for (int i = 0; i < rows; i++)
        {
            int g = order[i];
            sortedFirsts[i] = firstSpan[g];
            sortedCounts[i] = counts[g];
        }
        var sorted = Query.Take(Reshape.Select(t, columns), sortedFirsts, rows);
        var schema = new Schema.Builder();
        foreach (var f in sorted.Schema.FieldsList) schema.Field(f);
        schema.Field(new Field(name, Int64Type.Default, true));
        var cells = sorted.Arrays.ToList();
        cells.Add(Longs(sortedCounts));
        return new RecordBatch(schema.Build(), cells, rows);
    }

    /// <summary>
    /// R's table of two columns: a row for each value of
    /// <paramref name="rowKey"/>, then a column for each value of
    /// <paramref name="colKey"/>, named by its text, with the number of rows
    /// that have both; 0 for a pair that no row has. Both are in the order of
    /// table-sort. A row with a null in either key is left out, as R does.
    /// </summary>
    public static RecordBatch Crosstab(RecordBatch t, string rowKey, string colKey)
    {
        if (rowKey == colKey) throw new TableProblem($"the rows and the columns are both of '{rowKey}'.");
        var keys = Reshape.KeyColumns(t, new[] { rowKey, colKey });
        var rowCol = t.Column(keys[0]);
        var colCol = t.Column(keys[1]);
        int n = t.Length;
        var rowOf = Reshape.Groups(new[] { rowCol }, n, out var allRowFirsts, out var rowStrIds);
        var colOf = Reshape.Groups(new[] { colCol }, n, out var allColFirsts, out var colStrIds);
        var counts = new long[allColFirsts.Count][];
        for (int c = 0; c < counts.Length; c++) counts[c] = new long[allRowFirsts.Count];

        List<int> rowFirsts, colFirsts;
        int[] activeRowGroups, activeColGroups;
        if (rowCol.NullCount == 0 && colCol.NullCount == 0)
        {
            for (int r = 0; r < n; r++) counts[colOf[r]][rowOf[r]]++;
            rowFirsts = allRowFirsts;
            colFirsts = allColFirsts;
            activeRowGroups = new int[rowFirsts.Count];
            for (int i = 0; i < activeRowGroups.Length; i++) activeRowGroups[i] = i;
            activeColGroups = new int[colFirsts.Count];
            for (int i = 0; i < activeColGroups.Length; i++) activeColGroups[i] = i;
        }
        else
        {
            var rowValid = rowCol.NullCount == 0 ? ReadOnlySpan<byte>.Empty : rowCol.Data.Buffers[0].Span;
            var colValid = colCol.NullCount == 0 ? ReadOnlySpan<byte>.Empty : colCol.Data.Buffers[0].Span;
            int rowOff = rowCol.Offset, colOff = colCol.Offset;
            for (int r = 0; r < n; r++)
            {
                int rb = rowOff + r, cb = colOff + r;
                if (!rowValid.IsEmpty && (rowValid[rb >> 3] & (1 << (rb & 7))) == 0) continue;
                if (!colValid.IsEmpty && (colValid[cb >> 3] & (1 << (cb & 7))) == 0) continue;
                counts[colOf[r]][rowOf[r]]++;
            }
            rowFirsts = new List<int>(allRowFirsts.Count);
            var rowKept = new List<int>(allRowFirsts.Count);
            for (int rg = 0; rg < allRowFirsts.Count; rg++)
            {
                for (int cg = 0; cg < allColFirsts.Count; cg++)
                    if (counts[cg][rg] != 0)
                    {
                        rowFirsts.Add(allRowFirsts[rg]);
                        rowKept.Add(rg);
                        break;
                    }
            }
            colFirsts = new List<int>(allColFirsts.Count);
            var colKept = new List<int>(allColFirsts.Count);
            for (int cg = 0; cg < allColFirsts.Count; cg++)
            {
                var colCounts = counts[cg];
                for (int rg = 0; rg < allRowFirsts.Count; rg++)
                    if (colCounts[rg] != 0)
                    {
                        colFirsts.Add(allColFirsts[cg]);
                        colKept.Add(cg);
                        break;
                    }
            }
            activeRowGroups = rowKept.ToArray();
            activeColGroups = colKept.ToArray();
        }

        // The groups of each key in the order of their values.
        var rowFirstSpan = CollectionsMarshal.AsSpan(rowFirsts);
        var rowOrder = Sorting.OrderRows(new[] { rowCol }, new[] { false }, rowFirstSpan, rowStrIds, rowFirsts.Count);
        var colFirstSpan = CollectionsMarshal.AsSpan(colFirsts);
        var colOrder = Sorting.OrderRows(new[] { colCol }, new[] { false }, colFirstSpan, colStrIds, colFirsts.Count);

        var sortedRowFirsts = new int[rowOrder.Length];
        for (int i = 0; i < rowOrder.Length; i++) sortedRowFirsts[i] = rowFirstSpan[rowOrder[i]];
        var sortedRows = Query.Take(Reshape.Select(t, new[] { keys[0] }), sortedRowFirsts, rowOrder.Length);

        var schema = new Schema.Builder();
        var cells = new List<IArrowArray>();
        schema.Field(sortedRows.Schema.GetFieldByIndex(0));
        cells.Add(sortedRows.Column(0));
        var names = new HashSet<string> { rowKey };
        foreach (int c in colOrder)
        {
            var name = Columns.CellText(colCol, colFirstSpan[c], -1);
            if (!names.Add(name))
                throw new TableProblem($"'{colKey}' has the value '{name}', and the table has a column of that name already.");
            schema.Field(new Field(name, Int64Type.Default, true));
            var colCounts = counts[activeColGroups[c]];
            var sortedColCounts = new long[rowOrder.Length];
            for (int i = 0; i < rowOrder.Length; i++) sortedColCounts[i] = colCounts[activeRowGroups[rowOrder[i]]];
            cells.Add(Longs(sortedColCounts));
        }
        return new RecordBatch(schema.Build(), cells, rowOrder.Length);
    }

    private static IArrowArray Longs(long[] values) =>
        new Int64Array(new ArrowBuffer(MemoryMarshal.AsBytes(values.AsSpan()).ToArray()),
                       ArrowBuffer.Empty, values.Length, 0, 0);
}
