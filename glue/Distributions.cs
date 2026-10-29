/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Distributions: densities, distribution functions and quantiles of the
// normal, t, chi-squared, F, binomial and Poisson distributions, for
// (bjostat distributions).
//
// Each distribution function is made from three functions: the logarithm of
// the gamma function (Lanczos), the regularized incomplete gamma function and
// the regularized incomplete beta function (series and continued fractions,
// evaluated by Lentz's method). The upper tail is computed as itself, not as
// one minus the lower one, so that a small p-value keeps its digits. A
// quantile is found by Newton's method on the distribution function, kept in
// a bracket and bisected where a step leaves it, in the tail whose
// probability is the smaller.
//
// A parameter outside its range gives NaN, as R does.

namespace Bjostat;

public static class Distributions
{
    private const double Eps = 1e-16;
    private const double Tiny = 1e-300;
    private const int MaxIterations = 100_000;

    // ---------------------------------------------------------------------
    // The functions the others are made of

    // Lanczos's approximation, g = 7 and 9 terms: about 15 digits.
    private static readonly double[] Lanczos =
    {
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
        1.5056327351493116e-7,
    };

    /// <summary>The logarithm of the absolute value of the gamma function.</summary>
    public static double LogGamma(double x)
    {
        if (double.IsNaN(x)) return x;
        if (x <= 0 && Math.Floor(x) == x) return double.PositiveInfinity;
        if (x < 0.5)
            // The reflection formula.
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        // ln Γ(n) of a small whole number, exactly as a sum of logarithms.
        if (x <= 30 && Math.Floor(x) == x)
        {
            double s = 0;
            for (int i = 2; i < (int)x; i++) s += Math.Log(i);
            return s;
        }
        x -= 1;
        double a = Lanczos[0];
        double t = x + 7.5;
        for (int i = 1; i < 9; i++) a += Lanczos[i] / (x + i);
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    // ln Γ(x) less Stirling's approximation, (x - 1/2) ln x - x + ln √(2π),
    // for x of at least 10: the first eight terms of Stirling's series.
    private static double StirlingRest(double x)
    {
        double r = 1 / x, r2 = r * r;
        return r * (1.0 / 12 + r2 * (-1.0 / 360 + r2 * (1.0 / 1260 + r2 * (-1.0 / 1680 + r2 * (1.0 / 1188
               + r2 * (-691.0 / 360360 + r2 * (1.0 / 156 + r2 * (-3617.0 / 122400))))))));
    }

    private const double LogRoot2Pi = 0.918938533204672742;

    /// <summary>
    /// ln B(a, b). When a parameter is 10 or more, the parts of Stirling's
    /// approximation that would cancel are left out first, as R's lbeta does:
    /// ln Γ(a) - ln Γ(a + b) of a large a is a difference of two large numbers.
    /// </summary>
    private static double LogBeta(double a, double b)
    {
        double p = Math.Min(a, b), q = Math.Max(a, b);
        if (p >= 10)
        {
            double corr = StirlingRest(p) + StirlingRest(q) - StirlingRest(p + q);
            return -0.5 * Math.Log(q) + LogRoot2Pi + corr + (p - 0.5) * Math.Log(p / (p + q))
                   + q * double.LogP1(-p / (p + q));
        }
        if (q >= 10)
        {
            double corr = StirlingRest(q) - StirlingRest(p + q);
            return LogGamma(p) + corr + p - p * Math.Log(p + q) + (q - 0.5) * double.LogP1(-p / (p + q));
        }
        return LogGamma(a) + LogGamma(b) - LogGamma(a + b);
    }

    // ln(x^a e^-x / Γ(a)), the factor in front of both forms of the
    // incomplete gamma function. For an a of 10 or more, written so that the
    // large terms cancel before they are added: with x = a (1 + d), it is
    // a (ln(1 + d) - d) + ln √(a / 2π) - StirlingRest(a).
    private static double LogGammaFront(double a, double x)
    {
        if (a < 10) return -x + a * Math.Log(x) - LogGamma(a);
        double d = (x - a) / a;
        return a * (double.LogP1(d) - d) + 0.5 * Math.Log(a) - LogRoot2Pi - StirlingRest(a);
    }

    /// <summary>
    /// The regularized incomplete gamma function: P(a, x) when
    /// <paramref name="upper"/> is false, Q(a, x) = 1 - P(a, x) when it is
    /// true.
    /// </summary>
    public static double Gamma(double a, double x, bool upper)
    {
        if (double.IsNaN(a) || double.IsNaN(x) || a <= 0) return double.NaN;
        if (x <= 0) return upper ? 1 : 0;
        if (double.IsPositiveInfinity(x)) return upper ? 0 : 1;
        double front = Math.Exp(LogGammaFront(a, x));
        if (x < a + 1)
        {
            // The series of P.
            double ap = a, del = 1 / a, sum = del;
            for (int n = 0; n < MaxIterations; n++)
            {
                ap += 1;
                del *= x / ap;
                sum += del;
                if (Math.Abs(del) < Math.Abs(sum) * Eps) break;
            }
            double p = sum * front;
            return upper ? 1 - p : p;
        }
        // The continued fraction of Q.
        double b = x + 1 - a, c = 1 / Tiny, d = 1 / b, h = d;
        for (int i = 1; i < MaxIterations; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = an * d + b;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = b + an / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < Eps) break;
        }
        double q = front * h;
        return upper ? q : 1 - q;
    }

