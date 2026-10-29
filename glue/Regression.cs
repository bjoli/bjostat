/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Regression: a linear model of least squares, as R's lm, fitted one row at
// a time for (bjostat models).
//
// The rows are not kept. Each row [x | y] is turned into the upper
// triangular R of a QR decomposition of the rows so far, and into Q'y, by
// Givens rotations: R is p × p, so the memory is that of p² numbers whatever
// the number of rows. What is left of y when the row's x is zero is the
// part of y that no x can give, and the sum of its squares is the residual
// sum of squares. This is the method of Gentleman (1974) without square
// roots taken out, and is as stable as a QR of all the rows at once, where
// the normal equations X'X b = X'y lose half the digits.

using Collections;

namespace Bjostat;

public sealed class Regression
{
    private readonly bool _intercept;
    private int _p = -1;
    private double[] _r = System.Array.Empty<double>();  // row-major p × p, upper triangular
    private double[] _z = System.Array.Empty<double>();  // Q'y
    private double[] _row = System.Array.Empty<double>();
    private double[] _columnSquares = System.Array.Empty<double>();
    private double _rss;
    private long _n;
    private string _problem = "";

    // The response's sums, from its first value, for the total sum of squares.
    private double _k, _s1, _s2;

    private Regression(bool intercept) => _intercept = intercept;

    public static Regression Make(bool intercept) => new(intercept);

    /// <summary>Adds a row: the response and its predictors.</summary>
    public static Regression Add(Regression m, double y, RrbList<double> xs)
    {
        int k = xs.Count;
        if (m._p < 0) m.Start(k);
        if (k + (m._intercept ? 1 : 0) != m._p)
        {
            if (m._problem == "")
                m._problem = $"row {m._n} has {k} predictors, and the first row has {m._p - (m._intercept ? 1 : 0)}.";
            return m;
        }
        int at = 0;
        if (m._intercept) m._row[at++] = 1;
        for (int i = 0; i < k; i++) m._row[at++] = xs[i];
        m.Rotate(y);
        return m;
    }

    /// <summary>Adds a row of one predictor.</summary>
    public static Regression AddOne(Regression m, double y, double x)
    {
        if (m._p < 0) m.Start(1);
        int at = 0;
        if (m._intercept) m._row[at++] = 1;
        m._row[at] = x;
        m.Rotate(y);
        return m;
    }

    /// <summary>A row of all p columns, the intercept's 1 too, for Glm's weighted rows.</summary>
    internal void AddRow(double y, ReadOnlySpan<double> x)
    {
        if (_p < 0) Start(x.Length - (_intercept ? 1 : 0));
        x.CopyTo(_row);
        Rotate(y);
    }

    private void Start(int predictors)
    {
        _p = predictors + (_intercept ? 1 : 0);
        _r = new double[_p * _p];
        _z = new double[_p];
        _row = new double[_p];
        _columnSquares = new double[_p];
    }

    // The row in _row and y into R and Q'y, by one Givens rotation for each
    // place of the row that is not 0.
    private void Rotate(double y)
    {
        int p = _p;
        if (_n == 0) _k = y;
        double d0 = y - _k;
        _s1 += d0;
        _s2 += d0 * d0;
        _n++;
        for (int j = 0; j < p; j++) _columnSquares[j] += _row[j] * _row[j];

        for (int j = 0; j < p; j++)
        {
            double w = _row[j];
            if (w == 0) continue;
            double rjj = _r[j * p + j];
            if (rjj == 0)
            {
                // An empty row of R takes the rest of this row as it is.
                for (int k = j; k < p; k++) _r[j * p + k] = _row[k];
                _z[j] = y;
                return;
            }
            double h = Math.Sqrt(rjj * rjj + w * w);
            double c = rjj / h, s = w / h;
            _r[j * p + j] = h;
            for (int k = j + 1; k < p; k++)
            {
                double rk = _r[j * p + k], xk = _row[k];
                _r[j * p + k] = c * rk + s * xk;
                _row[k] = c * xk - s * rk;
            }
            double zj = _z[j];
            _z[j] = c * zj + s * y;
            y = c * y - s * zj;
        }
        _rss += y * y;
    }

    // ---------------------------------------------------------------------
    // The fit

