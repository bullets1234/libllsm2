using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NnsvsOnnx;

/// <summary>One QS (binary) question: OR of one or more wildcard patterns.</summary>
public sealed class BinaryQuestion
{
    public required string Name { get; init; }
    public required List<Regex> Patterns { get; init; }
}

/// <summary>One CQS (continuous/numeric) question: a single pattern with one capture group.</summary>
public sealed class NumericQuestion
{
    public required string Name { get; init; }
    public required Regex Pattern { get; init; }
    /// <summary>The regex source text (pre-compile), used for the /D,/E,/F pitch-index
    /// detection in <see cref="QuestionSet.GetPitchIndices"/> (mirrors nnsvs checking
    /// the compiled Python regex's `.pattern` string).</summary>
    public required string PatternText { get; init; }
    public required double DefaultValue { get; init; }
}

/// <summary>
/// Port of nnmnkwii.io.hts.load_question_set / wildcards2regex and
/// nnmnkwii.frontend.merlin.pattern_matching_binary / pattern_matching_continous_position.
/// </summary>
public sealed class QuestionSet
{
    public List<BinaryQuestion> BinaryDict { get; } = new();
    public List<NumericQuestion> NumericDict { get; } = new();

    // Numeric-group tokens that are already valid (unescaped) regex and must be
    // passed through as-is rather than character-escaped. Order doesn't matter
    // for correctness (none is a prefix of another), longest-first is just defensive.
    private static readonly string[] NumberTokens = { "([-\\d]+)", "([\\d\\.]+)", "(\\d+)" };
    private static readonly string[] SvsTokens = { "([A-Z][b]?[0-9]+)", "([pm]\\d+)" };
    private const string NoteToken = "(\\NOTE)";
    private const string NoteReplacement = "([A-Z][b]?[0-9]+)";

    public static QuestionSet Load(string path, bool appendHatForLL = true, bool convertSvsPattern = true)
    {
        var qs = new QuestionSet();

        foreach (var rawLine in File.ReadLines(path))
        {
            string line = rawLine.Replace("\n", "").Replace("\r", "");
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int braceStart = line.IndexOf('{');
            int braceEnd = line.IndexOf('}');
            if (braceStart < 0 || braceEnd < 0 || braceEnd < braceStart) continue;
            string inner = line.Substring(braceStart + 1, braceEnd - braceStart - 1).Trim();
            var questionList = inner.Split(',');

            var spaceTokens = line.Split(' ');
            if (spaceTokens.Length < 2) continue;
            string type = spaceTokens[0];
            string questionKeyRaw = spaceTokens[1];
            string name = questionKeyRaw.Replace("\"", "").Replace("'", "");

            if (type == "CQS")
            {
                if (questionList.Length != 1)
                    throw new InvalidDataException($"CQS must have exactly one pattern: '{rawLine}'");
                string raw = questionList[0];
                string patternText = WildcardsToRegex(raw, convertNumberPattern: true, convertSvsPattern: convertSvsPattern);
                double defaultValue = raw.Contains("([-\\d]+)") ? -50.0 : -1.0;
                qs.NumericDict.Add(new NumericQuestion
                {
                    Name = name,
                    Pattern = new Regex(patternText, RegexOptions.Compiled),
                    PatternText = patternText,
                    DefaultValue = defaultValue,
                });
            }
            else if (type == "QS")
            {
                bool isLL = appendHatForLL && questionKeyRaw.Contains("LL-");
                var patterns = new List<Regex>();
                foreach (var q in questionList)
                {
                    string patternText = WildcardsToRegex(q, convertNumberPattern: false, convertSvsPattern: convertSvsPattern);
                    if (isLL && patternText.Length > 0 && patternText[0] != '^')
                        patternText = "^" + patternText;
                    patterns.Add(new Regex(patternText, RegexOptions.Compiled));
                }
                qs.BinaryDict.Add(new BinaryQuestion { Name = name, Patterns = patterns });
            }
            else
            {
                throw new InvalidDataException($"Unsupported question type '{type}' in line: '{rawLine}'");
            }
        }

        return qs;
    }

    /// <summary>Port of nnmnkwii.io.hts.wildcards2regex.</summary>
    public static string WildcardsToRegex(string question, bool convertNumberPattern, bool convertSvsPattern)
    {
        string prefix = "", postfix = "";
        if (question.Contains('*'))
        {
            if (!question.StartsWith("*")) prefix = "\\A";
            if (!question.EndsWith("*")) postfix = "\\z";
        }
        question = question.Trim('*');

        var tokens = new List<string>();
        if (convertNumberPattern) tokens.AddRange(NumberTokens);
        if (convertSvsPattern)
        {
            tokens.AddRange(SvsTokens);
            tokens.Add(NoteToken);
        }

        var sb = new StringBuilder();
        int i = 0;
        while (i < question.Length)
        {
            string? matchedToken = null;
            foreach (var tok in tokens)
            {
                if (i + tok.Length <= question.Length &&
                    string.CompareOrdinal(question, i, tok, 0, tok.Length) == 0)
                {
                    matchedToken = tok;
                    break;
                }
            }

            if (matchedToken != null)
            {
                sb.Append(matchedToken == NoteToken ? NoteReplacement : matchedToken);
                i += matchedToken.Length;
            }
            else if (question[i] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(question[i].ToString()));
                i++;
            }
        }

