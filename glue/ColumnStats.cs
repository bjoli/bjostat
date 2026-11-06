/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// ColumnStats: the sums that the moments of (bjostat stats) are made of, of
// a whole column at once, with vector instructions.
//
// The sums are those of the collectors `moments` and `co-moments`: the
// count, the first value, and the sums of the differences from the first
// value and of their squares and products. (bjostat stats) finishes them
// with the collectors' own functions, so a column function and a loop differ
// only in the order of the additions.
//
// The order is fixed, so that a result is the same on every machine: the
// value of row r goes to lane r % 8, each lane adds its values in row order,
// and the lanes are added ((0 + 1) + (2 + 3)) + ((4 + 5) + (6 + 7)). With
// AVX2 two Vector256 of doubles are the 8 lanes; without it, and for a type
// that has no vector load here, 8 doubles do the same additions. A null adds
// +0.0 to its lane in both. A vector's subtraction, multiplication and
// addition round as a double's do, and .NET does not fuse a multiplication
// and an addition that the code writes apart.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Apache.Arrow;

namespace Bjostat;

public static class ColumnStats
{
    private const int Lanes = 8;

    // For each 4 valid bits, the lanes of a Vector256 of doubles: all ones
    // where the bit is set.
    private static readonly Vector256<double>[] Masks = MakeMasks();

    private static Vector256<double>[] MakeMasks()
    {
        var masks = new Vector256<double>[16];
        for (int m = 0; m < 16; m++)
            masks[m] = Vector256.Create(
                (m & 1) != 0 ? -1L : 0L, (m & 2) != 0 ? -1L : 0L,
                (m & 4) != 0 ? -1L : 0L, (m & 8) != 0 ? -1L : 0L).AsDouble();
        return masks;
    }

