/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Kernels: arithmetic on whole columns, as Arrow's compute functions do it.
//
// Every value is computed, a null's too, by TensorPrimitives, which is
// vectorized; the result is null where either operand is, by the AND of the
// validity bitmaps. There is no test a row. Arrow leaves the value under a
// null unspecified, and here it is what the arithmetic gave, of values that
// are themselves written: no memory that held something else is read.

using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Apache.Arrow;

namespace Bjostat;

public static class Kernels
{
    private delegate void Binary<T>(ReadOnlySpan<T> x, ReadOnlySpan<T> y, Span<T> into);

    private static PrimitiveArray<T> Apply<T>(PrimitiveArray<T> a, PrimitiveArray<T> b, Binary<T> op, string name)
        where T : struct, IEquatable<T>
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"(bjostat query): {name} of columns of {a.Length} and {b.Length} rows");
        int n = a.Length;
        var bytes = GC.AllocateUninitializedArray<byte>(n * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        op(a.Values, b.Values, MemoryMarshal.Cast<byte, T>(bytes.AsSpan()));
        var bitmap = BothValid(a.Data, b.Data, n, out int nulls);
        var data = new ArrayData(a.Data.DataType, n, nulls, 0, new[] { bitmap, new ArrowBuffer(bytes) });
        return (PrimitiveArray<T>)ArrowArrayFactory.BuildArray(data);
    }

    public static PrimitiveArray<T> Add<T>(PrimitiveArray<T> a, PrimitiveArray<T> b)
        where T : struct, IEquatable<T>, INumber<T> =>
        Apply(a, b, TensorPrimitives.Add, "column+");

    public static PrimitiveArray<T> Subtract<T>(PrimitiveArray<T> a, PrimitiveArray<T> b)
        where T : struct, IEquatable<T>, INumber<T> =>
        Apply(a, b, TensorPrimitives.Subtract, "column-");

    public static PrimitiveArray<T> Multiply<T>(PrimitiveArray<T> a, PrimitiveArray<T> b)
        where T : struct, IEquatable<T>, INumber<T> =>
        Apply(a, b, TensorPrimitives.Multiply, "column*");

    /// <summary>
    /// The quotients. An integer divided by 0 throws, as it does in a loop,
    /// a null's 0 too: divide doubles, or a column without zeros.
    /// </summary>
    public static PrimitiveArray<T> Divide<T>(PrimitiveArray<T> a, PrimitiveArray<T> b)
        where T : struct, IEquatable<T>, INumber<T> =>
        Apply(a, b, TensorPrimitives.Divide, "column/");

    /// <summary>
    /// The values as another number type, with the same nulls. A value that
    /// does not fit the new type throws, as <c>CreateChecked</c> does.
    /// </summary>
    public static PrimitiveArray<TTo> Cast<TFrom, TTo>(PrimitiveArray<TFrom> a)
        where TFrom : struct, IEquatable<TFrom>, INumber<TFrom>
        where TTo : struct, IEquatable<TTo>, INumber<TTo>
    {
        int n = a.Length;
        var bytes = GC.AllocateUninitializedArray<byte>(n * System.Runtime.CompilerServices.Unsafe.SizeOf<TTo>());
        var into = MemoryMarshal.Cast<byte, TTo>(bytes.AsSpan());
        // An integer as a double or a float cannot overflow, so the checked
        // conversion and the truncating one give the same values, and the
        // truncating one is vectorized: three times as fast for longs.
        bool cannotOverflow =
            (typeof(TTo) == typeof(double) || typeof(TTo) == typeof(float))
            && (typeof(TFrom) == typeof(long) || typeof(TFrom) == typeof(int)
                || typeof(TFrom) == typeof(short) || typeof(TFrom) == typeof(byte));
        if (cannotOverflow) TensorPrimitives.ConvertTruncating(a.Values, into);
        else TensorPrimitives.ConvertChecked(a.Values, into);
        var bitmap = BothValid(a.Data, a.Data, n, out int nulls);
        var data = new ArrayData(Builder<TTo>.ArrowType(), n, nulls, 0, new[] { bitmap, new ArrowBuffer(bytes) });
        return (PrimitiveArray<TTo>)ArrowArrayFactory.BuildArray(data);
    }

    /// <summary>
    /// The bitmap of the rows valid in both, from 0, or empty when every row
    /// is. Byte by byte when both start at a byte, and bit by bit otherwise.
    /// </summary>
    private static ArrowBuffer BothValid(ArrayData a, ArrayData b, int n, out int nulls)
    {
        nulls = 0;
        bool aAll = a.NullCount == 0 || a.Buffers[0].IsEmpty;
        bool bAll = b.NullCount == 0 || b.Buffers[0].IsEmpty;
        if (aAll && bAll) return ArrowBuffer.Empty;

        var bits = new byte[(n + 7) / 8];
        var av = aAll ? default : a.Buffers[0].Span;
        var bv = bAll ? default : b.Buffers[0].Span;
        if ((aAll || a.Offset % 8 == 0) && (bAll || b.Offset % 8 == 0))
        {
            int ao = a.Offset / 8, bo = b.Offset / 8;
            for (int i = 0; i < bits.Length; i++)
                bits[i] = (byte)((aAll ? 0xFF : av[ao + i]) & (bAll ? 0xFF : bv[bo + i]));
            // The bits after the last row are not rows.
            if (n % 8 != 0) bits[^1] &= (byte)((1 << (n % 8)) - 1);
            nulls = n - CountSet(bits);
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                bool valid = (aAll || Bit(av, a.Offset + i)) && (bAll || Bit(bv, b.Offset + i));
                if (valid) bits[i >> 3] |= (byte)(1 << (i & 7));
                else nulls++;
            }
        }
        return nulls == 0 ? ArrowBuffer.Empty : new ArrowBuffer(bits);
    }

    private static bool Bit(ReadOnlySpan<byte> bits, int i) => (bits[i >> 3] & (1 << (i & 7))) != 0;

    private static int CountSet(ReadOnlySpan<byte> bits)
    {
        int count = 0;
        foreach (byte b in bits) count += BitOperations.PopCount(b);
        return count;
    }
}
