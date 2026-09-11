namespace NnsvsOnnx;

/// <summary>
/// C# port of WORLD vocoder's (mmorise/World, MIT license) codec.cpp
/// <c>DecodeSpectralEnvelope</c> / <c>DecodeAperiodicity</c>, plus the
/// matlabfunctions.cpp <c>interp1</c>/<c>histc</c> helpers they depend on.
/// This is a direct algorithmic transliteration (double precision throughout,
/// matching WORLD's own use of double for the codec path) -- see Stage D
/// task notes for the source algorithm this was extracted from.
/// </summary>
public static class WorldCodec
{
    private const double KPi = 3.1415926535897932384;
    private const double KMySafeGuardMinimum = 0.000000000001;
    private const double KM0 = 1127.01048;
    private const double KF0 = 700.0;
    private const double KFloorFrequency = 40.0;
    private const double KCeilFrequency = 20000.0;
    private const double KFrequencyInterval = 3000.0;
    private const double KUpperLimit = 15000.0;
    private const double KFloorF0 = 71.0;
    private const double KLog2 = 0.69314718055994529;

    public static int GetFFTSizeForCheapTrick(int fs)
    {
        return (int)Math.Pow(2.0, 1.0 + (int)(Math.Log(3.0 * fs / KFloorF0 + 1) / KLog2));
    }

    public static int GetNumberOfAperiodicities(int fs)
    {
        return (int)(Math.Min(KUpperLimit, fs / 2.0 - KFrequencyInterval) / KFrequencyInterval);
    }

    private static double FreqToMel(double f) => KM0 * Math.Log(f / KF0 + 1.0);
    private static double MelToFreq(double mel) => KF0 * (Math.Exp(mel / KM0) - 1.0);

    /// <summary>
    /// Direct port of matlabfunctions.cpp's <c>histc</c>. Deliberately kept
    /// literal (including the <c>i--</c>/<c>count++</c> control-flow quirk) --
    /// do not "clean up", the boundary semantics depend on it.
    /// </summary>
    private static int[] Histc(double[] x, double[] xi)
    {
        int xLength = x.Length, edgesLength = xi.Length;
        int[] index = new int[edgesLength];
        int count = 1;
        int i = 0;
        for (; i < edgesLength; i++)
        {
            index[i] = 1;
            if (xi[i] >= x[0]) break;
        }
        for (; i < edgesLength; i++)
        {
            if (xi[i] < x[count])
            {
                index[i] = count;
            }
            else
            {
                index[i--] = count++;
            }
            if (count == xLength) break;
        }
        count--;
        for (i++; i < edgesLength; i++) index[i] = count;
        return index;
    }

    /// <summary>Direct port of matlabfunctions.cpp's <c>interp1</c> (piecewise-linear
    /// interpolation via the histc bucket index above). x must be monotonically
    /// increasing, length n; xi is length m; returns length m.</summary>
    public static double[] Interp1(double[] x, double[] y, double[] xi)
    {
        int n = x.Length, m = xi.Length;
        double[] h = new double[n - 1];
        for (int i = 0; i < n - 1; i++) h[i] = x[i + 1] - x[i];
        int[] k = Histc(x, xi);
        double[] yi = new double[m];
        for (int i = 0; i < m; i++)
        {
            double s = (xi[i] - x[k[i] - 1]) / h[k[i] - 1];
            yi[i] = y[k[i] - 1] + s * (y[k[i]] - y[k[i] - 1]);
        }
        return yi;
    }

    /// <summary>
    /// Port of codec.cpp's <c>DecodeAperiodicity</c>. No FFT involved -- pure
    /// dB-domain piecewise-linear interpolation of the coarse (5-band for 48kHz)
    /// coded aperiodicity, gated by a per-frame VUV heuristic (average of the
    /// raw coded values &gt; -0.5 => unvoiced => aperiodicity left at ~1.0 for
    /// every bin).
    /// </summary>
    /// <param name="codedAperiodicity">[t, numAp] coded aperiodicity (bap stream).</param>
    public static double[,] DecodeAperiodicity(double[,] codedAperiodicity, int t, int fs, int fftSize)
    {
        int numAp = GetNumberOfAperiodicities(fs);
        int nBin = fftSize / 2 + 1;
        var aperiodicity = new double[t, nBin];
        for (int i = 0; i < t; i++)
            for (int j = 0; j < nBin; j++)
                aperiodicity[i, j] = 1.0 - KMySafeGuardMinimum;

        var frequencyAxis = new double[nBin];
        for (int j = 0; j < nBin; j++) frequencyAxis[j] = (double)fs / fftSize * j;

        int coarseLen = numAp + 2;
        var coarseFrequencyAxis = new double[coarseLen];
        for (int k = 0; k <= numAp; k++) coarseFrequencyAxis[k] = k * KFrequencyInterval;
        coarseFrequencyAxis[numAp + 1] = fs / 2.0;

        var coarseAperiodicity = new double[coarseLen];
        for (int i = 0; i < t; i++)
        {
            double avg = 0;
            for (int k = 0; k < numAp; k++) avg += codedAperiodicity[i, k];
            avg /= numAp;
            if (avg > -0.5) continue;

            coarseAperiodicity[0] = -60.0;
            coarseAperiodicity[numAp + 1] = -KMySafeGuardMinimum;
            for (int k = 0; k < numAp; k++) coarseAperiodicity[k + 1] = codedAperiodicity[i, k];

            var interpolated = Interp1(coarseFrequencyAxis, coarseAperiodicity, frequencyAxis);
            for (int j = 0; j < nBin; j++)
                aperiodicity[i, j] = Math.Pow(10.0, interpolated[j] / 20.0);
        }
        return aperiodicity;
    }

