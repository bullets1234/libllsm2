namespace NnsvsOnnx;

/// <summary>
/// Port of nnsvs.gen.postprocess_acoustic's GV post-filter (nnsvs.postfilters.
/// variance_scaling) + trajectory smoothing (nnsvs.dsp.lowpass_filter, via
/// <see cref="DspFilters"/>) + bap clipping, applied to the already
/// out-scaler-denormalized 67-dim acoustic feature vector (mgc[60] + lf0[1] +
/// vuv[1] + bap[5]). Config assumed: feature_type="world", post_filter_type="gv",
/// relative_f0=False, trajectory_smoothing=True (defaults used by the reference
/// pipeline for this model), no dynamic features (static-only streams, matching
/// the rest of this C# port), no vibrato streams, optional force_fix_vuv (V/UV
/// correction-by-phone is out of scope for this port, same as before).
/// </summary>
public static class AcousticPostfilter
{
    public const double Fs = 200.0; // modfs = 1 / (frame_period_ms * 0.001) for frame_period=5ms
    public const double MgcCutoff = 50.0;
    public const double BapCutoff = 50.0;
    public const double Lf0Cutoff = 20.0;
    public const int FilterOrder = 5;
    public const double BapClipMin = -60.0;
    public const double BapClipMax = 0.0;

    public sealed class Result
    {
        public float[,] Mgc = null!;
        public float[,] Bap = null!;
        public float[] F0Final = null!;
        public int[] Vuv = null!;

        // Intermediates, exposed for gold-comparison verification.
        public float[,] MgcGv = null!;
        public double[] Lf0Prefilter = null!;
        public double[] Lf0Smoothed = null!;
        public int[] NoteFrameIndices = null!;
    }

