using System.Numerics;

namespace NnsvsOnnx;

/// <summary>
/// Port of the exact subset of scipy.signal needed by nnsvs.dsp.lowpass_filter:
/// a digital Butterworth lowpass filter designed via
/// buttap -> lp2lp_zpk -> bilinear_zpk -> zpk2tf, applied zero-phase via
/// filtfilt (odd-extension padding, lfilter_zi steady-state initial conditions,
/// forward+backward Direct-Form-II-Transposed lfilter passes). Verified against
/// scipy 1.x source (scipy/signal/_filter_design.py, _signaltools.py).
/// </summary>
public static class DspFilters
{
    /// <summary>Mirrors nnsvs.dsp.lowpass_filter(x, sr, cutoff, N=5): designs an
    /// order-N Butterworth lowpass at `cutoff` Hz (sampling rate `fs` Hz) and applies
    /// it zero-phase via filtfilt. Short inputs (len(x) &lt;= max(len(a),len(b)) *
    /// (N//2+1)) are returned unchanged, matching nnsvs's own guard.</summary>
    public static double[] ButterworthLowpassFiltFilt(double[] x, double fs, double cutoff, int order = 5)
    {
        var (b, a) = ButterLowpassBa(order, cutoff, fs);
        int ntaps = Math.Max(a.Length, b.Length);
        if (x.Length <= ntaps * (order / 2 + 1))
            return (double[])x.Clone();

        return FiltFilt(b, a, x);
    }

    /// <summary>Port of scipy.signal.butter(N, Wn, "lowpass") for digital filters
    /// (fs not passed to scipy.signal.butter, so Wn is pre-normalized to [0,1] by the
    /// caller exactly like nnsvs.dsp.lowpass_filter does: Wn = cutoff / (fs // 2)).</summary>
    public static (double[] b, double[] a) ButterLowpassBa(int order, double cutoff, double fs)
    {
        int nyquist = (int)fs / 2; // fs // 2 (integer division, matches Python for integer fs)
        double wn = cutoff / nyquist;
        if (wn <= 0 || wn >= 1)
            throw new ArgumentOutOfRangeException(nameof(cutoff), "Digital filter critical frequency must satisfy 0 < Wn < 1");

        // buttap(N): z=[], p = -exp(i*pi*m/(2N)) for m in arange(-N+1, N, 2), k=1.
        var protoPoles = new Complex[order];
        for (int k = 0; k < order; k++)
        {
            int m = -order + 1 + 2 * k;
            double theta = Math.PI * m / (2.0 * order);
            protoPoles[k] = -new Complex(Math.Cos(theta), Math.Sin(theta));
        }
        double protoK = 1.0;

        // Pre-warp (iirfilter's internal digital path always uses fs=2.0):
        double warped = 2.0 * 2.0 * Math.Tan(Math.PI * wn / 2.0);

        // lp2lp_zpk(z=[], p=protoPoles, k=protoK, wo=warped): z stays empty.
        var pLp = new Complex[order];
        for (int k = 0; k < order; k++) pLp[k] = warped * protoPoles[k];
        double kLp = protoK * Math.Pow(warped, order); // degree = len(p)-len(z) = order

        // bilinear_zpk(z=[], p=pLp, k=kLp, fs=2.0):
        const double fs2 = 4.0; // 2.0 * fs(=2.0)
        var pZ = new Complex[order];
        Complex denomProd = Complex.One;
        for (int k = 0; k < order; k++)
        {
            pZ[k] = (fs2 + pLp[k]) / (fs2 - pLp[k]);
            denomProd *= fs2 - pLp[k];
        }
        // z_z = append([], -ones(degree)) -- all `order` zeros land at z=-1.
        // k_z = k_lp * real(prod(fs2 - z_orig) / prod(fs2 - p_lp)); prod over empty z_orig = 1.
        double kZ = (kLp / denomProd).Real;

        // zpk2tf: b = k_z * poly(zZeros) where all zeros are at -1 -> binomial coefficients.
        var b = new double[order + 1];
        for (int i = 0; i <= order; i++) b[i] = kZ * Binomial(order, i);

        // a = poly(pZ) (monic, descending powers); take .Real (imaginary parts cancel
        // via conjugate pole pairs up to floating-point noise).
        var aComplex = PolyFromRoots(pZ);
        var a = new double[order + 1];
        for (int i = 0; i <= order; i++) a[i] = aComplex[i].Real;

        return (b, a);
    }

    private static double Binomial(int n, int k)
    {
        double result = 1.0;
        for (int i = 0; i < k; i++) result = result * (n - i) / (i + 1);
        return result;
    }