    /// <summary>
    /// Port of codec.cpp's <c>DecodeSpectralEnvelope</c> (a.k.a. WORLD's own
    /// mel-scale codec -- NOT SPTK mel-cepstrum). Returns LINEAR POWER spectrum
    /// (already exponentiated), matching pyworld.decode_spectral_envelope's
    /// convention.
    /// </summary>
    /// <param name="codedSpectralEnvelope">[t, numDim] coded envelope (mgc stream).</param>
    public static double[,] DecodeSpectralEnvelope(double[,] codedSpectralEnvelope, int t, int fs, int fftSize, int numDim)
    {
        int maxDim = fftSize / 2;
        int nBin = fftSize / 2 + 1;

        double floorMel = FreqToMel(KFloorFrequency);
        double ceilMel = FreqToMel(Math.Min(fs / 2.0, KCeilFrequency));

        var weightReal = new double[numDim];
        var weightImag = new double[numDim];
        for (int i = 0; i < numDim; i++)
        {
            weightReal[i] = Math.Cos(i * KPi / fftSize) * Math.Sqrt(fftSize);
            weightImag[i] = Math.Sin(i * KPi / fftSize) * Math.Sqrt(fftSize);
        }
        weightReal[0] /= Math.Sqrt(2.0);

        var melAxis = new double[maxDim + 2];
        melAxis[0] = 0;
        for (int i = 0; i < maxDim; i++)
            melAxis[i + 1] = MelToFreq(floorMel + (ceilMel - floorMel) * i / maxDim);
        melAxis[maxDim + 1] = fs / 2.0;

        var frequencyAxis = new double[nBin];
        for (int j = 0; j < nBin; j++) frequencyAxis[j] = (double)j * fs / fftSize;

        double normalization = Math.Sqrt(maxDim);

        var spectrogram = new double[t, nBin];
        var inputRe = new double[maxDim];
        var inputIm = new double[maxDim];
        var melSpectrum = new double[maxDim + 2];

        for (int i = 0; i < t; i++)
        {
            Array.Clear(inputRe, 0, maxDim);
            Array.Clear(inputIm, 0, maxDim);
            for (int k = 0; k < numDim; k++)
            {
                double c = codedSpectralEnvelope[i, k];
                inputRe[k] = c * weightReal[k] * normalization;
                // NOTE: WORLD's actual InverseComplexFFT (fft.cpp BackwardFFT, c2c path) computes
                // out[n] = sum_k conj(input[k]) * exp(+i*2*pi*k*n/N), because internally it calls
                // Ooura's cdft with isgn=-1 (a forward, exp(-i...) transform) and then negates the
                // output imaginary part. The net effect is a conjugation of the *input* relative to
                // the naive "sum_k input[k]*exp(+i...)" formula. To compensate, we must NOT apply the
                // literal C++ "input[i][1] = -c*weight[i][1]*normalization" negation here, since our
                // ComplexIfftUnnormalized implements the naive (non-conjugating) sum_k in[k]*exp(+i...).
                // Feeding it conj(cppInput) = (Re, +c*weight[i][1]*normalization) reproduces WORLD's result.
                inputIm[k] = c * weightImag[k] * normalization;
            }

            ComplexIfftUnnormalized(inputRe, inputIm);

            for (int k = 0; k < maxDim / 2; k++)
            {
                melSpectrum[1 + k * 2] = inputRe[k];
                melSpectrum[1 + k * 2 + 1] = inputRe[maxDim - k - 1];
            }
            melSpectrum[0] = melSpectrum[1];
            melSpectrum[maxDim + 1] = melSpectrum[maxDim];

            var interpolated = Interp1(melAxis, melSpectrum, frequencyAxis);
            for (int j = 0; j < nBin; j++)
                spectrogram[i, j] = Math.Exp(interpolated[j] / maxDim);
        }
        return spectrogram;
    }

    /// <summary>Test-only hook exposing the unnormalized IFFT building block.</summary>
    internal static void DebugIfft(double[] re, double[] im) => ComplexIfftUnnormalized(re, im);

    /// <summary>
    /// Unnormalized complex inverse DFT (in-place): output[n] = sum_k input[k] *
    /// exp(+i*2*pi*k*n/N), i.e. the "textbook" inverse sum with NO 1/N scaling
    /// (matches WORLD's own fft_execute, which applies scaling only via the
    /// explicit sqrt(fft_size) `normalization` factor baked into the input).
    /// Iterative radix-2 Cooley-Tukey; n must be a power of 2 (guaranteed here
    /// since fftSize itself is a power of 2 via GetFFTSizeForCheapTrick).
    /// </summary>
    private static void ComplexIfftUnnormalized(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len; // inverse convention: +angle, no 1/N
            double wReal = Math.Cos(ang), wImag = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curReal = 1, curImag = 0;
                int half = len / 2;
                for (int j = 0; j < half; j++)
                {
                    double uRe = re[i + j], uIm = im[i + j];
                    double tRe = re[i + j + half], tIm = im[i + j + half];
                    double vRe = tRe * curReal - tIm * curImag;
                    double vIm = tRe * curImag + tIm * curReal;
                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + half] = uRe - vRe;
                    im[i + j + half] = uIm - vIm;
                    double nextReal = curReal * wReal - curImag * wImag;
                    double nextImag = curReal * wImag + curImag * wReal;
                    curReal = nextReal;
                    curImag = nextImag;
                }
            }
        }
    }
}