    public static Result Apply(
        float[] physical, int t, int outDim, int mgcDim, int bapDim,
        double[] gv, float[] rawPitchColumn, double vuvThreshold = 0.5,
        bool[]? forceVoiced = null, bool[]? forceUnvoiced = null)
    {
        // Step 1: note_frame_indices = frames where the raw (non-log-f0-conditioned)
        // score pitch column is > 0 (mirrors nnsvs.io.hts.get_note_frame_indices).
        var noteFrameIndices = new List<int>();
        for (int i = 0; i < t; i++)
            if (rawPitchColumn[i] > 0) noteFrameIndices.Add(i);

        // Step 2: extract mgc/lf0raw/vuv/bap from the flat physical (out-scaler
        // denormalized) array. lf0raw/vuv here are the raw acoustic-model outputs,
        // matching gen_spsvs_static_features's `target_f0` / `vuv` streams.
        var mgc = new double[t, mgcDim];
        var lf0Raw = new double[t];
        var vuv = new int[t];
        var bap = new double[t, bapDim];
        for (int i = 0; i < t; i++)
        {
            int o = i * outDim;
            for (int d = 0; d < mgcDim; d++) mgc[i, d] = physical[o + d];
            lf0Raw[i] = physical[o + mgcDim];
            vuv[i] = physical[o + mgcDim + 1] >= vuvThreshold ? 1 : 0;
            for (int d = 0; d < bapDim; d++) bap[i, d] = physical[o + mgcDim + 2 + d];
        }

        // Step 2': force_fix_vuv=True (nnsvs.gen.correct_vuv_by_phone): the reference
        // overwrites the vuv probability with 1.0/0.0 based on hed phone flags BEFORE
        // thresholding; applying the same masks post-threshold is equivalent.
        // Order matters: voiced first, then unvoiced/sil overrides (matches reference).
        if (forceVoiced != null)
            for (int i = 0; i < t; i++) if (forceVoiced[i]) vuv[i] = 1;
        if (forceUnvoiced != null)
            for (int i = 0; i < t; i++) if (forceUnvoiced[i]) vuv[i] = 0;

        // Step 3: GV post-filter (nnsvs.postfilters.variance_scaling(gv, mgc,
        // offset=2, note_frame_indices=note_frame_indices)): population mean/var
        // (ddof=0) over note_frame_indices rows only, columns [2, mgcDim), applied
        // in-place only to those rows; all other rows/columns pass through unchanged.
        bool skipGv = Environment.GetEnvironmentVariable("NNSVS_SKIP_GV") == "1";
        if (noteFrameIndices.Count > 0 && !skipGv)
        {
            int n = noteFrameIndices.Count;
            double scaleMin = double.PositiveInfinity, scaleMax = double.NegativeInfinity, scaleSum = 0;
            for (int d = 2; d < mgcDim; d++)
            {
                double mean = 0;
                foreach (var i in noteFrameIndices) mean += mgc[i, d];
                mean /= n;

                double varSum = 0;
                foreach (var i in noteFrameIndices)
                {
                    double diff = mgc[i, d] - mean;
                    varSum += diff * diff;
                }
                double variance = varSum / n; // population variance (ddof=0)

                double scale = Math.Sqrt(gv[d] / variance);
                if (scale < scaleMin) scaleMin = scale;
                if (scale > scaleMax) scaleMax = scale;
                scaleSum += scale;
                foreach (var i in noteFrameIndices)
                    mgc[i, d] = scale * (mgc[i, d] - mean) + mean;
            }
            if (Environment.GetEnvironmentVariable("NNSVS_GV_DEBUG") == "1")
                Console.WriteLine($"    [gv] T={t} noteFrames={n} scale min/mean/max = {scaleMin:F3}/{scaleSum / (mgcDim - 2):F3}/{scaleMax:F3}");
        }

        var mgcGv = new float[t, mgcDim];
        for (int i = 0; i < t; i++)
            for (int d = 0; d < mgcDim; d++)
                mgcGv[i, d] = (float)mgc[i, d];

        // Step 4: f0/lf0 round-trip (gen_spsvs_static_features, relative_f0=False path):
        //   f0 = lf0raw; f0[vuv < thresh] = 0; f0[nonzero] = exp(f0[nonzero])
        //   lf0 = f0.copy(); lf0[nonzero] = log(f0[nonzero]); lf0 = interp1d(lf0, "slinear")
        var f0 = new double[t];
        for (int i = 0; i < t; i++)
        {
            double v = lf0Raw[i];
            if (vuv[i] < 1) v = 0;
            if (v != 0) v = Math.Exp(v);
            f0[i] = v;
        }
        var lf0 = new double[t];
        for (int i = 0; i < t; i++)
            lf0[i] = f0[i] != 0 ? Math.Log(f0[i]) : 0.0;
        LinguisticFeatures.Interp1dContinuousF0(lf0);
        var lf0Prefilter = (double[])lf0.Clone();

        // Step 5: trajectory smoothing (Butterworth zero-phase lowpass, order 5).
        var lf0Smoothed = DspFilters.ButterworthLowpassFiltFilt(lf0, Fs, Lf0Cutoff, FilterOrder);

        var mgcCol = new double[t];
        var mgcSmoothed = new double[t, mgcDim];
        for (int d = 0; d < mgcDim; d++)
        {
            for (int i = 0; i < t; i++) mgcCol[i] = mgc[i, d];
            var filtered = DspFilters.ButterworthLowpassFiltFilt(mgcCol, Fs, MgcCutoff, FilterOrder);
            for (int i = 0; i < t; i++) mgcSmoothed[i, d] = filtered[i];
        }

        var bapCol = new double[t];
        var bapSmoothed = new double[t, bapDim];
        for (int d = 0; d < bapDim; d++)
        {
            for (int i = 0; i < t; i++) bapCol[i] = bap[i, d];
            var filtered = DspFilters.ButterworthLowpassFiltFilt(bapCol, Fs, BapCutoff, FilterOrder);
            for (int i = 0; i < t; i++) bapSmoothed[i, d] = filtered[i];
        }

        // Step 6: bap clip to [-60, 0] (use_mcep_aperiodicity == bapDim > 5 == false here).
        var bapClipped = new float[t, bapDim];
        for (int i = 0; i < t; i++)
            for (int d = 0; d < bapDim; d++)
                bapClipped[i, d] = (float)Math.Clamp(bapSmoothed[i, d], BapClipMin, BapClipMax);

        // Step 7: final f0 in Hz (gen_world_params): exp(lf0Smoothed), gated by vuv.
        var f0Final = new float[t];
        for (int i = 0; i < t; i++)
            f0Final[i] = vuv[i] == 1 ? (float)Math.Exp(lf0Smoothed[i]) : 0f;

        var mgcFinal = new float[t, mgcDim];
        for (int i = 0; i < t; i++)
            for (int d = 0; d < mgcDim; d++)
                mgcFinal[i, d] = (float)mgcSmoothed[i, d];

        return new Result
        {
            Mgc = mgcFinal,
            Bap = bapClipped,
            F0Final = f0Final,
            Vuv = vuv,
            MgcGv = mgcGv,
            Lf0Prefilter = lf0Prefilter,
            Lf0Smoothed = lf0Smoothed,
            NoteFrameIndices = noteFrameIndices.ToArray(),
        };
    }
}