    /// <summary>
    /// The regularized incomplete beta function I_x(a, b), or 1 - I_x(a, b)
    /// when <paramref name="upper"/> is true. <paramref name="y"/> is 1 - x,
    /// given apart so that a caller who knows it exactly loses no digits.
    /// </summary>
    public static double Beta(double x, double y, double a, double b, bool upper)
    {
        if (double.IsNaN(x) || double.IsNaN(a) || double.IsNaN(b) || a <= 0 || b <= 0) return double.NaN;
        if (x <= 0) return upper ? 1 : 0;
        if (y <= 0) return upper ? 0 : 1;
        // ln x and ln y, through ln(1 + t) for the one near 1.
        double lx = x > 0.5 ? double.LogP1(-y) : Math.Log(x);
        double ly = y > 0.5 ? double.LogP1(-x) : Math.Log(y);
        double front = Math.Exp(a * lx + b * ly - LogBeta(a, b));
        if (x < (a + 1) / (a + b + 2))
        {
            double lower = front * BetaFraction(x, a, b) / a;
            return upper ? 1 - lower : lower;
        }
        double up = front * BetaFraction(y, b, a) / b;
        return upper ? up : 1 - up;
    }

    private static double BetaFraction(double x, double a, double b)
    {
        double qab = a + b, qap = a + 1, qam = a - 1;
        double c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < Tiny) d = Tiny;
        d = 1 / d;
        double h = d;
        for (int m = 1; m < MaxIterations; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < Eps) break;
        }
        return h;
    }

    // ---------------------------------------------------------------------
    // Quantiles of continuous distributions

    /// <summary>
    /// The x in [lo, hi] where the distribution function is p: the lower
    /// tail's when <paramref name="lower"/>, the upper tail's otherwise. The
    /// tail of the smaller probability is solved, as 1 - p is exact for p of
    /// at least 0.5.
    /// </summary>
    private static double Invert(Func<double, bool, double> cdf, Func<double, double> density,
                                 double p, bool lower, double lo, double hi, double guess)
    {
        if (double.IsNaN(p) || p < 0 || p > 1) return double.NaN;
        if (p == 0) return lower ? lo : hi;
        if (p == 1) return lower ? hi : lo;
        // Solve in the lower tail when its probability is the smaller.
        bool solveLower = lower ? p <= 0.5 : p > 0.5;
        double target = lower == solveLower ? p : 1 - p;
        // f(x) rises with x in the lower tail and falls in the upper.
        double sign = solveLower ? 1 : -1;
        double F(double x) => sign * (cdf(x, !solveLower) - target);

        // A bracket: f(a) < 0 < f(b).
        double a = double.IsInfinity(lo) ? Math.Min(guess, 0) - 1 : lo;
        double b = double.IsInfinity(hi) ? Math.Max(guess, 0) + 1 : hi;
        for (int i = 0; i < 2000 && F(a) > 0; i++) a = double.IsInfinity(lo) ? a * 2 - 1 : lo;
        for (int i = 0; i < 2000 && F(b) < 0; i++) b = b * 2 + 1;

        // Newton's method on the logarithm of the tail, ln P - ln target,
        // whose slope is the density over P: in a far tail, where P falls as
        // e^(-x²/2), a step on P itself moves x by about 1/x, and one on its
        // logarithm moves it to the root in a few steps.
        double logTarget = Math.Log(target);
        double x = guess > a && guess < b ? guess : 0.5 * (a + b);
        for (int i = 0; i < 400; i++)
        {
            double fx = F(x);
            if (fx == 0) return x;
            if (fx < 0) a = x; else b = x;
            double tail = cdf(x, !solveLower);
            double next = tail > 0 && density(x) > 0
                ? x - sign * (Math.Log(tail) - logTarget) * tail / density(x)
                : double.NaN;
            if (!(next > a && next < b)) next = 0.5 * (a + b);
            if (Math.Abs(next - x) <= 4 * Eps * Math.Abs(x) || b - a <= 4 * Eps * Math.Max(Math.Abs(a), Math.Abs(b)))
                return next;
            x = next;
        }
        return x;
    }

    // ---------------------------------------------------------------------
    // Normal

    public static double NormalDensity(double x, double mean, double sd)
    {
        if (!(sd > 0)) return double.NaN;
        double z = (x - mean) / sd;
        return Math.Exp(-0.5 * z * z) / (sd * Math.Sqrt(2 * Math.PI));
    }

    public static double NormalCdf(double x, double mean, double sd, bool lower)
    {
        if (!(sd > 0) || double.IsNaN(x)) return double.NaN;
        double z = (x - mean) / sd;
        // Φ(z) = Q(1/2, z²/2) / 2 below 0, and its mirror above.
        if (!lower) z = -z;
        double half = 0.5 * Gamma(0.5, 0.5 * z * z, true);
        return z <= 0 ? half : 1 - half;
    }

    public static double NormalQuantile(double p, double mean, double sd, bool lower)
    {
        if (!(sd > 0)) return double.NaN;
        double pl = lower ? p : 1 - p;
        double guess = pl > 0 && pl < 1 ? Acklam(pl) : 0;
        double z = Invert((x, up) => NormalCdf(x, 0, 1, !up), x => NormalDensity(x, 0, 1),
                          p, lower, double.NegativeInfinity, double.PositiveInfinity, guess);
        return mean + sd * z;
    }

    // Acklam's approximation of the normal quantile, to about 1e-9: the
    // first guess of Newton's method.
    private static double Acklam(double p)
    {
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                       1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                       6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                       -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                       3.754408661907416e+00 };
        const double low = 0.02425;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                   / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > 1 - low)
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                   / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        double r = p - 0.5, s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r
               / (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }

    // ---------------------------------------------------------------------
    // Student's t

    public static double TDensity(double x, double df)
    {
        if (!(df > 0)) return double.NaN;
        if (double.IsPositiveInfinity(df)) return NormalDensity(x, 0, 1);
        return Math.Exp(LogGamma((df + 1) / 2) - LogGamma(df / 2) - 0.5 * Math.Log(df * Math.PI)
                        - (df + 1) / 2 * Math.Log(1 + x * x / df));
    }

    public static double TCdf(double x, double df, bool lower)
    {
        if (!(df > 0) || double.IsNaN(x)) return double.NaN;
        if (double.IsPositiveInfinity(df)) return NormalCdf(x, 0, 1, lower);
        if (double.IsInfinity(x)) return (x > 0) == lower ? 1 : 0;
        // Of many degrees of freedom, a corrected normal, as R's pt: its
        // error is of the order of 1 / df², and the continued fraction of
        // the beta function converges slowly there.
        if (df > 4e5)
        {
            double val = 1 / (4 * df);
            return NormalCdf(x * (1 - val) / Math.Sqrt(1 + x * x * 2 * val), 0, 1, lower);
        }
        // P(T > |x|) = I_w(df/2, 1/2) / 2, with w = df / (df + x²).
        double x2 = x * x;
        double w = df / (df + x2), v = x2 / (df + x2);
        double tail = 0.5 * Beta(w, v, df / 2, 0.5, false);
        bool small = (x < 0) == lower;
        if (small) return tail;
        // 1 - tail, as 1/2 + I_v(1/2, df/2) / 2.
        return 0.5 + 0.5 * Beta(w, v, df / 2, 0.5, true);
    }

    public static double TQuantile(double p, double df, bool lower)
    {
        if (!(df > 0)) return double.NaN;
        if (double.IsPositiveInfinity(df)) return NormalQuantile(p, 0, 1, lower);
        double pl = lower ? p : 1 - p;
        double z = pl > 0 && pl < 1 ? Acklam(pl) : 0;
        // Fisher's first terms of the t from the normal quantile.
        double guess = z + (z * z * z + z) / (4 * df);
        return Invert((x, up) => TCdf(x, df, !up), x => TDensity(x, df),
                      p, lower, double.NegativeInfinity, double.PositiveInfinity, guess);
    }

    // ---------------------------------------------------------------------
    // Chi-squared

    public static double ChiSquaredDensity(double x, double df)
    {
        if (!(df > 0)) return double.NaN;
        if (x < 0) return 0;
        if (x == 0) return df < 2 ? double.PositiveInfinity : df == 2 ? 0.5 : 0;
        double k = df / 2;
        return Math.Exp((k - 1) * Math.Log(x) - x / 2 - k * Math.Log(2) - LogGamma(k));
    }

    public static double ChiSquaredCdf(double x, double df, bool lower)
    {
        if (!(df > 0) || double.IsNaN(x)) return double.NaN;
        return Gamma(df / 2, x / 2, !lower);
    }

    public static double ChiSquaredQuantile(double p, double df, bool lower)
    {
        if (!(df > 0)) return double.NaN;
        double pl = lower ? p : 1 - p;
        double z = pl > 0 && pl < 1 ? Acklam(pl) : 0;
        // Wilson and Hilferty's cube of a normal.
        double h = 2 / (9 * df);
        double guess = df * Math.Pow(Math.Max(1 - h + z * Math.Sqrt(h), 0.01), 3);
        return Invert((x, up) => ChiSquaredCdf(x, df, !up), x => ChiSquaredDensity(x, df),
                      p, lower, 0, double.PositiveInfinity, guess);
    }

    // ---------------------------------------------------------------------
    // F

    public static double FDensity(double x, double d1, double d2)
    {
        if (!(d1 > 0) || !(d2 > 0)) return double.NaN;
        if (x < 0) return 0;
        if (x == 0) return d1 < 2 ? double.PositiveInfinity : d1 == 2 ? 1 : 0;
        return Math.Exp(0.5 * (d1 * Math.Log(d1 * x) + d2 * Math.Log(d2) - (d1 + d2) * Math.Log(d1 * x + d2))
                        - Math.Log(x) - LogBeta(d1 / 2, d2 / 2));
    }

    public static double FCdf(double x, double d1, double d2, bool lower)
    {
        if (!(d1 > 0) || !(d2 > 0) || double.IsNaN(x)) return double.NaN;
        if (x <= 0) return lower ? 0 : 1;
        if (double.IsPositiveInfinity(x)) return lower ? 1 : 0;
        double s = d1 * x + d2;
        return Beta(d1 * x / s, d2 / s, d1 / 2, d2 / 2, !lower);
    }

    public static double FQuantile(double p, double d1, double d2, bool lower)
    {
        if (!(d1 > 0) || !(d2 > 0)) return double.NaN;
        double guess = d2 > 2 ? d2 / (d2 - 2) : 1;
        return Invert((x, up) => FCdf(x, d1, d2, !up), x => FDensity(x, d1, d2),
                      p, lower, 0, double.PositiveInfinity, guess);
    }

    // ---------------------------------------------------------------------
    // Binomial

    public static double BinomialProbability(long k, long n, double p)
    {
        if (n < 0 || !(p >= 0 && p <= 1)) return double.NaN;
        if (k < 0 || k > n) return 0;
        if (p == 0) return k == 0 ? 1 : 0;
        if (p == 1) return k == n ? 1 : 0;
        double choose = LogGamma(n + 1.0) - LogGamma(k + 1.0) - LogGamma(n - k + 1.0);
        return Math.Exp(choose + k * Math.Log(p) + (n - k) * Math.Log(1 - p));
    }

    public static double BinomialCdf(long k, long n, double p, bool lower)
    {
        if (n < 0 || !(p >= 0 && p <= 1)) return double.NaN;
        if (k < 0) return lower ? 0 : 1;
        if (k >= n) return lower ? 1 : 0;
        if (p == 0) return lower ? 1 : 0;
        if (p == 1) return lower ? 0 : 1;
        // P(X ≤ k) = I_(1-p)(n - k, k + 1).
        return Beta(1 - p, p, n - k, k + 1, !lower);
    }

    /// <summary>The least k whose distribution function reaches p, or -1 for parameters out of range.</summary>
    public static long BinomialQuantile(double q, long n, double p, bool lower)
    {
        if (n < 0 || !(p >= 0 && p <= 1) || !(q >= 0 && q <= 1)) return -1;
        double mean = n * p, sd = Math.Sqrt(n * p * (1 - p));
        return DiscreteQuantile(k => BinomialCdf(k, n, p, true), k => BinomialCdf(k, n, p, false),
                                q, lower, mean, sd, n);
    }

    // ---------------------------------------------------------------------
    // Poisson

    public static double PoissonProbability(long k, double lambda)
    {
        if (!(lambda >= 0)) return double.NaN;
        if (k < 0) return 0;
        if (lambda == 0) return k == 0 ? 1 : 0;
        return Math.Exp(k * Math.Log(lambda) - lambda - LogGamma(k + 1.0));
    }

    public static double PoissonCdf(long k, double lambda, bool lower)
    {
        if (!(lambda >= 0)) return double.NaN;
        if (k < 0) return lower ? 0 : 1;
        if (lambda == 0) return lower ? 1 : 0;
        // P(X ≤ k) = Q(k + 1, λ).
        return Gamma(k + 1.0, lambda, lower);
    }

    public static long PoissonQuantile(double q, double lambda, bool lower)
    {
        if (!(lambda >= 0) || !(q >= 0 && q <= 1)) return -1;
        return DiscreteQuantile(k => PoissonCdf(k, lambda, true), k => PoissonCdf(k, lambda, false),
                                q, lower, lambda, Math.Sqrt(lambda), long.MaxValue);
    }

    // The least k from 0 to max whose lower distribution function reaches q,
    // or, of the upper tail, whose upper one is at most q. A step from the
    // normal guess, then walks of one. q is moved by 64 ulps, as R does, so
    // that the rounding of a sum does not give the next k.
    // 64 times the machine epsilon of doubles, 2^-52.
    private const double Fuzz = 64 * 2.220446049250313e-16;

    private static long DiscreteQuantile(Func<long, double> lowerCdf, Func<long, double> upperCdf,
                                         double q, bool lower, double mean, double sd, long max)
    {
        bool Reached(long k) => lower ? lowerCdf(k) >= q * (1 - Fuzz) : upperCdf(k) <= q * (1 + Fuzz);
        if (lower && q == 0 || !lower && q == 1) return 0;
        double ql = lower ? q : 1 - q;
        double z = ql > 0 && ql < 1 ? Acklam(ql) : (ql >= 1 ? 8.5 : -8.5);
        long k = (long)Math.Clamp(Math.Floor(mean + sd * z + 0.5), 0, (double)max);
        if (Reached(k))
        {
            while (k > 0 && Reached(k - 1)) k--;
            return k;
        }
        while (k < max && !Reached(k)) k++;
        return k;
    }
}
