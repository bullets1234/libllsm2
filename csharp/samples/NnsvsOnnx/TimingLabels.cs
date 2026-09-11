namespace NnsvsOnnx;

/// <summary>
/// Port of nnsvs.gen's timelag/duration post-processing and timing-label
/// generation (predict_timelag's post-model steps + predict_duration's
/// post-model steps + postprocess_duration). Reference (read-only):
/// G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master\nnsvs\gen.py
/// </summary>
public static class TimingLabels
{
    private const long HtsFrameShift = 50000;

    /// <summary>Port of nnsvs.io.hts._is_silence / nnsvs.gen._is_silence.</summary>
    public static bool IsSilence(string context)
    {
        bool isFullContext = context.Contains('@');
        if (isFullContext)
            return context.Contains("-sil") || context.Contains("-pau");
        return context == "sil" || context == "pau";
    }

    /// <summary>Port of predict_timelag's post-model steps: round the denormalized
    /// MDN mu to the nearest frame, clip per-note to allowedRange (allowedRangeRest
    /// if the note is silence), then convert frames -&gt; 100ns units.</summary>
    /// <param name="muDenorm">Denormalized timelag mu, one value per note (N_notes).</param>
    /// <param name="noteLabels">Note-level labels (same order as muDenorm).</param>
    public static double[] PostprocessTimelag(float[] muDenorm, HtsLabelFile noteLabels,
        double[] allowedRange, double[] allowedRangeRest)
    {
        int n = noteLabels.Count;
        if (muDenorm.Length != n)
            throw new ArgumentException($"muDenorm length {muDenorm.Length} does not match note count {n}");

        var lag = new double[n];
        for (int i = 0; i < n; i++)
        {
            double v = Math.Round((double)muDenorm[i]);

            double lo, hi;
            if (IsSilence(noteLabels.Contexts[i]))
            {
                lo = allowedRangeRest[0];
                hi = allowedRangeRest[1];
            }
            else
            {
                lo = allowedRange[0];
                hi = allowedRange[1];
            }
            v = Math.Min(Math.Max(v, lo), hi);

            lag[i] = v * HtsFrameShift;
        }
        return lag;
    }

    /// <summary>Port of nnsvs.gen.postprocess_duration (MDN variant only, since
    /// both timelag and duration models here are MDN/PROBABILISTIC).</summary>
    /// <param name="labels">Full phone-level (rounded) labels, same order/count as mu/sigmaSq.</param>
    /// <param name="mu">Denormalized duration mu, one value per phone (N_phones).</param>
    /// <param name="sigmaSq">Denormalized duration sigma^2, one value per phone (N_phones).</param>
    /// <param name="lag">Timelag (100ns units), one value per note (N_notes), from <see cref="PostprocessTimelag"/>.</param>
    public static HtsLabelFile PostprocessDuration(HtsLabelFile labels, double[] mu, double[] sigmaSq, double[] lag)
    {
        int n = labels.Count;
        if (mu.Length != n || sigmaSq.Length != n)
            throw new ArgumentException("mu/sigmaSq length must match labels count");

        var noteIndices = labels.GetNoteIndices();
        noteIndices.Add(n); // sentinel: end of labels

        if (lag.Length != noteIndices.Count - 1)
            throw new ArgumentException($"lag length {lag.Length} does not match note count {noteIndices.Count - 1}");

        var output = new HtsLabelFile { FrameShift = labels.FrameShift };

        for (int i = 1; i < noteIndices.Count; i++)
        {
            int s0 = noteIndices[i - 1];
            int e0 = noteIndices[i];
            int len = e0 - s0;

            // eq (11): L = duration (in frames) of the FIRST phone of the note.
            // All phones within a note share the same start/end at score-label
            // time, so any phone would give the same value (fe.duration_features(p)[0]).
            double frameNumber = (double)(labels.EndTimes[s0] - labels.StartTimes[s0]) / HtsFrameShift;
            long L = (long)frameNumber; // numpy int-dtype array assignment truncates toward zero

            double lagVal = lag[i - 1];
            double lHat = (i < noteIndices.Count - 1)
                ? L - (lag[i - 1] - lag[i]) / HtsFrameShift
                : L - lag[i - 1] / HtsFrameShift;
            lHat = Math.Max(lHat, 1.0);

            // Adjust the start time of every phone in the segment (only index 0
            // is actually consumed below via set_durations' offset, but computed
            // for all phones to mirror the numpy elementwise semantics exactly).
            var newStart = new double[len];
            for (int j = 0; j < len; j++)
            {
                double shifted = Math.Min(
                    labels.StartTimes[s0 + j] + lagVal,
                    labels.EndTimes[s0 + j] - HtsFrameShift * len);
                shifted = Math.Max(shifted, 0.0);
                if (output.Count > 0)
                    shifted = Math.Max(shifted, output.StartTimes[^1] + HtsFrameShift);
                newStart[j] = shifted;
            }

            // eq (17)/(16): variance-scaling normalized phoneme durations.
            double muSum = 0, sigmaSum = 0;
            for (int j = 0; j < len; j++)
            {
                muSum += mu[s0 + j];
                sigmaSum += sigmaSq[s0 + j];
            }
            double rho = (lHat - muSum) / sigmaSum;

            var dNorm = new double[len];
            bool anyNonPositive = false;
            for (int j = 0; j < len; j++)
            {
                dNorm[j] = mu[s0 + j] + rho * sigmaSq[s0 + j];
                if (dNorm[j] <= 0) anyNonPositive = true;
            }
            if (anyNonPositive)
            {
                // eq (12) fallback: uniform scaling using mu as d_hat.
                for (int j = 0; j < len; j++)
                    dNorm[j] = lHat * mu[s0 + j] / muSum;
            }
            for (int j = 0; j < len; j++)
            {
                dNorm[j] = Math.Round(dNorm[j]);
                if (dNorm[j] <= 0) dNorm[j] = 1;
            }

            // HTSLabelFile.set_durations: offset = start_times[0];
            // end_times = offset + cumsum(d * frame_shift); start_times = [offset, end_times[:-1]].
            long offset = (long)Math.Round(newStart[0]);
            var segStart = new long[len];
            var segEnd = new long[len];
            long cum = offset;
            for (int j = 0; j < len; j++)
            {
                cum += (long)dNorm[j] * HtsFrameShift;
                segEnd[j] = cum;
            }
            segStart[0] = offset;
            for (int j = 1; j < len; j++) segStart[j] = segEnd[j - 1];

            if (output.Count > 0)
                output.EndTimes[^1] = segStart[0];

            for (int j = 0; j < len; j++)
            {
                output.StartTimes.Add(segStart[j]);
                output.EndTimes.Add(segEnd[j]);
                output.Contexts.Add(labels.Contexts[s0 + j]);
            }
        }

        return output;
    }
}
