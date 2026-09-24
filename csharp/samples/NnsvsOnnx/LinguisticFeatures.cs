namespace NnsvsOnnx;

/// <summary>
/// Port of nnmnkwii.frontend.merlin.linguistic_features (score-level and
/// frame-level/coarse_coding paths only -- the subset actually used by
/// nnsvs.gen.predict_timelag / predict_duration / predict_acoustic) plus
/// nnsvs.gen's log_f0_conditioning (midi -> log-Hz + interp1d fill).
/// </summary>
public static class LinguisticFeatures
{
    /// <summary>add_frame_features=False, subphone_features=None: one output row
    /// per input label row (used for timelag's note-level subset and for duration's
    /// full phone-level labels).</summary>
    public static float[,] ScoreLevel(HtsLabelFile labels, QuestionSet qs)
    {
        int binSize = qs.BinaryDict.Count;
        int numSize = qs.NumericDict.Count;
        int n = labels.Count;
        var result = new float[n, binSize + numSize];

        for (int row = 0; row < n; row++)
        {
            string label = labels.Contexts[row];
            var bin = qs.PatternMatchBinary(label);
            var cont = qs.PatternMatchContinuous(label);
            for (int i = 0; i < binSize; i++) result[row, i] = bin[i];
            for (int i = 0; i < numSize; i++) result[row, binSize + i] = (float)cont[i];
        }

        return result;
    }

    /// <summary>add_frame_features=True, subphone_features="coarse_coding": one
    /// output row per frame (5ms frame period), with 4 extra coarse-coding columns
    /// appended (3 relative-position Gaussian-basis values + phone duration in frames).</summary>
    public static float[,] FrameLevel(HtsLabelFile labels, QuestionSet qs, long frameShift)
    {
        int binSize = qs.BinaryDict.Count;
        int numSize = qs.NumericDict.Count;
        int dictSize = binSize + numSize;
        const int frameFeatureSize = 4;
        int dim = dictSize + frameFeatureSize;

        int n = labels.Count;
        var frameCounts = new int[n];
        long totalFrames = 0;
        for (int row = 0; row < n; row++)
        {
            long s = labels.StartTimes[row] / frameShift;
            long e = labels.EndTimes[row] / frameShift;
            int fn = (int)(e - s);
            frameCounts[row] = fn;
            if (fn > 0) totalFrames += fn;
        }

        var result = new float[totalFrames, dim];
        var ccTable = CoarseCodingTable.Value;
        long outRow = 0;

        for (int row = 0; row < n; row++)
        {
            int fn = frameCounts[row];
            if (fn <= 0) continue;

            string label = labels.Contexts[row];
            var bin = qs.PatternMatchBinary(label);
            var cont = qs.PatternMatchContinuous(label);

            for (int i = 0; i < fn; i++)
            {
                for (int d = 0; d < binSize; d++) result[outRow, d] = bin[d];
                for (int d = 0; d < numSize; d++) result[outRow, binSize + d] = (float)cont[d];

                int relIdx = (int)(200.0 / fn * i); // truncation toward zero, matches Python int()
                result[outRow, dictSize + 0] = (float)ccTable[0, 300 + relIdx];
                result[outRow, dictSize + 1] = (float)ccTable[1, 200 + relIdx];
                result[outRow, dictSize + 2] = (float)ccTable[2, 100 + relIdx];
                result[outRow, dictSize + 3] = fn;
                outRow++;
            }
        }

        return result;
    }

    /// <summary>Mirrors nnsvs.gen's log_f0_conditioning: for each pitch column,
    /// converts positive midi note numbers to log-Hz (log(440 * 2^((midi-69)/12))),
    /// zeroes/negatives left as 0 initially, then fills all non-positive entries via
    /// nnmnkwii.preprocessing.f0.interp1d (boundary-extend + linear interpolation).</summary>
    public static void ApplyLogF0Conditioning(float[,] feats, IReadOnlyList<int> pitchIndices)
    {
        int n = feats.GetLength(0);
        foreach (var idx in pitchIndices)
        {
            var col = new double[n];
            for (int i = 0; i < n; i++)
            {
                double midi = feats[i, idx];
                col[i] = midi > 0 ? Math.Log(440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0)) : 0.0;
            }
            Interp1dContinuousF0(col);
            for (int i = 0; i < n; i++) feats[i, idx] = (float)col[i];
        }
    }

    /// <summary>Port of nnmnkwii.preprocessing.f0.interp1d(..., kind="slinear"): extends
    /// the first/last valid (&gt;0) values to the array boundaries, then linearly
    /// interpolates all remaining non-positive entries between neighboring valid points.
    /// Exposed as internal (rather than private) so <see cref="AcousticPostfilter"/> can
    /// reuse it for the lf0/f0 round-trip in the same assembly/namespace.</summary>
    internal static void Interp1dContinuousF0(double[] values)
    {
        int n = values.Length;
        var nonzero = new List<int>();
        for (int i = 0; i < n; i++) if (values[i] > 0) nonzero.Add(i);
        if (nonzero.Count == 0) return;

        values[0] = values[nonzero[0]];
        values[n - 1] = values[nonzero[^1]];

        var anchors = new List<int>();
        for (int i = 0; i < n; i++) if (values[i] > 0) anchors.Add(i);

        int lo = 0;
        for (int i = 0; i < n; i++)
        {
            if (values[i] > 0) continue;
            while (lo < anchors.Count - 1 && anchors[lo + 1] < i) lo++;
            int i0 = anchors[lo];
            int i1 = (lo + 1 < anchors.Count) ? anchors[lo + 1] : i0;
            if (i0 == i1) { values[i] = values[i0]; continue; }
            double t = (double)(i - i0) / (i1 - i0);
            values[i] = values[i0] + t * (values[i1] - values[i0]);
        }
    }

    /// <summary>Precomputed coarse-coding Gaussian-basis lookup table, mirroring
    /// nnmnkwii.frontend.merlin.compute_coarse_coding_features (3 Gaussians,
    /// sigma=0.4, means 0/0.5/1.0, each sampled at 600 points over ranges
    /// [-1.5,1.5] / [-1.0,2.0] / [-0.5,2.5]).</summary>
    private static readonly Lazy<double[,]> CoarseCodingTable = new(() =>
    {
        const int npoints = 600;
        const double sigma = 0.4;
        double[] mus = { 0.0, 0.5, 1.0 };
        double[] starts = { -1.5, -1.0, -0.5 };
        double[] ends = { 1.5, 2.0, 2.5 };

        var table = new double[3, npoints];
        for (int s = 0; s < 3; s++)
        {
            double step = (ends[s] - starts[s]) / (npoints - 1);
            for (int k = 0; k < npoints; k++)
            {
                double x = starts[s] + step * k;
                double z = (x - mus[s]) / sigma;
                table[s, k] = Math.Exp(-0.5 * z * z) / (sigma * Math.Sqrt(2.0 * Math.PI));
            }
        }
        return table;
    });
}
