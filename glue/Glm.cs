/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

// Glm: generalized linear models, as R's glm, of the binomial family with
// the logit link (logistic regression) and of the Poisson family with the
// log link, for (bjostat models).
//
// The fit is iteratively reweighted least squares, as R's glm.fit: from a
// first guess of the means, each step is a least-squares fit of the working
// response z = η + (y - μ) / μ'(η) on the predictors, each row weighted by
// μ'(η)² / V(μ). A step is a Regression of the rows times the root of their
// weights, so it is a QR by Givens rotations. The rows are kept, as each
// step reads all of them. The steps end when the deviance changes by less
// than 1e-8 of itself, or after 25 steps, as R's glm.control.

using Collections;

namespace Bjostat;

public sealed class Glm
{
    public const int Binomial = 0;
    public const int Poisson = 1;

    private readonly int _family;
    private readonly bool _intercept;
    private int _p = -1;
    private readonly List<double> _x = new();
    private readonly List<double> _y = new();
    private string _problem = "";

    // The fit.
    private double[] _b = System.Array.Empty<double>();
    private double[] _se = System.Array.Empty<double>();
    private double _deviance, _nullDeviance, _logLikelihood;
    private int _iterations;
    private bool _converged;

    private Glm(int family, bool intercept)
    {
        _family = family;
        _intercept = intercept;
    }

    public static Glm Make(int family, bool intercept) => new(family, intercept);

    public static Glm Add(Glm m, double y, RrbList<double> xs)
    {
        int k = xs.Count + (m._intercept ? 1 : 0);
        if (m._p < 0) m._p = k;
        if (m._problem != "") return m;
        long row = m._y.Count;
        if (k != m._p)
            m._problem = $"row {row} has {xs.Count} predictors, and the first row has {m._p - (m._intercept ? 1 : 0)}.";
        else if (m._family == Binomial && !(y >= 0 && y <= 1))
            m._problem = $"row {row} has the response {y}; a logistic model needs one from 0 to 1.";
        else if (m._family == Poisson && !(y >= 0))
            m._problem = $"row {row} has the response {y}; a Poisson model needs a count of at least 0.";
        if (m._problem != "") return m;
        if (m._intercept) m._x.Add(1);
        for (int i = 0; i < xs.Count; i++) m._x.Add(xs[i]);
        m._y.Add(y);
        return m;
    }

    // ---------------------------------------------------------------------
    // The family

    private double Mean(double eta) =>
        _family == Binomial ? 1 / (1 + Math.Exp(-eta)) : Math.Exp(eta);

    // dμ/dη: μ(1 - μ) of the logit, μ of the log. Kept from 0, as R's
    // binomial()$mu.eta keeps it above the machine epsilon.
    private double Slope(double mu) =>
        Math.Max(_family == Binomial ? mu * (1 - mu) : mu, 2.220446049250313e-16);

    // The deviance of one row, 2 (ln L(saturated) - ln L(μ)).
    private double UnitDeviance(double y, double mu)
    {
        static double YLog(double a, double b) => a == 0 ? 0 : a * Math.Log(a / b);
        return _family == Binomial
            ? 2 * (YLog(y, mu) + YLog(1 - y, 1 - mu))
            : 2 * (YLog(y, mu) - (y - mu));
    }

    private double LogLikelihood(double y, double mu)
    {
        if (_family == Binomial)
            return (y == 0 ? 0 : y * Math.Log(mu)) + (y == 1 ? 0 : (1 - y) * Math.Log(1 - mu));
        return (y == 0 ? 0 : y * Math.Log(mu)) - mu - Distributions.LogGamma(y + 1);
    }

    // ---------------------------------------------------------------------
    // The fit

