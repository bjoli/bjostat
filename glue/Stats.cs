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
        int n = xs.Length;
        // The places that are needed, each once, from the least.
        var places = new SortedSet<int>();
        foreach (double p in ps)
        {
            double h = (n - 1) * p;
            int lo = (int)Math.Floor(h);
            places.Add(lo);
            if (h > lo) places.Add(lo + 1);
        }
        int from = 0;
        foreach (int place in places)
        {
            Select(xs, from, n - 1, place);
            from = place + 1;
        }
        for (int i = 0; i < ps.Length; i++)
        {
            double h = (n - 1) * ps[i];
            int lo = (int)Math.Floor(h);
            result[i] = h > lo ? xs[lo] + (h - lo) * (xs[lo + 1] - xs[lo]) : xs[lo];
        }
        return result;
    }

    // Puts the value that belongs at `k` in the sorted order of xs[lo..hi]
    // there, the smaller values before it and the greater after it: Hoare's
    // selection, with the median of three as the pivot, and a sort of the
    // last few values.
    private static void Select(Span<double> xs, int lo, int hi, int k)
    {
        while (hi - lo > 16)
        {
            int mid = lo + (hi - lo) / 2;
            if (xs[mid] < xs[lo]) (xs[mid], xs[lo]) = (xs[lo], xs[mid]);
            if (xs[hi] < xs[lo]) (xs[hi], xs[lo]) = (xs[lo], xs[hi]);
            if (xs[hi] < xs[mid]) (xs[hi], xs[mid]) = (xs[mid], xs[hi]);
            double pivot = xs[mid];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (xs[i] < pivot) i++;
                while (xs[j] > pivot) j--;
                if (i <= j)
                {
                    (xs[i], xs[j]) = (xs[j], xs[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return;
        }
        xs[lo..(hi + 1)].Sort();
    }
}
