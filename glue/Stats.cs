/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Stats: the quantiles of (bjostat stats), of the values a loop collected.

using System.Runtime.InteropServices;

namespace Bjostat;

public static class Stats
{
    /// <summary>
    /// The quantiles at <paramref name="ps"/> of the values, as R's
    /// <c>quantile</c> gives them by default (its type 7): the value at
    /// (n - 1) p in the sorted values, between two values a straight line
    /// between them. The values are reordered in place. NaN at each p when a
    /// value is NaN or there are none; a p outside 0 to 1 is an
    /// ArgumentOutOfRangeException.
    /// </summary>
    /// <remarks>
    /// The values are not sorted: each value that is needed is found by a
    /// selection, which puts it at its place in the sorted order with the
    /// smaller values before it and the others after, in time proportional
    /// to the values it looks at. The places are found from the least up, so
    /// each selection looks only at the values after the last place.
    /// </remarks>
    public static double[] Quantiles(List<double> values, double[] ps)
    {
        foreach (double p in ps)
            if (!(p >= 0.0 && p <= 1.0))
                throw new ArgumentOutOfRangeException(nameof(ps), $"(bjostat stats): a quantile at {p}, which is not between 0 and 1");
        var xs = CollectionsMarshal.AsSpan(values);
        var result = new double[ps.Length];
        bool nan = xs.IsEmpty;
        foreach (double x in xs)
            if (double.IsNaN(x)) { nan = true; break; }
        if (nan)
        {
            Array.Fill(result, double.NaN);
            return result;
        }
        SelectQuantiles(xs, ps, result);
        return result;
    }

    /// <summary>
    /// The seven numbers of a summary of <paramref name="values"/> (which is
    /// not empty): min, q1, median, mean, q3, max and sd. Reorders
    /// <paramref name="values"/> in place.
    /// </summary>
    public static double[] Summarize(List<double> values)
    {
        var xs = CollectionsMarshal.AsSpan(values);
        double k = xs[0], s1 = 0.0, s2 = 0.0;
        for (int i = 0; i < xs.Length; i++)
        {
            double d = xs[i] - k;
            s1 += d;
            s2 += d * d;
        }
        if (double.IsNaN(s2))
            return new[] { double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN };
        double n = xs.Length;
        double mean = k + s1 / n;
        double sd = xs.Length < 2 ? double.NaN : Math.Sqrt(Math.Max(0.0, (s2 - s1 * s1 / n) / (n - 1.0)));
        Span<double> qs = stackalloc double[5];
        SelectQuantiles(xs, SummaryPs, qs);
        return new[] { qs[0], qs[1], qs[2], mean, qs[3], qs[4], sd };
    }

    private static readonly double[] SummaryPs = { 0.0, 0.25, 0.5, 0.75, 1.0 };