        return prefix + sb.ToString() + postfix;
    }

    public int[] PatternMatchBinary(string label)
    {
        var result = new int[BinaryDict.Count];
        for (int i = 0; i < BinaryDict.Count; i++)
        {
            int flag = 0;
            foreach (var re in BinaryDict[i].Patterns)
            {
                if (re.IsMatch(label)) { flag = 1; break; }
            }
            result[i] = flag;
        }
        return result;
    }

    public double[] PatternMatchContinuous(string label)
    {
        var result = new double[NumericDict.Count];
        for (int i = 0; i < NumericDict.Count; i++)
        {
            var nq = NumericDict[i];
            double value = nq.DefaultValue;
            var m = nq.Pattern.Match(label);
            if (m.Success)
            {
                string g = m.Groups[1].Value;
                if (TryNoteNameToMidi(g, out double midi))
                {
                    value = midi;
                }
                else if (g.Length > 0 && g[0] == 'p')
                {
                    value = double.Parse(g.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
                else if (g.Length > 0 && g[0] == 'm')
                {
                    value = -double.Parse(g.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
                else
                {
                    value = double.Parse(g, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }
            result[i] = value;
        }
        return result;
    }

    /// <summary>Port of nnsvs.io.hts.get_pitch_indices: finds the contiguous run of
    /// CQS entries (starting at numeric_dict[0], which must be a /D, /E or /F pitch
    /// question) that represent previous/current/next-note pitch.</summary>
    public List<int> GetPitchIndices()
    {
        static bool StartsWithDEF(string s) => s.StartsWith("/D") || s.StartsWith("/E") || s.StartsWith("/F");

        if (NumericDict.Count == 0 || !StartsWithDEF(NumericDict[0].PatternText))
            throw new InvalidOperationException("First CQS entry must be a /D, /E or /F pitch pattern");

        int pitchIdx = BinaryDict.Count;
        var pitchIndices = new List<int> { pitchIdx };
        int idx = 0;
        while (true)
        {
            idx++;
            if (idx >= NumericDict.Count || !StartsWithDEF(NumericDict[idx].PatternText))
                break;
            pitchIndices.Add(pitchIdx + idx);
        }
        return pitchIndices;
    }

    /// <summary>Port of nnsvs.io.hts.get_pitch_index (singular): linear scan for the
    /// FIRST CQS entry whose pattern text starts with "/E" (current-note pitch).
    /// Unlike <see cref="GetPitchIndices"/> (which only checks numeric_dict[0] then
    /// walks a contiguous /D,/E,/F run), this scans the whole numeric dict and only
    /// matches "/E", not "/D" or "/F". Returns len(BinaryDict) if no match is found
    /// (mirrors Python's default `pitch_idx = len(binary_dict)` before the loop).</summary>
    public int GetPitchIndex()
    {
        int idx = 0;
        int pitchIdx = BinaryDict.Count;
        while (idx < NumericDict.Count)
        {
            if (NumericDict[idx].PatternText.StartsWith("/E"))
            {
                pitchIdx += idx;
                break;
            }
            idx++;
        }
        return pitchIdx;
    }

    // A0=21 .. G9=127, standard MIDI formula: midi = (octave+1)*12 + pitch_class,
    // pitch_class: C=0,D=2,E=4,F=5,G=7,A=9,B=11 (with trailing 'b' = flat, -1).
    // Verified against nnmnkwii.frontend.NOTE_MAPPING (a hardcoded dict following
    // exactly this convention over the whole A0..G9 range).
    private static readonly Dictionary<char, int> PitchClass = new()
    {
        ['C'] = 0, ['D'] = 2, ['E'] = 4, ['F'] = 5, ['G'] = 7, ['A'] = 9, ['B'] = 11,
    };

    private static bool TryNoteNameToMidi(string s, out double midi)
    {
        midi = 0;
        if (string.IsNullOrEmpty(s)) return false;
        if (!PitchClass.TryGetValue(char.ToUpperInvariant(s[0]), out int pc)) return false;
        int i = 1;
        if (i < s.Length && s[i] == 'b') { pc -= 1; i++; }
        int start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i != s.Length || i == start) return false;
        int octave = int.Parse(s.Substring(start), CultureInfo.InvariantCulture);
        midi = (octave + 1) * 12 + pc;
        return true;
    }
}
