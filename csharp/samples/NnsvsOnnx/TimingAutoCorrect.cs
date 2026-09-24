using System.Globalization;
using System.Text.RegularExpressions;

namespace NnsvsOnnx;

/// <summary>
/// Literal port of the model's ENUNU timing_editor extension
/// (timing_auto_correct/enunu_timing_auto_correct.py, mono-label stage).
/// Rewrites the predicted timing label boundaries using the score label
/// boundaries: consonants are re-anchored to end at the note-on (taking their
/// predicted length, capped at consonant_retio of the previous phoneme),
/// vowels after rests start rest_X (default 5 ms) early, cl/br/devoiced
/// vowels get special handling. The ustparam/p16 stage of the same script is
/// ported separately in FullPipeline.RunCore.
/// </summary>
public static class TimingAutoCorrect
{
    /// <summary>Applies the correction in place to <paramref name="timing"/>'s
    /// start/end times. <paramref name="score"/> and <paramref name="timing"/>
    /// must be parallel (same phoneme rows), as in the reference pipeline.</summary>
    public static void Apply(HtsLabelFile score, HtsLabelFile timing, string extensionDir)
    {
        int n = score.Count;
        if (n != timing.Count)
        {
            Console.Error.WriteLine($"  [timing_auto_correct] row count mismatch (score={n}, timing={timing.Count}) -- skipped");
            return;
        }

        // --- settings.txt (index-ordered, matching the reference parser) ---
        var con = File.ReadAllLines(Path.Combine(extensionDir, "settings.txt"))
            .Where(l => l.Contains('='))
            .Select(l => l.Split('=', 2)[1].Trim())
            .ToArray();

        double conson = double.Parse(con[0], CultureInfo.InvariantCulture);
        conson = Math.Clamp(conson, 0.1, 0.9);
        double consonant = 1.0 / conson;
        double vowel = 1.0 / (1.0 - conson);
        double restA = double.Parse(con[1], CultureInfo.InvariantCulture);
        double restI = double.Parse(con[2], CultureInfo.InvariantCulture);
        double restU = double.Parse(con[3], CultureInfo.InvariantCulture);
        double restE = double.Parse(con[4], CultureInfo.InvariantCulture);
        double restO = double.Parse(con[5], CultureInfo.InvariantCulture);
        double restN = double.Parse(con[6], CultureInfo.InvariantCulture);
        bool cList = con[7].Equals("on", StringComparison.OrdinalIgnoreCase);
        double cScale = double.Parse(con[8], CultureInfo.InvariantCulture);

        // --- consonant_time.txt (fixed consonant lengths, only when enabled) ---
        var ctTime = new Dictionary<string, double>(StringComparer.Ordinal);
        string conPath = Path.Combine(extensionDir, "consonant_time.txt");
        if (cList && File.Exists(conPath))
        {
            foreach (var line in File.ReadAllLines(conPath))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    ctTime[parts[0]] = double.Parse(parts[1], CultureInfo.InvariantCulture) * cScale;
            }
        }

        // --- classification (mirrors the reference `types` array) ---
        var phone = new string[n];
        var types = new char[n];     // v / l (vl) / r / q (cl) / c
        var monoLen = new double[n];
        var timeLen = new double[n];
        for (int i = 0; i < n; i++)
        {
            phone[i] = ExtractPhoneme(score.Contexts[i]);
            monoLen[i] = score.EndTimes[i] - score.StartTimes[i];
            timeLen[i] = timing.EndTimes[i] - timing.StartTimes[i];
            types[i] = phone[i] switch
            {
                "a" or "i" or "u" or "e" or "o" or "N" => 'v',
                "A" or "I" or "U" or "E" or "O" => 'l',
                "pau" or "sl" or "br" => 'r',
                "cl" => 'q',
                _ => 'c',
            };
            if (cList && ctTime.TryGetValue(phone[i], out double fixedLen))
                timeLen[i] = fixedLen;
        }

        // --- boundary computation (literal port; times in 100ns units) ---
        var last1 = new long[n];
        var last2 = new long[n]; // default 0, as in the reference
        for (int i = 0; i < n; i++)
        {
            last1[i] = i == 0 ? score.StartTimes[0] : last2[i - 1];
            if (i >= n - 1) break;

            if (types[i + 1] == 'c')
            {
                if (types[i] != 'q')
                {
                    if (monoLen[i] >= timeLen[i + 1] * consonant)
                        last2[i] = (long)(score.EndTimes[i] - timeLen[i + 1]);
                    else
                        last2[i] = (long)(last1[i] + monoLen[i] / vowel);
                }
                else
                {
                    if (timeLen[i] >= timeLen[i + 1] * 2)
                        last2[i] = (long)(score.EndTimes[i] - timeLen[i + 1]);
                    else
                        last2[i] = (long)(last1[i] + timeLen[i] / 2);
                }
            }
            else if (types[i + 1] == 'q')
            {
                last2[i] = timing.EndTimes[i];
            }
            else
            {
                if (types[i] == 'r')
                {
                    double rest = phone[i + 1] switch
                    {
                        "a" => restA, "i" => restI, "u" => restU,
                        "e" => restE, "o" => restO, "N" => restN,
                        _ => double.NaN,
                    };
                    if (!double.IsNaN(rest) && monoLen[i + 1] > rest + 50000)
                        last2[i] = (long)(score.StartTimes[i + 1] - rest);
                    else
                        last2[i] = score.StartTimes[i + 1];
                }
                else if (types[i + 1] == 'l')
                {
                    // Devoiced vowel next: keep the unvoiced consonant tail audible.
                    if (types[i] == 'c' && i < n - 2)
                    {
                        if (types[i + 2] == 'r')
                            last2[i] = score.EndTimes[i + 1] - 50000;
                        else
                        {
                            last2[i] = score.StartTimes[i + 1];
                            if (last2[i] == 0) last2[i] = timing.StartTimes[i + 1];
                        }
                    }
                    // else: stays 0 (reference behavior)
                }
                else
                {
                    last2[i] = score.StartTimes[i + 1];
                    if (last2[i] == 0) last2[i] = timing.StartTimes[i + 1];
                }
            }
        }
        last2[n - 1] = score.EndTimes[n - 1];

        for (int i = 0; i < n; i++)
        {
            timing.StartTimes[i] = last1[i];
            timing.EndTimes[i] = last2[i];
        }
    }

    /// <summary>Extracts p4 (current phoneme) from a full-context label.</summary>
    private static string ExtractPhoneme(string context)
    {
        var m = Regex.Match(context, @"-(.+?)\+");
        return m.Success ? m.Groups[1].Value : context;
    }
}