    /// <summary>
    /// Fits the model and answers the empty string, or why it cannot be
    /// fitted, as Regression.Problem.
    /// </summary>
    public static string Fit(Glm m, string[] names)
    {
        if (m._problem != "") return m._problem;
        if (m._p < 0) return "there are no rows.";
        int p = m._p, n = m._y.Count;
        if (n <= p) return $"{n} rows are too few for {p} coefficients: a model needs more rows than coefficients.";

        // The first means, as R's mustart: (y + 1/2) / 2 of the binomial,
        // y + 1/10 of the Poisson.
        var mu = new double[n];
        var eta = new double[n];
        for (int i = 0; i < n; i++)
        {
            double y = m._y[i];
            mu[i] = m._family == Binomial ? (y + 0.5) / 2 : y + 0.1;
            eta[i] = m._family == Binomial ? Math.Log(mu[i] / (1 - mu[i])) : Math.Log(mu[i]);
        }
        double deviance = m.DevianceOf(mu);
        var row = new double[p];
        m._converged = false;
        for (m._iterations = 1; m._iterations <= 25; m._iterations++)
        {
            var step = m.Weighted(mu, eta, row);
            var problem = Regression.Problem(step, m.Columns(names));
            if (problem != "") return problem;
            m._b = Regression.Estimates(step);
            for (int i = 0; i < n; i++)
            {
                double e = 0;
                for (int j = 0; j < p; j++) e += m._x[i * p + j] * m._b[j];
                eta[i] = e;
                mu[i] = m.Mean(e);
            }
            double next = m.DevianceOf(mu);
            bool done = Math.Abs(next - deviance) / (Math.Abs(next) + 0.1) < 1e-8;
            deviance = next;
            if (done)
            {
                m._converged = true;
                break;
            }
        }
        if (m._iterations > 25) m._iterations = 25;
        m._deviance = deviance;
        // The errors are of the weights at the estimates, one pass more: the
        // weights of the last step are those of the estimates before it,
        // which differ by about 1e-4 when the deviance has stopped changing.
        // R takes the last step's. The dispersion is 1 for both families.
        m._se = Regression.UnscaledErrors(m.Weighted(mu, eta, row));
        m._logLikelihood = 0;
        for (int i = 0; i < n; i++) m._logLikelihood += m.LogLikelihood(m._y[i], mu[i]);

        // The null model: the mean of y with an intercept, or η = 0 without.
        double mean = m._y.Average();
        double nullMu = m._intercept ? mean : m.Mean(0);
        m._nullDeviance = 0;
        for (int i = 0; i < n; i++) m._nullDeviance += m.UnitDeviance(m._y[i], nullMu);
        return "";
    }

    // The weighted least squares of a step: the rows and the working
    // response, each times the root of its weight.
    private Regression Weighted(double[] mu, double[] eta, double[] row)
    {
        int p = _p, n = _y.Count;
        var step = Regression.Make(false);
        for (int i = 0; i < n; i++)
        {
            double d = Slope(mu[i]);
            double variance = _family == Binomial ? mu[i] * (1 - mu[i]) : mu[i];
            double w = Math.Sqrt(d * d / Math.Max(variance, 2.220446049250313e-16));
            double z = eta[i] + (_y[i] - mu[i]) / d;
            for (int j = 0; j < p; j++) row[j] = w * _x[i * p + j];
            step.AddRow(w * z, row);
        }
        return step;
    }

    // The names of the columns of Regression.Problem, which sees the
    // intercept as a column of its own.
    private string[] Columns(string[] names) =>
        _intercept ? new[] { "(Intercept)" }.Concat(names).ToArray() : names;

    private double DevianceOf(double[] mu)
    {
        double d = 0;
        for (int i = 0; i < mu.Length; i++) d += UnitDeviance(_y[i], mu[i]);
        return d;
    }

    public static double[] Estimates(Glm m) => m._b;

    public static double[] StandardErrors(Glm m) => m._se;

    public static double Deviance(Glm m) => m._deviance;

    public static double NullDeviance(Glm m) => m._nullDeviance;

    public static double LogLikelihood(Glm m) => m._logLikelihood;

    public static long Rows(Glm m) => m._y.Count;

    public static int Coefficients(Glm m) => m._p;

    public static bool Intercept(Glm m) => m._intercept;

    public static int Iterations(Glm m) => m._iterations;

    public static bool Converged(Glm m) => m._converged;
}