    /// <summary>Computes coefficients (descending powers, monic) of the product of
    /// (x - r) over all roots r, mirroring numpy.poly(roots).</summary>
    private static Complex[] PolyFromRoots(Complex[] roots)
    {
        var coeffs = new Complex[] { Complex.One };
        foreach (var r in roots)
        {
            var next = new Complex[coeffs.Length + 1];
            for (int i = 0; i < coeffs.Length; i++)
            {
                next[i] += coeffs[i];
                next[i + 1] -= coeffs[i] * r;
            }
            coeffs = next;
        }
        return coeffs;
    }

    /// <summary>Port of scipy.signal.lfilter_zi(b, a) using the explicit closed-form
    /// solution given in scipy's own source comments (equivalent to solving
    /// zi = A*zi + B via linalg.solve, but non-iterative-matrix). Assumes a[0] == 1
    /// (already true here: zpk2tf on a monic pole polynomial).</summary>
    private static double[] LfilterZi(double[] b, double[] a)
    {
        int n = a.Length; // == b.Length
        var bb = new double[] { b[0] };
        var big = new double[n - 1];
        for (int i = 0; i < n - 1; i++) big[i] = b[i + 1] - a[i + 1] * b[0];

        double sumB = 0;
        for (int i = 0; i < n - 1; i++) sumB += big[i];
        double sumA1 = 0;
        for (int k = 1; k < n; k++) sumA1 += a[k];
        double denom = 1.0 + sumA1;

        var zi = new double[n - 1];
        zi[0] = sumB / denom;
        double asum = 1.0, csum = 0.0;
        for (int k = 1; k < n - 1; k++)
        {
            asum += a[k];
            csum += b[k] - a[k] * b[0];
            zi[k] = asum * zi[0] - csum;
        }
        _ = bb;
        return zi;
    }

    /// <summary>Direct Form II Transposed IIR filter with explicit initial state
    /// (mirrors scipy.signal.lfilter(b, a, x, zi=zi)). Assumes a[0] == 1 and
    /// len(a) == len(b).</summary>
    private static double[] Lfilter(double[] b, double[] a, double[] x, double[] zi)
    {
        int n = b.Length;
        var z = (double[])zi.Clone();
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            y[i] = b[0] * x[i] + z[0];
            for (int j = 1; j < n - 1; j++)
                z[j - 1] = b[j] * x[i] + z[j] - a[j] * y[i];
            z[n - 2] = b[n - 1] * x[i] - a[n - 1] * y[i];
        }
        return y;
    }

    /// <summary>Port of scipy.signal.odd_ext(x, n): odd (point) symmetric extension
    /// of length n at both ends.</summary>
    private static double[] OddExt(double[] x, int n)
    {
        int len = x.Length;
        var result = new double[len + 2 * n];
        for (int i = 0; i < n; i++) result[i] = 2.0 * x[0] - x[n - i];
        Array.Copy(x, 0, result, n, len);
        for (int i = 0; i < n; i++) result[n + len + i] = 2.0 * x[len - 1] - x[len - 2 - i];
        return result;
    }

    /// <summary>Port of scipy.signal.filtfilt(b, a, x) with default padtype='odd',
    /// padlen=None (-&gt; 3*max(len(a),len(b))), method='pad'.</summary>
    private static double[] FiltFilt(double[] b, double[] a, double[] x)
    {
        int ntaps = Math.Max(a.Length, b.Length);
        int edge = 3 * ntaps;

        var ext = OddExt(x, edge);
        var zi = LfilterZi(b, a);

        double x0 = ext[0];
        var ziX0 = new double[zi.Length];
        for (int i = 0; i < zi.Length; i++) ziX0[i] = zi[i] * x0;
        var y = Lfilter(b, a, ext, ziX0);

        double y0 = y[^1];
        var ziY0 = new double[zi.Length];
        for (int i = 0; i < zi.Length; i++) ziY0[i] = zi[i] * y0;
        var yRev = new double[y.Length];
        for (int i = 0; i < y.Length; i++) yRev[i] = y[y.Length - 1 - i];
        var y2 = Lfilter(b, a, yRev, ziY0);

        var yFinal = new double[y2.Length];
        for (int i = 0; i < y2.Length; i++) yFinal[i] = y2[y2.Length - 1 - i];

        var trimmed = new double[yFinal.Length - 2 * edge];
        Array.Copy(yFinal, edge, trimmed, 0, trimmed.Length);
        return trimmed;
    }
}