    /// <summary>
    /// Why the model cannot be fitted, or the empty string: rows of other
    /// numbers of predictors, no more rows than coefficients, or a predictor
    /// that is a linear combination of the others (or of the intercept), as
    /// R's lm gives NA for. A column is taken as such when its diagonal in R
    /// is below 1e-7 of its length, as R's tolerance.
    /// </summary>
    public static string Problem(Regression m, string[] names)
    {
        if (m._problem != "") return m._problem;
        if (m._p < 0) return "there are no rows.";
        if (m._n <= m._p)
            return $"{m._n} rows are too few for {m._p} coefficients: a model needs more rows than coefficients.";
        for (int j = 0; j < m._p; j++)
        {
            double norm = Math.Sqrt(m._columnSquares[j]);
            if (Math.Abs(m._r[j * m._p + j]) <= 1e-7 * norm || norm == 0)
            {
                int predictor = j - (m._intercept ? 1 : 0);
                return predictor < 0
                    ? "the intercept is 0 in every row."
                    : $"'{(predictor < names.Length ? names[predictor] : predictor.ToString())}' is a linear "
                      + "combination of the predictors before it" + (m._intercept ? " and the intercept." : ".");
            }
        }
        return "";
    }

    public static long Rows(Regression m) => m._n;

    public static int Coefficients(Regression m) => m._p;

    public static bool Intercept(Regression m) => m._intercept;

    /// <summary>The coefficients b of R b = Q'y, by back substitution.</summary>
    public static double[] Estimates(Regression m)
    {
        int p = m._p;
        var b = new double[p];
        for (int j = p - 1; j >= 0; j--)
        {
            double s = m._z[j];
            for (int k = j + 1; k < p; k++) s -= m._r[j * p + k] * b[k];
            b[j] = s / m._r[j * p + j];
        }
        return b;
    }

    public static double ResidualSquares(Regression m) => m._rss;

    /// <summary>
    /// The total sum of squares of the response: about its mean with an
    /// intercept, and about 0 without one, as R's summary.lm.
    /// </summary>
    public static double TotalSquares(Regression m)
    {
        double n = m._n;
        double centred = m._s2 - m._s1 * m._s1 / n;
        if (m._intercept) return centred;
        double mean = m._k + m._s1 / n;
        return centred + n * mean * mean;
    }

    /// <summary>
    /// The number to <paramref name="digits"/> significant digits, as R
    /// prints a summary: 0.001234, 12.35, 1.235e+06.
    /// </summary>
    public static string Significant(double x, int digits)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsInfinity(x)) return x > 0 ? "Inf" : "-Inf";
        var text = x.ToString("G" + digits, System.Globalization.CultureInfo.InvariantCulture);
        return text.Replace("E+", "e+").Replace("E-", "e-");
    }

    /// <summary>
    /// The standard error of each coefficient over σ: the length of each row
    /// of R⁻¹, as (X'X)⁻¹ = R⁻¹ R⁻ᵀ.
    /// </summary>
    public static double[] UnscaledErrors(Regression m)
    {
        int p = m._p;
        var inv = Inverse(m);
        var e = new double[p];
        for (int j = 0; j < p; j++)
        {
            double s = 0;
            for (int c = j; c < p; c++) s += inv[j * p + c] * inv[j * p + c];
            e[j] = Math.Sqrt(s);
        }
        return e;
    }

    /// <summary>(X'X)⁻¹ = R⁻¹ R⁻ᵀ, row-major p × p: R's cov.unscaled.</summary>
    public static double[] UnscaledCovariance(Regression m)
    {
        int p = m._p;
        var inv = Inverse(m);
        var c = new double[p * p];
        for (int i = 0; i < p; i++)
            for (int j = 0; j < p; j++)
            {
                double s = 0;
                for (int k = Math.Max(i, j); k < p; k++) s += inv[i * p + k] * inv[j * p + k];
                c[i * p + j] = s;
            }
        return c;
    }

    // R⁻¹, upper triangular, column by column.
    private static double[] Inverse(Regression m)
    {
        int p = m._p;
        var inv = new double[p * p];
        for (int c = 0; c < p; c++)
        {
            inv[c * p + c] = 1 / m._r[c * p + c];
            for (int j = c - 1; j >= 0; j--)
            {
                double s = 0;
                for (int k = j + 1; k <= c; k++) s += m._r[j * p + k] * inv[k * p + c];
                inv[j * p + c] = -s / m._r[j * p + j];
            }
        }
        return inv;
    }
}
