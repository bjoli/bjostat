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
//
// Making categories and numbering strings is Bjolang, in (bjostat table).
// What is left here is for glue/Reshape.cs and glue/ColumnTypes.cs.

using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Bjostat;

public static class Categories
{
    /// <summary>The type of every category column.</summary>
    public static DictionaryType Type { get; } = new(Int32Type.Default, StringType.Default, false);

    /// <summary>Whether the column is a category: a dictionary of int codes and string levels.</summary>
    public static bool Is(IArrowArray a) =>
        a is DictionaryArray { Dictionary: StringArray, Indices: Int32Array };

    public static bool IsType(IArrowType t) =>
        t is DictionaryType { IndexType: Int32Type, ValueType: StringType };

    // The numbering of strings, which (bjostat table) installs when it is
    // initialised, before it calls any glue: its ids and first rows, the same
    // in the order of the strings' bytes, and the order of the bytes.
    private static Func<StringArray, int[][]>? _stringIds;
    private static Func<StringArray, int[][]>? _sortedStringIds;
    private static Func<StringArray, int[], int[]>? _inByteOrder;

    /// <summary>Sets the functions of (bjostat table) that number strings.</summary>
    public static void InstallNumbering(
        Func<StringArray, int[][]> stringIds,
        Func<StringArray, int[][]> sortedStringIds,
        Func<StringArray, int[], int[]> inByteOrder)
    {
        _stringIds = stringIds;
        _sortedStringIds = sortedStringIds;
        _inByteOrder = inByteOrder;
    }

    private static T Installed<T>(T? f) where T : class =>
        f ?? throw new InvalidOperationException("(bjostat table) has not installed the numbering of strings");

    /// <summary>
    /// A number for each distinct string, from 0 in the order of their first
    /// rows, and the id of each row; 0 at a null. <paramref name="firsts"/>
    /// gets the first row of each id.
    /// </summary>
    internal static int[] StringIds(StringArray s, out List<int> firsts)
    {
        var r = Installed(_stringIds)(s);
        firsts = new List<int>(r[1]);
        return r[0];
    }

    /// <summary>
    /// A number for each distinct string in the order of their UTF-8 bytes
    /// (0 to <paramref name="card"/> - 1, or with nulls at <paramref name="card"/> - 1).
    /// </summary>
    internal static int[] SortedStringIds(StringArray s, out int card)
    {
        var r = Installed(_sortedStringIds)(s);
        card = s.NullCount == 0 ? r[1].Length : r[1].Length + 1;
        return r[0];
    }

    /// <summary>
    /// The numbers of the strings whose first rows are <paramref name="firsts"/>,
    /// in the order of the strings' bytes.
    /// </summary>
    internal static int[] InByteOrder(StringArray s, List<int> firsts) =>
        Installed(_inByteOrder)(s, firsts.ToArray());

    internal static DictionaryArray Make(ArrowBuffer codes, ArrowBuffer validity, int nulls, int n, IReadOnlyList<string> levels)
    {
        var dictionary = new StringArray.Builder().AppendRange(levels).Build();
        var data = new ArrayData(Type, n, nulls, 0, new[] { validity, codes }, null, dictionary.Data);
        return new DictionaryArray(data);
    }

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
}
