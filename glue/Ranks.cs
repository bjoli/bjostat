/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Ranks: Wilcoxon's rank-sum and signed-rank tests, and Fisher's exact test
// of a 2 × 2 table, as R's wilcox.test and fisher.test, for
// (bjostat hypothesis).
//
// A test answers an array: the statistic, the p-values of the two-sided,
// "less" and "greater" alternatives, and 1 when the p-values are exact, 0
// when they are of the normal approximation. As R, the exact distribution is
// used when each sample has fewer than 50 values and no value is tied (or,
// of the signed-rank test, 0); it is counted by dynamic programming over the
// sums of subsets of the ranks.

namespace Bjostat;

public static class Ranks
{
    // The ranks of the values, ties given the mean of their ranks, and the
    // sum of t³ - t over the groups of t tied values.
    private static double[] RanksOf(double[] v, out double ties)
    {
        int n = v.Length;
        var order = Enumerable.Range(0, n).ToArray();
        System.Array.Sort(order, (a, b) => v[a].CompareTo(v[b]));
        var ranks = new double[n];
        ties = 0;
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j + 1 < n && v[order[j + 1]] == v[order[i]]) j++;
            double rank = (i + j) / 2.0 + 1;
            for (int k = i; k <= j; k++) ranks[order[k]] = rank;
            double t = j - i + 1;
            ties += t * t * t - t;
            i = j + 1;
        }
        return ranks;
    }

    // The numbers of the subsets of size k of {0, ..., n - 1} by the sum of
    // their elements, for W of the rank-sum test: W of x is that sum less
    // nothing, as the ranks less 1 are 0 to n - 1 and W is the sum of x's
    // ranks less k(k+1)/2.
    private static double[] SubsetSums(int n, int k)
    {
        int max = k * (n - k);
        // ways[j][s]: subsets of size j of the elements so far whose sum,
        // less 0 + 1 + ... + (j - 1), is s.
        var ways = new double[k + 1][];
        for (int j = 0; j <= k; j++) ways[j] = new double[max + 1];
        ways[0][0] = 1;
        for (int e = 0; e < n; e++)
            for (int j = Math.Min(k, e + 1); j >= 1; j--)
            {
                int shift = e - (j - 1);
                if (shift < 0) continue;
                for (int s = max; s >= shift; s--) ways[j][s] += ways[j - 1][s - shift];
            }
        return ways[k];
    }

    /// <summary>Wilcoxon's rank-sum test of x against y, W the sum of x's ranks less n(n+1)/2.</summary>
    public static double[] RankSum(double[] x, double[] y, bool correct)
    {
        int nx = x.Length, ny = y.Length;
        var all = x.Concat(y).ToArray();
        var ranks = RanksOf(all, out double ties);
        double w = 0;
        for (int i = 0; i < nx; i++) w += ranks[i];
        w -= nx * (nx + 1) / 2.0;
        if (nx < 50 && ny < 50 && ties == 0)
        {
            var counts = SubsetSums(nx + ny, nx);
            double total = counts.Sum();
            double Below(double q)  // P(W ≤ q)
            {
                double s = 0;
                for (int i = 0; i <= Math.Min(q, counts.Length - 1); i++) s += counts[i];
                return q < 0 ? 0 : s / total;
            }
            double less = Below(w), greater = 1 - Below(w - 1);
            double two = w > nx * ny / 2.0 ? 2 * greater : 2 * less;
            return new[] { w, Math.Min(1, two), less, greater, 1 };
        }
        int n = nx + ny;
        double z = w - nx * ny / 2.0;
        double sigma = Math.Sqrt(nx * ny / 12.0 * (n + 1 - ties / ((double)n * (n - 1))));
        return Normal(w, z, sigma, correct);
    }

    /// <summary>Wilcoxon's signed-rank test of the differences, V the sum of the ranks of those above 0.</summary>
    public static double[] SignedRank(double[] d, bool correct)
    {
        bool zeros = d.Any(x => x == 0);
        var nonzero = d.Where(x => x != 0).ToArray();
        int n = nonzero.Length;
        var ranks = RanksOf(nonzero.Select(Math.Abs).ToArray(), out double ties);
        double v = 0;
        for (int i = 0; i < n; i++) if (nonzero[i] > 0) v += ranks[i];
        if (n < 50 && ties == 0 && !zeros)
        {
            // The subsets of {1, ..., n} by their sum.
            int max = n * (n + 1) / 2;
            var counts = new double[max + 1];
            counts[0] = 1;
            for (int e = 1; e <= n; e++)
                for (int s = max; s >= e; s--) counts[s] += counts[s - e];
            double total = Math.Pow(2, n);
            double Below(double q)
            {
                double s = 0;
                for (int i = 0; i <= Math.Min(q, max); i++) s += counts[i];
                return q < 0 ? 0 : s / total;
            }
            double less = Below(v), greater = 1 - Below(v - 1);
            double two = v > n * (n + 1) / 4.0 ? 2 * greater : 2 * less;
            return new[] { v, Math.Min(1, two), less, greater, 1 };
        }
        double z = v - n * (n + 1) / 4.0;
        double sigma = Math.Sqrt(n * (n + 1) * (2 * n + 1) / 24.0 - ties / 48.0);
        return Normal(v, z, sigma, correct);
    }

    // The p-values of the normal approximation, with R's correction of 1/2
    // towards the mean when `correct`.
    private static double[] Normal(double statistic, double z, double sigma, bool correct)
    {
        double c = correct ? 0.5 : 0;
        double two = 2 * Math.Min(Distributions.NormalCdf((z - Math.Sign(z) * c) / sigma, 0, 1, true),
                                  Distributions.NormalCdf((z - Math.Sign(z) * c) / sigma, 0, 1, false));
        double less = Distributions.NormalCdf((z + c) / sigma, 0, 1, true);
        double greater = Distributions.NormalCdf((z - c) / sigma, 0, 1, false);
        return new[] { statistic, Math.Min(1, two), less, greater, 0 };
    }

    // ---------------------------------------------------------------------
    // Fisher's exact test

    /// <summary>
    /// Fisher's exact test of the table [[a, b], [c, d]]: the p-values of
    /// the two-sided, "less" and "greater" alternatives, then the conditional
    /// maximum likelihood estimate of the odds ratio, as R's fisher.test.
    /// </summary>
    public static double[] Fisher(long a, long b, long c, long d)
    {
        long m = a + b, n = c + d, k = a + c;
        long lo = Math.Max(0, k - n), hi = Math.Min(k, m);
        int size = (int)(hi - lo + 1);
        // ln of the hypergeometric probability of each x from lo to hi.
        var logp = new double[size];
        for (int i = 0; i < size; i++)
        {
            long x = lo + i;
            logp[i] = LogChoose(m, x) + LogChoose(n, k - x) - LogChoose(m + n, k);
        }
        int at = (int)(a - lo);
        double less = 0, greater = 0, two = 0;
        double observed = logp[at];
        for (int i = 0; i < size; i++)
        {
            double p = Math.Exp(logp[i]);
            if (i <= at) less += p;
            if (i >= at) greater += p;
            // R's relative tolerance of 1e-7, so that a probability that is
            // the observed one but for rounding counts.
            if (logp[i] <= observed + Math.Log(1 + 1e-7)) two += p;
        }
        return new[] { Math.Min(1, two), Math.Min(1, less), Math.Min(1, greater), OddsRatio(logp, lo, a) };
    }

    private static double LogChoose(long n, long k) =>
        Distributions.LogGamma(n + 1.0) - Distributions.LogGamma(k + 1.0) - Distributions.LogGamma(n - k + 1.0);

    // The ψ whose noncentral hypergeometric mean is a: 0 when a is the least
    // x, infinite when it is the most, else found by bisection on ln ψ, as
    // the mean rises with ψ.
    private static double OddsRatio(double[] logp, long lo, long a)
    {
        int size = logp.Length;
        if (a == lo) return 0;
        if (a == lo + size - 1) return double.PositiveInfinity;
        double Mean(double logPsi)
        {
            double top = double.NegativeInfinity;
            for (int i = 0; i < size; i++) top = Math.Max(top, logp[i] + (lo + i) * logPsi);
            double s = 0, sx = 0;
            for (int i = 0; i < size; i++)
            {
                double w = Math.Exp(logp[i] + (lo + i) * logPsi - top);
                s += w;
                sx += w * (lo + i);
            }
            return sx / s;
        }
        double left = -100, right = 100;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (left + right);
            if (Mean(mid) < a) left = mid; else right = mid;
        }
        return Math.Exp(0.5 * (left + right));
    }
}