    /// <summary>
    /// The moments of the values of the column that are not null:
    /// { n, k, s1, s2 }, where k is the first value and s1 and s2 the sums
    /// of the differences from k and of their squares. All 0 for no value.
    /// </summary>
    public static double[] Moments<T>(PrimitiveArray<T> a) where T : struct, IEquatable<T>, INumber<T>
    {
        var values = a.Values;
        int length = a.Length;
        var bits = a.NullCount == 0 ? ReadOnlySpan<byte>.Empty : a.NullBitmapBuffer.Span;
        int offset = a.Offset;
        int first = NextValid(bits, offset, 0, length);
        if (first == length) return new double[4];
        double k = ToDouble(values[first]);

        Span<double> s1 = stackalloc double[Lanes];
        Span<double> s2 = stackalloc double[Lanes];
        s1.Clear();
        s2.Clear();
        long n = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && HasVectorLoad<T>())
        {
            var kv = Vector256.Create(k);
            Vector256<double> s1a = default, s1b = default, s2a = default, s2b = default;
            for (; i + Lanes <= length; i += Lanes)
            {
                var d0 = Load4(values, i) - kv;
                var d1 = Load4(values, i + 4) - kv;
                if (bits.IsEmpty)
                    n += Lanes;
                else
                {
                    int m = Bits8(bits, offset + i);
                    d0 &= Masks[m & 15];
                    d1 &= Masks[m >> 4];
                    n += BitOperations.PopCount((uint)m);
                }
                s1a += d0;
                s1b += d1;
                s2a += d0 * d0;
                s2b += d1 * d1;
            }
            s1a.CopyTo(s1);
            s1b.CopyTo(s1.Slice(4));
            s2a.CopyTo(s2);
            s2b.CopyTo(s2.Slice(4));
        }
        for (; i < length; i++)
        {
            int lane = i & (Lanes - 1);
            if (IsValid(bits, offset + i))
            {
                double d = ToDouble(values[i]) - k;
                s1[lane] += d;
                s2[lane] += d * d;
                n++;
            }
            else
            {
                s1[lane] += 0.0;
                s2[lane] += 0.0;
            }
        }
        return new[] { (double)n, k, Total(s1), Total(s2) };
    }

    /// <summary>
    /// The moments of the pairs of the two columns where neither is null,
    /// of the rows of the shorter column: { n, kx, ky, sx, sy, sxx, syy, sxy },
    /// as <see cref="Moments"/> for each side, and the sum of the products
    /// of the differences. All 0 for no pair.
    /// </summary>
    public static double[] CoMoments<X, Y>(PrimitiveArray<X> xs, PrimitiveArray<Y> ys)
        where X : struct, IEquatable<X>, INumber<X>
        where Y : struct, IEquatable<Y>, INumber<Y>
    {
        var xv = xs.Values;
        var yv = ys.Values;
        int length = Math.Min(xs.Length, ys.Length);
        var xbits = xs.NullCount == 0 ? ReadOnlySpan<byte>.Empty : xs.NullBitmapBuffer.Span;
        var ybits = ys.NullCount == 0 ? ReadOnlySpan<byte>.Empty : ys.NullBitmapBuffer.Span;
        int xoff = xs.Offset, yoff = ys.Offset;
        int first = 0;
        while (first < length && !(IsValid(xbits, xoff + first) && IsValid(ybits, yoff + first))) first++;
        if (first == length) return new double[8];
        double kx = ToDouble(xv[first]), ky = ToDouble(yv[first]);

        Span<double> sums = stackalloc double[5 * Lanes];
        sums.Clear();
        var sx = sums.Slice(0, Lanes);
        var sy = sums.Slice(Lanes, Lanes);
        var sxx = sums.Slice(2 * Lanes, Lanes);
        var syy = sums.Slice(3 * Lanes, Lanes);
        var sxy = sums.Slice(4 * Lanes, Lanes);
        long n = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && HasVectorLoad<X>() && HasVectorLoad<Y>())
        {
            var kxv = Vector256.Create(kx);
            var kyv = Vector256.Create(ky);
            Vector256<double> sxa = default, sxb = default, sya = default, syb = default;
            Vector256<double> sxxa = default, sxxb = default, syya = default, syyb = default;
            Vector256<double> sxya = default, sxyb = default;
            bool nulls = !xbits.IsEmpty || !ybits.IsEmpty;
            for (; i + Lanes <= length; i += Lanes)
            {
                var dx0 = Load4(xv, i) - kxv;
                var dx1 = Load4(xv, i + 4) - kxv;
                var dy0 = Load4(yv, i) - kyv;
                var dy1 = Load4(yv, i + 4) - kyv;
                if (!nulls)
                    n += Lanes;
                else
                {
                    int m = (xbits.IsEmpty ? 0xFF : Bits8(xbits, xoff + i)) & (ybits.IsEmpty ? 0xFF : Bits8(ybits, yoff + i));
                    var m0 = Masks[m & 15];
                    var m1 = Masks[m >> 4];
                    dx0 &= m0;
                    dy0 &= m0;
                    dx1 &= m1;
                    dy1 &= m1;
                    n += BitOperations.PopCount((uint)m);
                }
                sxa += dx0;
                sxb += dx1;
                sya += dy0;
                syb += dy1;
                sxxa += dx0 * dx0;
                sxxb += dx1 * dx1;
                syya += dy0 * dy0;
                syyb += dy1 * dy1;
                sxya += dx0 * dy0;
                sxyb += dx1 * dy1;
            }
            sxa.CopyTo(sx);
            sxb.CopyTo(sx.Slice(4));
            sya.CopyTo(sy);
            syb.CopyTo(sy.Slice(4));
            sxxa.CopyTo(sxx);
            sxxb.CopyTo(sxx.Slice(4));
            syya.CopyTo(syy);
            syyb.CopyTo(syy.Slice(4));
            sxya.CopyTo(sxy);
            sxyb.CopyTo(sxy.Slice(4));
        }
        for (; i < length; i++)
        {
            int lane = i & (Lanes - 1);
            double dx = 0.0, dy = 0.0;
            if (IsValid(xbits, xoff + i) && IsValid(ybits, yoff + i))
            {
                dx = ToDouble(xv[i]) - kx;
                dy = ToDouble(yv[i]) - ky;
                n++;
            }
            sx[lane] += dx;
            sy[lane] += dy;
            sxx[lane] += dx * dx;
            syy[lane] += dy * dy;
            sxy[lane] += dx * dy;
        }
        return new[] { (double)n, kx, ky, Total(sx), Total(sy), Total(sxx), Total(syy), Total(sxy) };
    }

    // The lanes added in the fixed order.
    private static double Total(ReadOnlySpan<double> s) =>
        ((s[0] + s[1]) + (s[2] + s[3])) + ((s[4] + s[5]) + (s[6] + s[7]));

    private static bool HasVectorLoad<T>() => typeof(T) == typeof(double) || typeof(T) == typeof(long);

    // Values i to i + 3 as doubles. A long converts as (double) does: to the
    // nearest double.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Load4<T>(ReadOnlySpan<T> values, int i)
    {
        ref T at = ref Unsafe.Add(ref MemoryMarshal.GetReference(values), i);
        if (typeof(T) == typeof(double))
            return Vector256.LoadUnsafe(ref Unsafe.As<T, double>(ref at));
        if (typeof(T) == typeof(long))
            return Vector256.ConvertToDouble(Vector256.LoadUnsafe(ref Unsafe.As<T, long>(ref at)));
        throw new NotSupportedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ToDouble<T>(T x) where T : INumber<T> => double.CreateTruncating(x);

    // The 8 valid bits from bit p, bit 0 the first. The block's 8 rows are
    // all in the array, so its bits are in the bitmap.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bits8(ReadOnlySpan<byte> bits, int p)
    {
        int b = p >> 3, shift = p & 7;
        int lo = bits[b];
        if (shift == 0) return lo;
        int hi = b + 1 < bits.Length ? bits[b + 1] : 0;
        return ((lo | (hi << 8)) >> shift) & 0xFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValid(ReadOnlySpan<byte> bits, int p) =>
        bits.IsEmpty || (bits[p >> 3] & (1 << (p & 7))) != 0;

    private static int NextValid(ReadOnlySpan<byte> bits, int offset, int i, int length)
    {
        while (i < length && !IsValid(bits, offset + i)) i++;
        return i;
    }
}
