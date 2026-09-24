using System.Globalization;

namespace NnsvsOnnx;

/// <summary>
/// C# port of utau_pitch.writing_pitch() (SimpleEnunu-0.3.1 fork,
/// G:\SimpleEnunu-0.3.1_TunedWavOut\utau_pitch.py): builds a continuous F0
/// curve (in Hz) directly from a .ust file's note numbers and pitchbend
/// (PBS/PBW/PBY/PBM) portamento curves, at 1ms resolution then decimated
/// every 5th sample (matching NNSVS's frame_period=5ms).
///
/// This is the exact mechanism the reference renderer (simple_enunu.py) uses
/// to REPLACE the acoustic model's own F0 prediction entirely (see its
/// `read_lf0` parameter to SimpleEnunu.svs(), which discards the model's
/// lf0_original and substitutes this UST-derived curve wholesale, still
/// gated by the model's own V/UV decision). Since UST note transitions are
/// always smoothed by the note's own portamento (sigmoid) curve, the
/// reference renderer never produces the abrupt pitch-onset jumps that the
/// acoustic model's raw F0 prediction can exhibit at unvoiced-&gt;voiced
/// boundaries.
///
/// NOTE: vibrato (VBR) mixing is present in the original script but wrapped
/// in a triple-quoted (dead) code block there, so it is NOT applied by the
/// reference renderer either -- intentionally omitted here for fidelity.
/// NOTE: the "s" (linear) PBM curve type is dead code in the original script
/// too (its np.linspace(...,100) assignment is unconditionally overwritten
/// two lines later by the generic sigmoid computation), so "s" behaves
/// identically to the default sigmoid (center=0.5) here as well, matching
/// that (buggy) reference behavior exactly.
/// </summary>
public static class UstPitchCurve
{
    private const double SigmoidCurve = -6.0;

