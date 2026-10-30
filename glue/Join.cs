/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Join: the rows of two tables whose keys are equal, as dplyr's joins give
// them.
//
// The rows of the right table are put in a hash table by their keys. Each row
// of the left table then finds the right rows with the same keys. One key of
// integers (or dates or timestamps) needs no hash: when the right keys are
// dense, an array by the key's distance from the least has the rows of each
// key; when both tables are sorted by the key and the right keys are unique,
// the two are merged. A side whose rows come out as they are, each once and
// in order, is not copied. The result
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

        int ln = left.Length;
        var leftRows = new List<int>(ln);
        var rightRows = new List<int>(kind == 0 || kind == 1 ? ln : 0);
        if (lk.Length == 1 && lk[0] is DictionaryArray lc && rk[0] is DictionaryArray rc
            && Categories.Is(lc) && Categories.Is(rc))
            MatchCategories(lc, rc, kind, leftRows, rightRows);
        else if (!(lk.Length == 1 && MatchIntegers(lk[0], rk[0], kind, leftRows, rightRows)))
            MatchHashes(lk, rk, kind, leftRows, rightRows);

        int n = leftRows.Count;
        var lrows = leftRows.ToArray();
        if (kind >= 2) return InOrder(lrows, ln) ? left : Query.Take(left, lrows, n);

        var rrows = rightRows.ToArray();
        var fields = new List<Field>();
        var columns = new List<IArrowArray>();
        var keySet = new HashSet<string>(keys);
        var leftNames = left.Schema.FieldsList.Select(f => f.Name).Where(x => !keySet.Contains(x)).ToHashSet();
        var rightNames = right.Schema.FieldsList.Select(f => f.Name).Where(x => !keySet.Contains(x)).ToHashSet();
        var takenLeft = InOrder(lrows, ln) ? left : Query.Take(left, lrows, n);
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
        var takenRight = InOrder(rrows, right.Length)
            ? Enumerable.Range(0, right.ColumnCount).Select(right.Column).ToArray()
            : TakeOrNull(right, rrows);
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

    // The rows that match, by the hash of their keys. The right rows are
    // put in a hash table: the first row of each hash, and the next row with
    // the same hash. The rows are put in from the last, each before the ones
    // there, so each chain is in the order of the rows.
    private static void MatchHashes(IArrowArray[] lk, IArrowArray[] rk, int kind, List<int> leftRows, List<int> rightRows)
    {
        int rn = rk[0].Length;
        var next = new int[rn];
        var rh = HashesInParts(rk, rn);
        // The hash table in parts by the top bits of the hash, each built on
        // a worker: a part has the rows of its hashes, from the last, so each
        // chain is still in the order of the rows. A row is in one part, so
        // the parts write `next` at other rows.
        var byPart = new List<int>[HashParts];
        for (int q = 0; q < HashParts; q++) byPart[q] = new List<int>();
        for (int r = 0; r < rn; r++) byPart[(int)(rh[r] >> HashPartShift)].Add(r);
        var tables = new Dictionary<ulong, int>[HashParts];
        Workers.For(HashParts, q =>
        {
            var rows = byPart[q];
            var heads = new Dictionary<ulong, int>(rows.Count);
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                int r = rows[i];
                ref int head = ref CollectionsMarshal.GetValueRefOrAddDefault(heads, rh[r], out bool there);
                next[r] = there ? head : -1;
                head = r;
            }
            tables[q] = heads;
        });
        var same = lk.Select((a, k) => Equality(a, rk[k])).ToArray();

        // The left rows in parts on bjostat's workers, each with lists of
        // its own, put one after the other in the order of the parts. The
        // hash table is only read here, which a Dictionary allows on many
        // threads.
        int ln = lk[0].Length;
        int parts = (ln + PartRows - 1) / PartRows;
        var partLeft = new List<int>[parts];
        var partRight = new List<int>[parts];
        Workers.For(parts, p =>
        {
            int from = p * PartRows, to = Math.Min(ln, from + PartRows);
            var lh = Hashes(lk.Select(a => ArrowArrayFactory.Slice(a, from, to - from)).ToArray(), to - from);
            var lrows = new List<int>(to - from);
            var rrows = new List<int>(kind == 0 || kind == 1 ? to - from : 0);
            for (int l = from; l < to; l++)
            {
                bool found = false;
                ulong h = lh[l - from];
                if (tables[(int)(h >> HashPartShift)].TryGetValue(h, out int r))
                {
                    for (; r >= 0; r = next[r])
                    {
                        if (!SameKeys(same, l, r)) continue;
                        found = true;
                        if (kind >= 2) break;
                        lrows.Add(l);
                        rrows.Add(r);
                    }
                }
                Unmatched(l, found, kind, lrows, rrows);
            }
            partLeft[p] = lrows;
            partRight[p] = rrows;
        });
        for (int p = 0; p < parts; p++)
        {
            leftRows.AddRange(partLeft[p]);
            rightRows.AddRange(partRight[p]);
        }
    }

    // The rows of a part of a join's left table.
    private const int PartRows = 1 << 16;

    // Whether the rows are each row of a table of n rows, once and in order:
    // then the table's columns are the result's as they are.
    private static bool InOrder(int[] rows, int n)
    {
        if (rows.Length != n) return false;
        for (int i = 0; i < rows.Length; i++)
            if (rows[i] != i) return false;
        return true;
    }

    // ---------------------------------------------------------------------
    // One key of integers

    // The keys as longs, when they are integers, dates or timestamps: the
    // types whose equal keys are equal numbers.
    private static long[]? Longs(IArrowArray a) => a switch
    {
        Int64Array x => x.Values.ToArray(),
        Int32Array x => Widen(x.Values, v => v),
        Int16Array x => Widen(x.Values, v => v),
        UInt8Array x => Widen(x.Values, v => v),
        Date32Array x => Widen(x.Values, v => v),
        TimestampArray x => x.Values.ToArray(),
        _ => null,
    };

    private static long[] Widen<T>(ReadOnlySpan<T> values, Func<T, long> widen)
    {
        var w = new long[values.Length];
        for (int i = 0; i < w.Length; i++) w[i] = widen(values[i]);
        return w;
    }

    // The rows that match by one key of integers, without hashes, or false
    // when the keys are neither dense nor sorted and unique, and the hash
    // join is to be used. The result is that of MatchHashes.
    private static bool MatchIntegers(IArrowArray lk, IArrowArray rk, int kind, List<int> leftRows, List<int> rightRows)
    {
        var lv = Longs(lk);
        var rv = Longs(rk);
        if (lv == null || rv == null) return false;
        int rn = rv.Length;
        bool rightNulls = rk.NullCount != 0, leftNulls = lk.NullCount != 0;

        long min = long.MaxValue, max = long.MinValue;
        bool rightSorted = true;
        long previous = long.MinValue;
        bool first = true;
        for (int r = 0; r < rn; r++)
        {
            if (rightNulls && rk.IsNull(r)) { rightSorted = false; continue; }
            long v = rv[r];
            if (v < min) min = v;
            if (v > max) max = v;
            if (!first && v <= previous) rightSorted = false;
            previous = v;
            first = false;
        }

        // Dense: room for each key from the least to the greatest, at most
        // twice the right rows and some more.
        if (rn > 0 && !first && (ulong)(max - min) <= 2UL * (ulong)rn + 1024)
        {
            MatchDense(lk, rk, lv, rv, min, (int)(max - min) + 1, kind, leftRows, rightRows);
            return true;
        }
        if (rn > 0 && rightSorted && !rightNulls && !leftNulls && Ascending(lv))
        {
            MatchMerged(lv, rv, kind, leftRows, rightRows);
            return true;
        }
        return false;
    }

    private static bool Ascending(long[] v)
    {
        for (int i = 1; i < v.Length; i++)
            if (v[i] < v[i - 1]) return false;
        return true;
    }

    // The right rows in chains by the key's distance from the least, in an
    // array, from the last row so that each chain is in the order of the
    // rows; the slot after the last key is the nulls'. The left rows in
    // parts on the workers, as in MatchHashes.
    private static void MatchDense(IArrowArray lk, IArrowArray rk, long[] lv, long[] rv, long min, int slots,
                                   int kind, List<int> leftRows, List<int> rightRows)
    {
        int rn = rv.Length, nullSlot = slots;
        var heads = new int[slots + 1];
        System.Array.Fill(heads, -1);
        var next = new int[rn];
        bool rightNulls = rk.NullCount != 0, leftNulls = lk.NullCount != 0;
        for (int r = rn - 1; r >= 0; r--)
        {
            int slot = rightNulls && rk.IsNull(r) ? nullSlot : (int)(rv[r] - min);
            next[r] = heads[slot];
            heads[slot] = r;
        }
        InLeftParts(lv.Length, kind, leftRows, rightRows, (from, to, lrows, rrows) =>
        {
            for (int l = from; l < to; l++)
            {
                int r;
                if (leftNulls && lk.IsNull(l)) r = heads[nullSlot];
                else
                {
                    long d = lv[l] - min;
                    r = (ulong)d < (ulong)slots ? heads[(int)d] : -1;
                }
                bool found = r >= 0;
                if (kind < 2)
                    for (; r >= 0; r = next[r])
                    {
                        lrows.Add(l);
                        rrows.Add(r);
                    }
                Unmatched(l, found, kind, lrows, rrows);
            }
        });
    }

    // Both keys ascending, the right ones unique and neither null: each left
    // row has at most one right row, found by walking both in order. A part
    // of the left rows finds where to start in the right rows by halving.
    private static void MatchMerged(long[] lv, long[] rv, int kind, List<int> leftRows, List<int> rightRows)
    {
        InLeftParts(lv.Length, kind, leftRows, rightRows, (from, to, lrows, rrows) =>
        {
            if (from >= to) return;
            int r = System.Array.BinarySearch(rv, lv[from]);
            if (r < 0) r = ~r;
            for (int l = from; l < to; l++)
            {
                long v = lv[l];
                while (r < rv.Length && rv[r] < v) r++;
                bool found = r < rv.Length && rv[r] == v;
                if (found && kind < 2)
                {
                    lrows.Add(l);
                    rrows.Add(r);
                }
                Unmatched(l, found, kind, lrows, rrows);
            }
        });
    }

    // Runs `match` on the left rows in parts on the workers, each with lists
    // of its own, and puts the lists one after the other in the order of the
    // parts.
    private static void InLeftParts(int ln, int kind, List<int> leftRows, List<int> rightRows,
                                    Action<int, int, List<int>, List<int>> match)
    {
        int parts = (ln + PartRows - 1) / PartRows;
        var partLeft = new List<int>[parts];
        var partRight = new List<int>[parts];
        Workers.For(parts, p =>
        {
            int from = p * PartRows, to = Math.Min(ln, from + PartRows);
            var lrows = new List<int>(to - from);
            var rrows = new List<int>(kind == 0 || kind == 1 ? to - from : 0);
            match(from, to, lrows, rrows);
            partLeft[p] = lrows;
            partRight[p] = rrows;
        });
        for (int p = 0; p < parts; p++)
        {
            leftRows.AddRange(partLeft[p]);
            rightRows.AddRange(partRight[p]);
        }
    }

    // The parts of a join's hash table: 64, by the top 6 bits of the hash.
    private const int HashParts = 64;
    private const int HashPartShift = 58;

    // The hashes of the rows, in parts on the workers.
    private static ulong[] HashesInParts(IArrowArray[] keys, int n)
    {
        if (n < 2 * PartRows) return Hashes(keys, n);
        var h = new ulong[n];
        int parts = (n + PartRows - 1) / PartRows;
        Workers.For(parts, p =>
        {
            int from = p * PartRows, to = Math.Min(n, from + PartRows);
            Hashes(keys.Select(a => ArrowArrayFactory.Slice(a, from, to - from)).ToArray(), to - from)
                .CopyTo(h, from);
        });
        return h;
    }

    // The rows that match, by one key that is a category in both tables:
    // each right level is given the left code of the same string once, and
    // the right rows are then in chains by left code, in an array rather than
    // a hash table. The slot after the last code is the nulls'.
    private static void MatchCategories(DictionaryArray left, DictionaryArray right, int kind,
                                        List<int> leftRows, List<int> rightRows)
    {
        var leftLevels = (StringArray)left.Dictionary;
        var rightLevels = (StringArray)right.Dictionary;
        int nullSlot = leftLevels.Length;
        var leftCode = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int c = 0; c < leftLevels.Length; c++) leftCode[leftLevels.GetString(c)] = c;
        // -1 for a right level that the left table does not have.
        var codeOf = new int[rightLevels.Length];
        for (int c = 0; c < rightLevels.Length; c++)
            codeOf[c] = leftCode.TryGetValue(rightLevels.GetString(c), out int lc) ? lc : -1;

        int rn = right.Length;
        var next = new int[rn];
        var heads = new int[nullSlot + 1];
        System.Array.Fill(heads, -1);
        var rcodes = ((Int32Array)right.Indices).Values;
        for (int r = rn - 1; r >= 0; r--)
        {
            int slot = right.IsNull(r) ? nullSlot : codeOf[rcodes[r]];
            if (slot < 0) continue;
            next[r] = heads[slot];
            heads[slot] = r;
        }

        int ln = left.Length;
        var lcodes = ((Int32Array)left.Indices).Values;
        bool nulls = left.NullCount != 0;
        for (int l = 0; l < ln; l++)
        {
            int r = heads[nulls && left.IsNull(l) ? nullSlot : lcodes[l]];
            bool found = r >= 0;
            if (kind < 2)
                for (; r >= 0; r = next[r])
                {
                    leftRows.Add(l);
                    rightRows.Add(r);
                }
            Unmatched(l, found, kind, leftRows, rightRows);
        }
    }

    // A left row with no match in a left join, or one a semi or anti join keeps.
    private static void Unmatched(int l, bool found, int kind, List<int> leftRows, List<int> rightRows)
    {
        if (kind == 1 && !found)
        {
            leftRows.Add(l);
            rightRows.Add(-1);
        }
        else if ((kind == 2 && found) || (kind == 3 && !found))
            leftRows.Add(l);
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
            if (!KeyType(lt)) return $"'{k}' is a column of {Reshape.TypeText(lt)}, which cannot be a key.";
            if (!SameType(lt, rt))
                return $"'{k}' is a column of {Reshape.TypeText(lt)} in the left table and of {Reshape.TypeText(rt)} in the right.";
        }
        return "";
    }

    internal static bool KeyType(IArrowType t) =>
        t is DoubleType or FloatType or Int64Type or Int32Type or Int16Type or UInt8Type
            or StringType or BooleanType or Date32Type or TimestampType
        || Categories.IsType(t);

    internal static bool SameType(IArrowType a, IArrowType b) =>
        a.TypeId == b.TypeId
        && (a is not TimestampType ta || b is TimestampType tb && ta.Unit == tb.Unit && ta.Timezone == tb.Timezone)
        && (a is not DictionaryType || Categories.IsType(a) && Categories.IsType(b));

    // The hash of each row's keys. A null and a NaN each have one hash, and
    // -0.0 has that of 0.0, since they are equal keys.
    internal static ulong[] Hashes(IArrowArray[] keys, int n)
    {
        var h = new ulong[n];
        System.Array.Fill(h, 14695981039346656037UL);
        // The value of each row in one key, then mixed into its hash.
        var v = new ulong[n];
        foreach (var a in keys)
        {
            switch (a)
            {
                case DoubleArray d: Values(v, d.Values, x => double.IsNaN(x) ? 0x7FF8_0000_0000_0001UL : BitConverter.DoubleToUInt64Bits(x == 0.0 ? 0.0 : x)); break;
                case FloatArray f: Values(v, f.Values, x => float.IsNaN(x) ? 0x7FC0_0001UL : BitConverter.SingleToUInt32Bits(x == 0.0f ? 0.0f : x)); break;
                case Int64Array l: Values(v, l.Values, x => (ulong)x); break;
                case Int32Array i: Values(v, i.Values, x => (ulong)x); break;
                case Int16Array s: Values(v, s.Values, x => (ulong)x); break;
                case UInt8Array b: Values(v, b.Values, x => x); break;
                case Date32Array d: Values(v, d.Values, x => (ulong)x); break;
                case TimestampArray t: Values(v, t.Values, x => (ulong)x); break;
                case BooleanArray b:
                    for (int i = 0; i < n; i++) v[i] = b.GetValue(i) == true ? 1UL : 2UL;
                    break;
                case StringArray s:
                    for (int i = 0; i < n; i++) v[i] = StringHash(s.GetBytes(i));
                    break;
                case DictionaryArray { Dictionary: StringArray levels, Indices: Int32Array codes }:
                {
                    // The hash of each level once; a row's is that of its
                    // level, so two categories of other levels agree.
                    var levelHash = new ulong[levels.Length + 1];
                    for (int l = 0; l < levels.Length; l++) levelHash[l] = StringHash(levels.GetBytes(l));
                    var values = codes.Values;
                    for (int i = 0; i < n; i++)
                        v[i] = levelHash[(uint)values[i] < (uint)levels.Length ? values[i] : levels.Length];
                    break;
                }
                default:
                    throw new ArgumentException($"(bjostat table): a column of {a.Data.DataType.Name} cannot be a key");
            }
            // A null has one hash, whatever value is under it: after a left
            // join, that is the value of another row.
            if (a.NullCount != 0)
                for (int i = 0; i < n; i++)
                    if (a.IsNull(i)) v[i] = 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < n; i++) Step(ref h[i], v[i]);
        }
        return h;
    }

    private static ulong StringHash(ReadOnlySpan<byte> bytes)
    {
        var hc = new HashCode();
        hc.AddBytes(bytes);
        return (ulong)(uint)hc.ToHashCode() * 0x9E3779B97F4A7C15UL;
    }

    private static void Values<T>(ulong[] v, ReadOnlySpan<T> values, Func<T, ulong> value)
    {
        for (int i = 0; i < v.Length; i++) v[i] = value(values[i]);
    }

    private static void Step(ref ulong h, ulong v)
    {
        h = (h ^ v) * 1099511628211UL;
        h ^= h >> 29;
    }

    internal static bool SameKeys(Func<int, int, bool>[] same, int l, int r)
    {
        foreach (var f in same)
            if (!f(l, r)) return false;
        return true;
    }

    // Whether row i of a and row j of b have the same key, two nulls and two
    // NaNs being the same.
    internal static Func<int, int, bool> Equality(IArrowArray a, IArrowArray b)
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
            // Two categories of the same levels compare codes; others, the
            // levels' strings.
            (DictionaryArray x, DictionaryArray y) when Categories.Is(x) && Categories.Is(y) =>
                ReferenceEquals(x.Dictionary, y.Dictionary)
                    ? CodesEqual((Int32Array)x.Indices, (Int32Array)y.Indices)
                    : (i, j) => Categories.LevelBytes(x, i).SequenceEqual(Categories.LevelBytes(y, j)),
            _ => throw new ArgumentException($"(bjostat table): a column of {a.Data.DataType.Name} cannot be a key"),
        };
        if (a.NullCount == 0 && b.NullCount == 0) return values;
        return (i, j) =>
        {
            bool na = a.IsNull(i), nb = b.IsNull(j);
            return na || nb ? na && nb : values(i, j);
        };
    }

    private static Func<int, int, bool> CodesEqual(Int32Array x, Int32Array y) =>
        (i, j) => x.Values[i] == y.Values[j];

    // The columns of `t` at the rows; a row of -1 is a null in each column.
    internal static IArrowArray[] TakeOrNull(RecordBatch t, int[] rows)
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
    internal static IArrowArray HideMissing(IArrowArray a, int[] rows)
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
    internal static IArrowArray Nulls(IArrowType type, int n)
    {
        if (Categories.IsType(type)) return Categories.Nulls(n);
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