    private static void SelectQuantiles(Span<double> xs, ReadOnlySpan<double> ps, Span<double> result)
    {
        int n = xs.Length;
        Span<int> places = ps.Length <= 8 ? stackalloc int[ps.Length * 2] : new int[ps.Length * 2];
        int count = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            double h = (n - 1) * ps[i];
            int lo = (int)Math.Floor(h);
            places[count++] = lo;
            if (h > lo) places[count++] = lo + 1;
        }
        places = places.Slice(0, count);
        places.Sort();
        int unique = 0;
        for (int i = 0; i < places.Length; i++)
            if (unique == 0 || places[i] != places[unique - 1])
                places[unique++] = places[i];
        SelectAll(xs, 0, n - 1, places.Slice(0, unique));
        for (int i = 0; i < ps.Length; i++)
        {
            double h = (n - 1) * ps[i];
            int lo = (int)Math.Floor(h);
            result[i] = h > lo ? xs[lo] + (h - lo) * (xs[lo + 1] - xs[lo]) : xs[lo];
        }
    }

    private static void SelectAll(Span<double> xs, int lo, int hi, ReadOnlySpan<int> places)
    {
        if (places.IsEmpty || lo > hi) return;
        int mid = places.Length / 2;
        int k = places[mid];
        Select(xs, lo, hi, k);
        SelectAll(xs, lo, k - 1, places.Slice(0, mid));
        SelectAll(xs, k + 1, hi, places.Slice(mid + 1));
    }

    // Puts the value that belongs at `k` in the sorted order of xs[left..right]
    // there, the smaller values before it and the greater after it: a linear
    // scan when `k` is an end of the slice, and Floyd-Rivest selection
    // otherwise, which samples a small slice around `k` to pick a pivot near
    // the kth value so that one pass narrows a long array to a few thousand
    // values.
    private static void Select(Span<double> xs, int left, int right, int k)
    {
        while (right > left)
        {
            if (k == left)
            {
                int minAt = left;
                double min = xs[left];
                for (int idx = left + 1; idx <= right; idx++)
                    if (xs[idx] < min) { min = xs[idx]; minAt = idx; }
                (xs[left], xs[minAt]) = (xs[minAt], xs[left]);
                return;
            }
            if (k == right)
            {
                int maxAt = right;
                double max = xs[right];
                for (int idx = left; idx < right; idx++)
                    if (xs[idx] > max) { max = xs[idx]; maxAt = idx; }
                (xs[right], xs[maxAt]) = (xs[maxAt], xs[right]);
                return;
            }
            if (right - left <= 32)
            {
                xs[left..(right + 1)].Sort();
                return;
            }
            if (right - left > 600)
            {
                double n = right - left + 1;
                double m = k - left + 1;
                double z = Math.Log(n);
                double s = 0.5 * Math.Exp(2.0 * z / 3.0);
                double sd = 0.5 * Math.Sqrt(z * s * (n - s) / n) * (m - n / 2.0 < 0 ? -1.0 : 1.0);
                int newLeft = Math.Clamp((int)(k - m * s / n + sd), left, k);
                int newRight = Math.Clamp((int)(k + (n - m) * s / n + sd), k, right);
                Select(xs, newLeft, newRight, k);
            }
            else
            {
                int mid = left + (right - left) / 2;
                if (xs[mid] < xs[left]) (xs[mid], xs[left]) = (xs[left], xs[mid]);
                if (xs[right] < xs[left]) (xs[right], xs[left]) = (xs[left], xs[right]);
                if (xs[right] < xs[mid]) (xs[right], xs[mid]) = (xs[mid], xs[right]);
                (xs[mid], xs[k]) = (xs[k], xs[mid]);
            }
            double t = xs[k];
            int i = left, j = right;
            (xs[left], xs[k]) = (xs[k], xs[left]);
            if (xs[right] > t) (xs[right], xs[left]) = (xs[left], xs[right]);
            while (i < j)
            {
                (xs[i], xs[j]) = (xs[j], xs[i]);
                i++;
                j--;
                while (xs[i] < t) i++;
                while (xs[j] > t) j--;
            }
            if (xs[left] == t)
                (xs[left], xs[j]) = (xs[j], xs[left]);
            else
            {
                j++;
                (xs[j], xs[right]) = (xs[right], xs[j]);
            }
            if (j <= k) left = j + 1;
            if (k <= j) right = j - 1;
        }
    }

    public static List<double> Grow(List<double> xs, int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        CollectionsMarshal.SetCount(xs, xs.Count + n);
        return xs;
    }

    public static Span<double> Span(List<double> xs, int start, int n)
    {
        if (start < 0 || n < 0 || start > xs.Count - n) throw new ArgumentOutOfRangeException(nameof(start));
        return CollectionsMarshal.AsSpan(xs).Slice(start, n);
    }

    public static void NoNone(List<double> xs, int i) =>
        throw new InvalidOperationException("(bjostat stats): a quantile value cannot be None");
}