    /// <summary>Builds the UST-derived F0 curve in Hz, at 5ms/frame
    /// resolution (native UST note timing). Consumers align it to the
    /// model-predicted frame count by plain consecutive slicing (see
    /// FullPipeline step g'), matching the reference's read_lf0 slicing --
    /// never by time-stretching.</summary>
    public static double[] BuildF0Hz(UstFile ust)
    {
        var totalT = new List<double>();
        var totalP = new List<double>(); // pitch in "10-cent" units: notenum*10 + shift(10cent)

        double endTime = 0.0;
        int notenum = 60;
        string prevLyric = "R";

        foreach (var note in ust.Notes)
        {
            double tempo = note.Tempo;
            int length = note.Length;
            double timeLength = 60000.0 / tempo * length / 480.0; // ms
            double startTime = endTime;
            endTime += timeLength;
            long endTimeRounded = RoundHalfEven(endTime);
            long startTimeRounded = RoundHalfEven(startTime);

            int lastPitch = notenum;
            notenum = note.NoteNum;
            int pitchDifference = lastPitch - notenum;

            bool hasPbs = note.Fields.TryGetValue("PBS", out var pbsRaw);
            double pbsTime, pbsShift;
            if (hasPbs)
            {
                var parts = pbsRaw!.Split(';');
                if (parts.Length == 2)
                {
                    pbsTime = ParseD(parts[0]);
                    double shiftVal = parts[1].Length == 0 ? 0.0 : ParseD(parts[1]);
                    pbsShift = (prevLyric == "R" || pbsTime >= 0) ? shiftVal : shiftVal + pitchDifference * 10;
                }
                else
                {
                    pbsTime = ParseD(parts[0]);
                    pbsShift = pitchDifference * 10;
                }
            }
            else
            {
                pbsTime = 0.0;
                pbsShift = pitchDifference * 10;
            }

            var pbw = ParseCsvDoubles(note.Fields, "PBW", new List<double> { Math.Round(timeLength) });
            var pby = ParseCsvDoubles(note.Fields, "PBY", new List<double> { 0.0 });
            var pbm = ParseCsvStrings(note.Fields, "PBM");

            while (pby.Count < pbw.Count) pby.Add(0.0);

            var timePoints = new List<double> { pbsTime };
            foreach (var w in pbw) timePoints.Add(timePoints[^1] + w);

            var pitchShifts = new List<double> { pbsShift };
            pitchShifts.AddRange(pby);

            if (pbsTime < 0)
            {
                int removeCount = (int)(-pbsTime);
                if (removeCount > 0)
                {
                    int newLen = Math.Max(0, totalT.Count - removeCount);
                    totalT.RemoveRange(newLen, totalT.Count - newLen);
                    totalP.RemoveRange(newLen, totalP.Count - newLen);
                }
            }
            else if (pbsTime > 0)
            {
                int n = (int)pbsTime;
                double addPitch = hasPbs ? lastPitch * 10.0 : notenum * 10.0;
                for (int i = 0; i < n; i++)
                {
                    totalT.Add(totalT.Count > 0 ? totalT[^1] + 1 : 0);
                    totalP.Add(addPitch);
                }
            }

            var timeValues = new List<double>();
            var pitchValues = new List<double>();

            for (int i = 0; i < pbw.Count; i++)
            {
                double tStart = timePoints[i] + startTimeRounded;
                double tEnd = timePoints[i + 1] + startTimeRounded;
                double pStart = pitchShifts[i] + notenum * 10.0;
                double pEnd = pitchShifts[i + 1] + notenum * 10.0;

                double center = 0.5;
                if (pbm.Count > i + 1)
                {
                    if (pbm[i] == "r") center = 0.0;
                    else if (pbm[i] == "j") center = 1.0;
                    else center = 0.5;
                }

                int count = (int)Math.Round(tEnd - tStart);
                if (count <= 0) continue;

                if (count == 1)
                {
                    timeValues.Add(tStart);
                    pitchValues.Add(pStart);
                    continue;
                }

                var sig = new double[count];
                double sigMin = double.PositiveInfinity, sigMax = double.NegativeInfinity;
                for (int k = 0; k < count; k++)
                {
                    double x = (double)k / (count - 1);
                    double s = 1.0 / (1.0 + Math.Exp(SigmoidCurve * (x - center)));
                    sig[k] = s;
                    if (s < sigMin) sigMin = s;
                    if (s > sigMax) sigMax = s;
                }
                double range = sigMax - sigMin;
                for (int k = 0; k < count; k++)
                {
                    double norm = range != 0 ? (sig[k] - sigMin) / range : 0.0;
                    double p = norm * (pEnd - pStart) + pStart;
                    double t = tStart + (tEnd - 1 - tStart) * ((double)k / (count - 1));
                    timeValues.Add(t);
                    pitchValues.Add(p);
                }
            }

            totalT.AddRange(timeValues);
            totalP.AddRange(pitchValues);

            if (totalT.Count < endTimeRounded)
            {
                long shortCount = endTimeRounded - totalT.Count;
                double addPitch = notenum * 10.0;
                for (long i = 0; i < shortCount; i++)
                {
                    totalT.Add(totalT.Count > 0 ? totalT[^1] + 1 : 0);
                    totalP.Add(addPitch);
                }
            }

            prevLyric = note.Lyric;
        }

        int nDec = (totalP.Count + 4) / 5; // ceil(count/5), matches python's [::5] slice length
        var f0Hz = new double[nDec];
        for (int i = 0; i < nDec; i++)
        {
            double pitchSemitone = totalP[i * 5] / 10.0;
            f0Hz[i] = 440.0 * Math.Pow(2.0, (pitchSemitone - 69.0) / 12.0);
        }
        return f0Hz;
    }

    private static double ParseD(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static List<double> ParseCsvDoubles(Dictionary<string, string> fields, string key, List<double> defaultValue)
    {
        if (!fields.TryGetValue(key, out var raw) || raw.Length == 0) return defaultValue;
        var tokens = raw.Split(',');
        var result = new List<double>(tokens.Length);
        foreach (var tok in tokens)
            result.Add(tok.Trim().Length == 0 ? 0.0 : ParseD(tok));
        return result;
    }

    private static List<string> ParseCsvStrings(Dictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out var raw)) return new List<string> { "" };
        return raw.Split(',').ToList();
    }

    private static long RoundHalfEven(double x) => (long)Math.Round(x, MidpointRounding.ToEven);
}
