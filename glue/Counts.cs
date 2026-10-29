/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Counts: how many rows have each value of some columns. Count is dplyr's
// count, a row for each distinct value; Crosstab is R's table of two
// columns, a row for each value of one and a column for each value of the
// other. The rows of equal keys are found as in Reshape.Distinct.

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
        long[] counts;
        RecordBatch firstRows;
        if (keys.Length == 0)
        {
            counts = new[] { (long)n };
            firstRows = new RecordBatch(new Schema.Builder().Build(), new List<IArrowArray>(), 1);
        }
        else
        {
            var groupOf = Reshape.Groups(columns.Select(t.Column).ToArray(), n, out var firsts);
            counts = new long[firsts.Count];
            foreach (int g in groupOf) counts[g]++;
            firstRows = Query.Take(Reshape.Select(t, columns), firsts.ToArray(), firsts.Count);
        }

        int rows = counts.Length;
        var order = Sorting.Order(Enumerable.Range(0, keys.Length).Select(firstRows.Column).ToArray(),
                                  new bool[keys.Length], rows);
        var sorted = Query.Take(firstRows, order, rows);
        var schema = new Schema.Builder();
        foreach (var f in sorted.Schema.FieldsList) schema.Field(f);
        schema.Field(new Field(name, Int64Type.Default, true));
        var cells = sorted.Arrays.ToList();
        cells.Add(Longs(order.Select(g => counts[g]).ToArray()));
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
        var both = Reshape.Select(t, keys);
        // The rows with no null in a key.
        var rowCol = both.Column(0);
        var colCol = both.Column(1);
        if (rowCol.NullCount != 0 || colCol.NullCount != 0)
        {
            var valid = Enumerable.Range(0, both.Length).Where(r => rowCol.IsValid(r) && colCol.IsValid(r)).ToArray();
            both = Query.Take(both, valid, valid.Length);
            rowCol = both.Column(0);
            colCol = both.Column(1);
        }
        int n = both.Length;
        var rowOf = Reshape.Groups(new[] { rowCol }, n, out var rowFirsts);
        var colOf = Reshape.Groups(new[] { colCol }, n, out var colFirsts);
        var counts = new long[colFirsts.Count][];
        for (int c = 0; c < counts.Length; c++) counts[c] = new long[rowFirsts.Count];
        for (int r = 0; r < n; r++) counts[colOf[r]][rowOf[r]]++;

        // The groups of each key in the order of their values.
        var rowValues = Query.Take(Reshape.Select(both, new[] { 0 }), rowFirsts.ToArray(), rowFirsts.Count);
        var rowOrder = Sorting.Order(new[] { rowValues.Column(0) }, new[] { false }, rowFirsts.Count);
        var colValues = Query.Take(Reshape.Select(both, new[] { 1 }), colFirsts.ToArray(), colFirsts.Count);
        var colOrder = Sorting.Order(new[] { colValues.Column(0) }, new[] { false }, colFirsts.Count);

        var schema = new Schema.Builder();
        var cells = new List<IArrowArray>();
        var sortedRows = Query.Take(rowValues, rowOrder, rowOrder.Length);
        schema.Field(sortedRows.Schema.GetFieldByIndex(0));
        cells.Add(sortedRows.Column(0));
        var names = new HashSet<string> { rowKey };
        foreach (int c in colOrder)
        {
            var name = Columns.CellText(colValues.Column(0), c, -1);
            if (!names.Add(name))
                throw new TableProblem($"'{colKey}' has the value '{name}', and the table has a column of that name already.");
            schema.Field(new Field(name, Int64Type.Default, true));
            cells.Add(Longs(rowOrder.Select(g => counts[c][g]).ToArray()));
        }
        return new RecordBatch(schema.Build(), cells, rowOrder.Length);
    }

    private static IArrowArray Longs(long[] values) =>
        new Int64Array(new ArrowBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray()),
                       ArrowBuffer.Empty, values.Length, 0, 0);
}
