/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Columns: what (bjostat table) cannot write in Bjolang yet.
//
// - FromArray and FromArrayAt make a column of an array of numbers. They
//   write the values as the bytes of an ArrowBuffer, and Bjolang cannot see
//   a span of numbers as a span of bytes.
// - Tables.IndexOf is used by the other files of the glue.
//
// The rest of what was here is Bjolang now, in src/table.bjo ("Columns and
// tables in Arrow").

using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Bjostat;

public static class Columns
{
    private static IArrowType ArrowTypeOf<T>() =>
        typeof(T) == typeof(double) ? DoubleType.Default
        : typeof(T) == typeof(float) ? FloatType.Default
        : typeof(T) == typeof(long) ? Int64Type.Default
        : typeof(T) == typeof(int) ? Int32Type.Default
        : typeof(T) == typeof(short) ? Int16Type.Default
        : typeof(T) == typeof(byte) ? UInt8Type.Default
        : throw new ArgumentException($"(bjostat table): a column of {typeof(T).Name} is not supported");

    /// <summary>
    /// A column of the values, copied. <paramref name="valid"/> is empty for a
    /// column with no nulls, or as long as the values, false at each null.
    /// </summary>
    public static PrimitiveArray<T> FromArray<T>(T[] values, bool[] valid) where T : struct, IEquatable<T>
    {
        CheckValid(values.Length, valid);
        // One copy of the values' bytes. ArrowBuffer.Builder's AppendRange
        // enumerates them one at a time, which was 2.6-4.4 ms of 600 000
        // longs where this is a block copy.
        var data = new ArrowBuffer(MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
        var bitmap = ArrowBuffer.Empty;
        int nulls = 0;

        if (valid.Length > 0)
        {
            var bits = new ArrowBuffer.BitmapBuilder(valid.Length);
            foreach (bool v in valid)
            {
                bits.Append(v);
                if (!v) nulls++;
            }
            bitmap = bits.Build();
        }

        var arrayData = new ArrayData(ArrowTypeOf<T>(), values.Length, nulls, 0, new[] { bitmap, data });
        return (PrimitiveArray<T>)ArrowArrayFactory.BuildArray(arrayData);
    }

    /// <summary>
    /// A column of values[at[i]] for each i, with no nulls: one pass, into
    /// the column's buffer.
    /// </summary>
    public static PrimitiveArray<T> FromArrayAt<T>(T[] values, int[] at) where T : struct, IEquatable<T>
    {
        var bytes = GC.AllocateUninitializedArray<byte>(at.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        var span = MemoryMarshal.Cast<byte, T>(bytes.AsSpan());
        for (int i = 0; i < at.Length; i++) span[i] = values[at[i]];
        var arrayData = new ArrayData(ArrowTypeOf<T>(), at.Length, 0, 0, new[] { ArrowBuffer.Empty, new ArrowBuffer(bytes) });
        return (PrimitiveArray<T>)ArrowArrayFactory.BuildArray(arrayData);
    }

    private static void CheckValid(int n, bool[] valid)
    {
        if (valid.Length != 0 && valid.Length != n)
            throw new ArgumentException($"(bjostat table): {valid.Length} validity flags for {n} values");
    }
}

public static class Tables
{
    /// <summary>
    /// The index of the first column of the name, or -1. Names are compared
    /// ordinally, character for character. Arrow's Schema.GetFieldIndex
    /// compares them in the current culture, which ignores characters such
    /// as a soft hyphen, so that "pri\u00ADce" found the column "price".
    /// </summary>
    public static int IndexOf(RecordBatch t, string name)
    {
        var fields = t.Schema.FieldsList;
        for (int i = 0; i < fields.Count; i++)
            if (string.Equals(fields[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}
