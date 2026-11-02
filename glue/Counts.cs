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
        if (keys.Length == 1 && keyCols[0] is DictionaryArray cat && Categories.Is(cat))
            return CountCategory(t, columns[0], cat, name, n);
        if (n >= 2 * PartRows && TryCountSorted(t, columns, keyCols, name, n) is { } fast)
            return fast;

        var groupOf = Reshape.Groups(keyCols, n, out var firsts, out var stringIds);
        int rows = firsts.Count;
        var counts = CountGroups(groupOf, rows, n);

        var firstSpan = CollectionsMarshal.AsSpan(firsts);
        var order = Sorting.OrderRows(keyCols, new bool[keys.Length], firstSpan, stringIds, rows);
        var sortedFirsts = new int[rows];
        var countCol = MakeLongs(rows, out var sortedCounts);
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
        cells.Add(countCol);
        return new RecordBatch(schema.Build(), cells, rows);
    }

    private const int PartRows = 1 << 14;
    private const int ParallelCountLimit = 1 << 14;

    // When each key column's ids can be numbered in table-sort order and their
    // combined slots fit in DenseLimit, counting those slots directly yields
    // the groups already in table-sort order without compacting or sorting the
    // distinct groups afterward.
    private static RecordBatch? TryCountSorted(RecordBatch t, int[] columns, IArrowArray[] keyCols, string name, int n)
    {
        var colIds = new int[keyCols.Length][];
        var cards = new int[keyCols.Length];
        long total = 1;
        for (int k = 0; k < keyCols.Length; k++)
        {
            var ids = Reshape.TrySortedIds(keyCols[k], out int ck);
            if (ids == null) return null;
            if (ck == 0) ck = 1;
            total *= ck;
            if (total > Reshape.DenseLimit) return null;
            colIds[k] = ids;
            cards[k] = ck;
        }
        int totalSlots = (int)total;
        int parts = Math.Min(n / PartRows, Workers.Count);
        int size = (n + parts - 1) / parts;
        int[] slotIds = colIds[0];
        if (keyCols.Length > 2)
        {
            Workers.For(parts, p =>
            {
                for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
                {
                    int s = slotIds[r];
                    for (int k = 1; k < keyCols.Length; k++) s = s * cards[k] + colIds[k][r];
                    slotIds[r] = s;
                }
            });
        }
        var slotCounts = new int[totalSlots];
        var slotFirsts = GC.AllocateUninitializedArray<int>(totalSlots);
        if (totalSlots <= ParallelCountLimit)
        {
            System.Array.Fill(slotFirsts, int.MaxValue);
            var partCounts = new int[parts][];
            var partFirsts = new int[parts][];
            int[]? ids1 = keyCols.Length == 2 ? colIds[1] : null;
            int c1 = keyCols.Length == 2 ? cards[1] : 1;
            Workers.For(parts, p =>
            {
                var pc = new int[totalSlots];
                var pf = new int[totalSlots];
                System.Array.Fill(pf, int.MaxValue);
                int from = p * size, to = Math.Min(n, from + size);
                if (ids1 != null)
                {
                    for (int r = from; r < to; r++)
                    {
                        int s = slotIds[r] * c1 + ids1[r];
                        if (pc[s]++ == 0) pf[s] = r;
                    }
                }
                else
                {
                    for (int r = from; r < to; r++)
                    {
                        int s = slotIds[r];
                        if (pc[s]++ == 0) pf[s] = r;
                    }
                }
                partCounts[p] = pc;
                partFirsts[p] = pf;
            });
            for (int p = 0; p < parts; p++)
            {
                var pc = partCounts[p];
                var pf = partFirsts[p];
                for (int s = 0; s < totalSlots; s++)
                {
                    slotCounts[s] += pc[s];
                    if (pf[s] < slotFirsts[s]) slotFirsts[s] = pf[s];
                }
            }
        }
        else
        {
            int[]? ids1 = keyCols.Length == 2 ? colIds[1] : null;
            int c1 = keyCols.Length == 2 ? cards[1] : 1;
            Workers.For(parts, p =>
            {
                int from = p * size, to = Math.Min(n, from + size);
                if (ids1 != null)
                {
                    for (int r = from; r < to; r++)
                    {
                        int s = slotIds[r] * c1 + ids1[r];
                        if (Interlocked.Increment(ref slotCounts[s]) == 1) slotFirsts[s] = r;
                    }
                }
                else
                {
                    for (int r = from; r < to; r++)
                    {
                        int s = slotIds[r];
                        if (Interlocked.Increment(ref slotCounts[s]) == 1) slotFirsts[s] = r;
                    }
                }
            });
        }
        int rows;
        int[] sortedFirsts;
        IArrowArray countCol;
        if (totalSlots >= PartRows)
        {
            int slotSize = (totalSlots + parts - 1) / parts;
            var partNonZero = new int[parts];
            Workers.For(parts, p =>
            {
                int c = 0;
                for (int s = p * slotSize, to = Math.Min(totalSlots, s + slotSize); s < to; s++)
                    if (slotCounts[s] > 0) c++;
                partNonZero[p] = c;
            });
            rows = 0;
            for (int p = 0; p < parts; p++)
            {
                int c = partNonZero[p];
                partNonZero[p] = rows;
                rows += c;
            }
            sortedFirsts = GC.AllocateUninitializedArray<int>(rows);
            var countBytes = GC.AllocateUninitializedArray<byte>(rows * sizeof(long));
            Workers.For(parts, p =>
            {
                var countsSpan = MemoryMarshal.Cast<byte, long>(countBytes.AsSpan());
                int i = partNonZero[p];
                for (int s = p * slotSize, to = Math.Min(totalSlots, s + slotSize); s < to; s++)
                {
                    int c = slotCounts[s];
                    if (c > 0)
                    {
                        sortedFirsts[i] = slotFirsts[s];
                        countsSpan[i] = c;
                        i++;
                    }
                }
            });
            countCol = new Int64Array(new ArrowBuffer(countBytes), ArrowBuffer.Empty, rows, 0, 0);
        }
        else
        {
            rows = 0;
            for (int s = 0; s < totalSlots; s++)
                if (slotCounts[s] > 0) rows++;
            sortedFirsts = GC.AllocateUninitializedArray<int>(rows);
            countCol = MakeLongs(rows, out var countsSpan);
            for (int s = 0, i = 0; s < totalSlots; s++)
            {
                int c = slotCounts[s];
                if (c > 0)
                {
                    sortedFirsts[i] = slotFirsts[s];
                    countsSpan[i] = c;
                    i++;
                }
            }
        }
        var sorted = Query.Take(Reshape.Select(t, columns), sortedFirsts, rows);
        var schema = new Schema.Builder();
        foreach (var f in sorted.Schema.FieldsList) schema.Field(f);
        schema.Field(new Field(name, Int64Type.Default, true));
        var cells = sorted.Arrays.ToList();
        cells.Add(countCol);
        return new RecordBatch(schema.Build(), cells, rows);
    }

    // A single category key: its codes are already in table-sort order, with
    // nulls last, so counting the codes directly needs no group compaction or
    // sort.
    private static RecordBatch CountCategory(RecordBatch t, int colIdx, DictionaryArray cat, string name, int n)
    {
        int levels = cat.Dictionary.Length;
        int slots = cat.NullCount == 0 ? levels : levels + 1;
        var counts = new long[slots];
        var firstRow = new int[slots];
        System.Array.Fill(firstRow, int.MaxValue);
        int parts = n >= 2 * PartRows && slots <= ParallelCountLimit ? Math.Min(n / PartRows, Workers.Count) : 1;
        if (parts >= 2)
        {
            int size = (n + parts - 1) / parts;
            var partCounts = new int[parts][];
            var partFirsts = new int[parts][];
            var valMem = cat.Indices.Data.Buffers[1].Memory;
            int valOff = cat.Indices.Data.Offset;
            var validMem = cat.NullCount == 0 ? ReadOnlyMemory<byte>.Empty : cat.Data.Buffers[0].Memory;
            int validOff = cat.Offset;
            Workers.For(parts, p =>
            {
                var pc = new int[slots];
                var pf = new int[slots];
                System.Array.Fill(pf, int.MaxValue);
                var codes = MemoryMarshal.Cast<byte, int>(valMem.Span).Slice(valOff);
                var valid = validMem.Span;
                int from = p * size, to = Math.Min(n, from + size);
                if (valid.IsEmpty)
                {
                    for (int r = from; r < to; r++)
                    {
                        int c = codes[r];
                        if (pc[c]++ == 0) pf[c] = r;
                    }
                }
                else
                {
                    for (int r = from; r < to; r++)
                    {
                        int bit = validOff + r;
                        int c = (valid[bit >> 3] & (1 << (bit & 7))) == 0 ? levels : codes[r];
                        if (pc[c]++ == 0) pf[c] = r;
                    }
                }
                partCounts[p] = pc;
                partFirsts[p] = pf;
            });
            for (int p = 0; p < parts; p++)
            {
                var pc = partCounts[p];
                var pf = partFirsts[p];
                for (int s = 0; s < slots; s++)
                {
                    counts[s] += pc[s];
                    if (pf[s] < firstRow[s]) firstRow[s] = pf[s];
                }
            }
        }
        else
        {
            var codes = ((Int32Array)cat.Indices).Values;
            if (cat.NullCount == 0)
            {
                for (int r = 0; r < n; r++)
                {
                    int c = codes[r];
                    if (counts[c]++ == 0) firstRow[c] = r;
                }
            }
            else
            {
                var valid = cat.Data.Buffers[0].Span;
                int off = cat.Offset;
                for (int r = 0; r < n; r++)
                {
                    int bit = off + r;
                    int c = (valid[bit >> 3] & (1 << (bit & 7))) == 0 ? levels : codes[r];
                    if (counts[c]++ == 0) firstRow[c] = r;
                }
            }
        }
        int rows = 0;
        for (int s = 0; s < slots; s++)
            if (counts[s] > 0) rows++;
        var sortedFirsts = new int[rows];
        var sortedCounts = new long[rows];
        for (int s = 0, i = 0; s < slots; s++)
            if (counts[s] > 0)
            {
                sortedFirsts[i] = firstRow[s];
                sortedCounts[i] = counts[s];
                i++;
            }
        var sorted = Query.Take(Reshape.Select(t, new[] { colIdx }), sortedFirsts, rows);
        var schema = new Schema.Builder()
            .Field(sorted.Schema.GetFieldByIndex(0))
            .Field(new Field(name, Int64Type.Default, true))
            .Build();
        return new RecordBatch(schema, new[] { sorted.Column(0), Longs(sortedCounts) }, rows);
    }

    private static long[] CountGroups(int[] groupOf, int rows, int n)
    {
        var counts = new long[rows];
        if (n >= 2 * PartRows && rows <= ParallelCountLimit)
        {
            int parts = Math.Min(n / PartRows, Workers.Count);
            int size = (n + parts - 1) / parts;
            var partCounts = new int[parts][];
            Workers.For(parts, p =>
            {
                var pc = new int[rows];
                for (int r = p * size, to = Math.Min(n, r + size); r < to; r++) pc[groupOf[r]]++;
                partCounts[p] = pc;
            });
            for (int p = 0; p < parts; p++)
            {
                var pc = partCounts[p];
                for (int g = 0; g < rows; g++) counts[g] += pc[g];
            }
        }
        else
        {
            foreach (int g in groupOf) counts[g]++;
        }
        return counts;
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
            int cCount = allColFirsts.Count, rCount = allRowFirsts.Count;
            if (n >= 2 * PartRows && (long)cCount * rCount <= ParallelCountLimit)
            {
                int parts = Math.Min(n / PartRows, Workers.Count);
                int size = (n + parts - 1) / parts;
                var partCounts = new int[parts][];
                Workers.For(parts, p =>
                {
                    var pc = new int[cCount * rCount];
                    for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
                        pc[colOf[r] * rCount + rowOf[r]]++;
                    partCounts[p] = pc;
                });
                for (int p = 0; p < parts; p++)
                {
                    var pc = partCounts[p];
                    for (int c = 0; c < cCount; c++)
                    {
                        var colCounts = counts[c];
                        int baseIdx = c * rCount;
                        for (int rg = 0; rg < rCount; rg++) colCounts[rg] += pc[baseIdx + rg];
                    }
                }
            }
            else if (n >= 2 * PartRows && (long)cCount * rCount <= Reshape.DenseLimit)
            {
                int parts = Math.Min(n / PartRows, Workers.Count);
                int size = (n + parts - 1) / parts;
                var flat = new int[cCount * rCount];
                Workers.For(parts, p =>
                {
                    for (int r = p * size, to = Math.Min(n, r + size); r < to; r++)
                        Interlocked.Increment(ref flat[colOf[r] * rCount + rowOf[r]]);
                });
                for (int c = 0; c < cCount; c++)
                {
                    var colCounts = counts[c];
                    int baseIdx = c * rCount;
                    for (int rg = 0; rg < rCount; rg++) colCounts[rg] = flat[baseIdx + rg];
                }
            }
            else
            {
                for (int r = 0; r < n; r++) counts[colOf[r]][rowOf[r]]++;
            }
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
            var colArray = MakeLongs(rowOrder.Length, out var sortedColCounts);
            for (int i = 0; i < rowOrder.Length; i++) sortedColCounts[i] = colCounts[activeRowGroups[rowOrder[i]]];
            cells.Add(colArray);
        }
        return new RecordBatch(schema.Build(), cells, rowOrder.Length);
    }

    private static IArrowArray MakeLongs(int n, out Span<long> span)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(n * sizeof(long));
        span = MemoryMarshal.Cast<byte, long>(bytes.AsSpan());
        return new Int64Array(new ArrowBuffer(bytes), ArrowBuffer.Empty, n, 0, 0);
    }

    private static IArrowArray Longs(long[] values) =>
        new Int64Array(new ArrowBuffer(MemoryMarshal.AsBytes(values.AsSpan()).ToArray()),
                       ArrowBuffer.Empty, values.Length, 0, 0);
}
