using System.Globalization;
using System.Text;

namespace NnsvsOnnx;

/// <summary>
/// C# port of utaupy.hts (Song/Note/Syllable/Phoneme/OneLine + the
/// autofill/reset_time/fill_contexts_from_songobj/adjust_*_contexts
/// algorithms), restricted to the single-syllable-per-note Japanese
/// pipeline used by utaupy.utils._ust2hts.ustobj2songobj. Produces HTS
/// full-context-label text lines byte-identical to the real Python output
/// (verified against real ENUNU-produced *_score.full gold files).
/// </summary>
public sealed class HtsPhoneme
{
    public long Start;
    public long End;

    /// <summary>p1: language-independent identity ('v','c','p','s','b' or "xx").</summary>
    public string P1 = "xx";
    /// <summary>p4: phoneme symbol.</summary>
    public string Identity = "xx";
    /// <summary>p9: flag.</summary>
    public string Flag = "xx";
    /// <summary>p12: position within syllable (1-indexed).</summary>
    public string Position = "xx";
    /// <summary>p13: position within syllable, counted from the end.</summary>
    public string PositionBackward = "xx";
    /// <summary>p14: distance from the previous vowel (consonants only).</summary>
    public string DistanceFromPreviousVowel = "xx";
    /// <summary>p15: distance to the next vowel (consonants only).</summary>
    public string DistanceToNextVowel = "xx";
    /// <summary>p16: always "xx" in this pipeline.</summary>
    public string UndefinedContext = "xx";

    public bool IsVowel() => P1 == "v";
    public bool IsConsonant() => P1 == "c";
    public bool IsRest() => P1 == "s" || P1 == "p";
    public bool IsBreak() => P1 == "b";
}

public sealed class HtsSyllable
{
    public List<HtsPhoneme> Phonemes { get; } = new();
    /// <summary>b1..b5 (b4,b5 are never set by this pipeline and stay "xx").</summary>
    public string[] Contexts = { "xx", "xx", "xx", "xx", "xx" };
}

public sealed class HtsNote
{
    public List<HtsSyllable> Syllables { get; } = new();
    /// <summary>e1..e60 (as previous/current/next note these map to d1-d9 / e1-e60 / f1-f9).</summary>
    public string[] Contexts = Enumerable.Repeat("xx", 60).ToArray();

    // Internal (not written to the label) helper fields used while computing e20/e21/e24/e25.
    public double? PositionNs100;
    public double? PositionNs100Backward;

    public string AbsolutePitch { get => Contexts[0]; set => Contexts[0] = value; }
    public string Tempo { get => Contexts[4]; set => Contexts[4] = value; }
    public string NumberOfSyllables { get => Contexts[5]; set => Contexts[5] = value; }
    public string Length10ms { get => Contexts[6]; set => Contexts[6] = value; }
    public string Length { get => Contexts[7]; set => Contexts[7] = value; }
    public string Position { get => Contexts[17]; set => Contexts[17] = value; }
    public string PositionBackward { get => Contexts[18]; set => Contexts[18] = value; }

    public int LengthInt => int.Parse(Length, CultureInfo.InvariantCulture);

    /// <summary>Note.length_100ns: float(25000000 * length / tempo), or null if either is "xx".</summary>
    public double? LengthNs100
    {
        get
        {
            if (Tempo == "xx" || Length == "xx") return null;
            return 25000000.0 * int.Parse(Length, CultureInfo.InvariantCulture) / int.Parse(Tempo, CultureInfo.InvariantCulture);
        }
    }

    public List<HtsPhoneme> AllPhonemes()
    {
        var list = new List<HtsPhoneme>();
        foreach (var syl in Syllables) list.AddRange(syl.Phonemes);
        return list;
    }

    /// <summary>Note.is_rest(): the first phoneme of the first syllable is a rest.</summary>
    public bool IsRest() => Syllables[0].Phonemes[0].IsRest();

    /// <summary>Note.is_break(): exactly one phoneme total, and it's a break (促音/息継ぎ).</summary>
    public bool IsBreak()
    {
        var phonemes = AllPhonemes();
        return phonemes.Count == 1 && phonemes[0].IsBreak();
    }
}

public sealed class HtsSong
{
    public List<HtsNote> Notes { get; } = new();
    /// <summary>j1..j3 (only j3, number_of_phrases, is ever set).</summary>
    public string[] Contexts = { "xx", "xx", "xx" };

    public IEnumerable<HtsSyllable> AllSyllables() => Notes.SelectMany(n => n.Syllables);
    public IEnumerable<HtsPhoneme> AllPhonemes() => AllSyllables().SelectMany(s => s.Phonemes);

    private static readonly string[] Vowels = { "a", "i", "u", "e", "o", "A", "I", "U", "E", "O", "N", "ae", "AE" };
    private static readonly string[] Breaks = { "br", "cl" };
    private static readonly string[] Pauses = { "pau" };
    private static readonly string[] Silences = { "sil" };

    public void Autofill()
    {
        FillPhonemeContexts();
        FillSyllableContexts();
        FillNoteContexts();
        FillSongContexts();
    }

    private void FillPhonemeContexts()
    {
        foreach (var ph in AllPhonemes())
        {
            string id = ph.Identity;
            if (id == "xx") ph.P1 = "xx";
            else if (Vowels.Contains(id)) ph.P1 = "v";
            else if (Pauses.Contains(id)) ph.P1 = "p";
            else if (Silences.Contains(id)) ph.P1 = "s";
            else if (Breaks.Contains(id)) ph.P1 = "b";
            else ph.P1 = "c";
        }

        foreach (var syl in AllSyllables())
        {
            int n = syl.Phonemes.Count;
            for (int i = 0; i < n; i++)
            {
                syl.Phonemes[i].Position = (i + 1).ToString(CultureInfo.InvariantCulture);
                syl.Phonemes[i].PositionBackward = (n - i).ToString(CultureInfo.InvariantCulture);
            }
        }

        foreach (var syl in AllSyllables())
        {
            int? distance = null;
            foreach (var ph in syl.Phonemes)
            {
                if (ph.IsVowel()) { ph.DistanceFromPreviousVowel = "xx"; distance = 1; }
                else if (distance is null) continue;
                else if (ph.IsConsonant()) { ph.DistanceFromPreviousVowel = distance.Value.ToString(CultureInfo.InvariantCulture); distance++; }
                else distance++;
            }
        }
        foreach (var syl in AllSyllables())
        {
            int? distance = null;
            for (int i = syl.Phonemes.Count - 1; i >= 0; i--)
            {
                var ph = syl.Phonemes[i];
                if (ph.IsVowel()) { ph.DistanceToNextVowel = "xx"; distance = 1; }
                else if (distance is null) continue;
                else if (ph.IsConsonant()) { ph.DistanceToNextVowel = distance.Value.ToString(CultureInfo.InvariantCulture); distance++; }
                else distance++;
            }
        }
    }

    private void FillSyllableContexts()
    {
        foreach (var note in Notes)
        {
            int lenNote = note.Syllables.Count;
            for (int i = 0; i < lenNote; i++)
            {
                var syl = note.Syllables[i];
                syl.Contexts[0] = syl.Phonemes.Count.ToString(CultureInfo.InvariantCulture);
                syl.Contexts[1] = (i + 1).ToString(CultureInfo.InvariantCulture);
                syl.Contexts[2] = (lenNote - i).ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private void FillNoteContexts()
    {
        foreach (var note in Notes)
        {
            note.NumberOfSyllables = note.Syllables.Count.ToString(CultureInfo.InvariantCulture);
            var lenNs100 = note.LengthNs100;
            if (lenNs100 is not null)
            {
                note.Length10ms = RoundHalfUp(lenNs100.Value / 100000.0).ToString(CultureInfo.InvariantCulture);
            }
        }

        FillE18E19();
        FillE22E23();
        FillE20E21Ns100();
        FillE24E25();
        FillE57E58();
    }

    private void FillE18E19()
    {
        int counter = 0;
        foreach (var note in Notes)
        {
            if (note.IsRest()) { counter = 0; note.Position = "xx"; }
            else { counter++; note.Position = counter.ToString(CultureInfo.InvariantCulture); }
        }
        counter = 0;
        for (int i = Notes.Count - 1; i >= 0; i--)
        {
            var note = Notes[i];
            if (note.IsRest()) { counter = 0; note.PositionBackward = "xx"; }
            else { counter++; note.PositionBackward = counter.ToString(CultureInfo.InvariantCulture); }
        }
    }

    private void FillE22E23()
    {
        int counter = 0;
        foreach (var note in Notes)
        {
            if (note.Position == "xx") { note.Contexts[21] = "xx"; counter = 0; }
            else if (note.Position == "1") { note.Contexts[21] = "0"; counter += note.LengthInt; }
            else { note.Contexts[21] = counter.ToString(CultureInfo.InvariantCulture); counter += note.LengthInt; }
        }
        counter = 0;
        for (int i = Notes.Count - 1; i >= 0; i--)
        {
            var note = Notes[i];
            if (note.PositionBackward == "xx") { counter = 0; note.Contexts[22] = "xx"; }
            else if (note.PositionBackward == "1") { counter = note.LengthInt; note.Contexts[22] = counter.ToString(CultureInfo.InvariantCulture); }
            else { counter += note.LengthInt; note.Contexts[22] = counter.ToString(CultureInfo.InvariantCulture); }
        }
    }

    private void FillE20E21Ns100()
    {
        double counterNs = 0;
        foreach (var note in Notes)
        {
            if (note.Position == "xx") { note.Contexts[19] = "xx"; note.PositionNs100 = null; counterNs = 0; }
            else if (note.Position == "1")
            {
                note.Contexts[19] = "0";
                note.PositionNs100 = 0;
                counterNs += note.LengthNs100!.Value;
            }
            else
            {
                note.Contexts[19] = RoundHalfUp(counterNs / 1000000.0).ToString(CultureInfo.InvariantCulture);
                note.PositionNs100 = counterNs;
                counterNs += note.LengthNs100!.Value;
            }
        }
        counterNs = 0;
        for (int i = Notes.Count - 1; i >= 0; i--)
        {
            var note = Notes[i];
            if (note.PositionBackward == "xx") { counterNs = 0; note.Contexts[20] = "xx"; note.PositionNs100Backward = null; }
            else if (note.PositionBackward == "1")
            {
                counterNs = note.LengthNs100!.Value;
                note.Contexts[20] = RoundHalfUp(counterNs / 1000000.0).ToString(CultureInfo.InvariantCulture);
                note.PositionNs100Backward = counterNs;
            }
            else
            {
                counterNs += note.LengthNs100!.Value;
                note.Contexts[20] = RoundHalfUp(counterNs / 1000000.0).ToString(CultureInfo.InvariantCulture);
                note.PositionNs100Backward = counterNs;
            }
        }
    }

    private void FillE24E25()
    {
        double phraseLengthNs = 0;
        double counterNs = 0;
        foreach (var note in Notes)
        {
            if (note.Position == "xx" || note.IsRest())
            {
                phraseLengthNs = 0; counterNs = 0; note.Contexts[23] = "xx";
            }
            else if (note.Position == "1")
            {
                phraseLengthNs = note.PositionNs100Backward!.Value;
                note.Contexts[23] = RoundHalfUp(100.0 * counterNs / phraseLengthNs).ToString(CultureInfo.InvariantCulture);
                counterNs += note.LengthNs100!.Value;
            }
            else
            {
                note.Contexts[23] = RoundHalfUp(100.0 * counterNs / phraseLengthNs).ToString(CultureInfo.InvariantCulture);
                counterNs += note.LengthNs100!.Value;
            }
        }

        foreach (var note in Notes)
        {
            if (note.Contexts[23] == "xx") note.Contexts[24] = "xx";
            else note.Contexts[24] = (100 - long.Parse(note.Contexts[23], CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void FillE57E58()
    {
        for (int i = 1; i < Notes.Count; i++)
        {
            var prev = Notes[i - 1];
            var cur = Notes[i];
            if (prev.IsRest() || prev.IsBreak() || cur.IsRest() || cur.IsBreak()) { cur.Contexts[56] = "xx"; continue; }
            long diff = AbspitchToNotenum(prev.AbsolutePitch) - AbspitchToNotenum(cur.AbsolutePitch);
            cur.Contexts[56] = (diff >= 0 ? "p" : "m") + Math.Abs(diff);
        }
        for (int i = 0; i < Notes.Count - 1; i++)
        {
            var cur = Notes[i];
            var next = Notes[i + 1];
            if (next.IsRest() || next.IsBreak() || cur.IsRest() || cur.IsBreak()) { cur.Contexts[57] = "xx"; continue; }
            long diff = AbspitchToNotenum(next.AbsolutePitch) - AbspitchToNotenum(cur.AbsolutePitch);
            cur.Contexts[57] = (diff >= 0 ? "p" : "m") + Math.Abs(diff);
        }
    }

    private void FillSongContexts()
    {
        bool previousIsRest = Notes[0].IsRest();
        int counter = previousIsRest ? 0 : 1;
        for (int i = 1; i < Notes.Count; i++)
        {
            bool currentIsRest = Notes[i].IsRest();
            if (previousIsRest && !currentIsRest) counter++;
            previousIsRest = currentIsRest;
        }
        Contexts[2] = counter.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Song.reset_time(): sets phoneme start/end purely from cumulative note.length_100ns, rounded half-up to the nearest 100ns tick.</summary>
    public void ResetTime()
    {
        long tStart = 0;
        double tEndF = 0;
        foreach (var note in Notes)
        {
            tEndF += note.LengthNs100!.Value;
            long tEnd = RoundHalfUp(tEndF);
            foreach (var ph in note.AllPhonemes())
            {
                ph.Start = tStart;
                ph.End = tEnd;
            }
            tStart = tEnd;
        }
    }

    private static long RoundHalfUp(double x) => (long)Math.Floor(x + (x >= 0 ? 0.5 : -0.5));

    private static readonly string[] NoteNames = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

    public static string NotenumToAbspitch(int notenum)
    {
        int octave = notenum / 12 - 1;
        int idx = ((notenum % 12) + 12) % 12;
        return NoteNames[idx] + octave.ToString(CultureInfo.InvariantCulture);
    }

    public static int AbspitchToNotenum(string abspitch)
    {
        string namePart = abspitch[..^1];
        int octave = int.Parse(abspitch[^1..], CultureInfo.InvariantCulture);
        int idx = Array.IndexOf(NoteNames, namePart);
        return idx + (octave + 1) * 12;
    }
}

/// <summary>One HTS full-context-label line, holding references to the surrounding
/// phonemes/syllables/notes (mirrors utaupy.hts.OneLine).</summary>
public sealed class HtsOneLine
{
    public HtsPhoneme BeforePreviousPhoneme = new();
    public HtsPhoneme PreviousPhoneme = new();
    public HtsPhoneme Phoneme = new();
    public HtsPhoneme NextPhoneme = new();
    public HtsPhoneme AfterNextPhoneme = new();

    public HtsSyllable PreviousSyllable = new();
    public HtsSyllable Syllable = new();
    public HtsSyllable NextSyllable = new();

    public HtsNote PreviousNote = new();
    public HtsNote Note = new();
    public HtsNote NextNote = new();

    public HtsSong Song = new();

    public long Start => Phoneme.Start;
    public long End => Phoneme.End;

    private static string J(string prefix, string[] seps, IReadOnlyList<string> vals)
    {
        var sb = new StringBuilder();
        sb.Append(prefix);
        sb.Append(vals[0]);
        for (int i = 0; i < seps.Length; i++)
        {
            sb.Append(seps[i]);
            sb.Append(vals[i + 1]);
        }
        return sb.ToString();
    }

    private static readonly string[] PSeps = { "@", "^", "-", "+", "=", "_", "%", "^", "_", "~", "-", "!", "[", "$", "]" };
    private static readonly string[] ASeps = { "-", "-", "@", "~" };
    private static readonly string[] BSeps = { "_", "_", "@", "|" };
    private static readonly string[] CSeps = { "+", "+", "@", "&" };
    private static readonly string[] DSeps = { "!", "#", "$", "%", "|", "&", ";", "-" };
    private static readonly string[] ESeps =
    {
        "]", "^", "=", "~", "!", "@", "#", "+", "]", "$", "|", "[", "&", "]", "=", "^", "~", "#", "_", ";",
        "$", "&", "%", "[", "|", "]", "-", "^", "+", "~", "=", "@", "$", "!", "%", "#", "|", "|", "-", "&",
        "&", "+", "[", ";", "]", ";", "~", "~", "^", "^", "@", "[", "#", "=", "!", "~", "+", "!", "^",
    };
    private static readonly string[] FSeps = { "#", "#", "-", "$", "$", "+", "%", ";" };
    private static readonly string[] GHISeps = { "_" };
    private static readonly string[] JSeps = { "~", "@" };

    public override string ToString()
    {
        var p = new[]
        {
            Phoneme.P1,
            BeforePreviousPhoneme.Identity, PreviousPhoneme.Identity, Phoneme.Identity, NextPhoneme.Identity, AfterNextPhoneme.Identity,
            BeforePreviousPhoneme.Flag, PreviousPhoneme.Flag, Phoneme.Flag, NextPhoneme.Flag, AfterNextPhoneme.Flag,
            Phoneme.Position, Phoneme.PositionBackward, Phoneme.DistanceFromPreviousVowel, Phoneme.DistanceToNextVowel, Phoneme.UndefinedContext,
        };

        var sb = new StringBuilder();
        sb.Append(Start).Append(' ').Append(End).Append(' ');
        sb.Append(J("", PSeps, p));
        sb.Append(J("/A:", ASeps, PreviousSyllable.Contexts));
        sb.Append(J("/B:", BSeps, Syllable.Contexts));
        sb.Append(J("/C:", CSeps, NextSyllable.Contexts));
        sb.Append(J("/D:", DSeps, PreviousNote.Contexts));
        sb.Append(J("/E:", ESeps, Note.Contexts));
        sb.Append(J("/F:", FSeps, NextNote.Contexts));
        sb.Append(J("/G:", GHISeps, new[] { "xx", "xx" }));
        sb.Append(J("/H:", GHISeps, new[] { "xx", "xx" }));
        sb.Append(J("/I:", GHISeps, new[] { "xx", "xx" }));
        sb.Append(J("/J:", JSeps, Song.Contexts));
        return sb.ToString();
    }
}

public static class HtsFullLabelBuilder
{
    /// <summary>Mirrors HTSFullLabel.fill_contexts_from_songobj() + fill_phonemes(),
    /// restricted to the (always true, for this pipeline) case of exactly one
    /// syllable per note.</summary>
    public static List<HtsOneLine> FillContextsFromSong(HtsSong song)
    {
        var dummyFront = MakeDummyNote();
        var dummyBack = MakeDummyNote();
        var notes = new List<HtsNote> { dummyFront };
        notes.AddRange(song.Notes);
        notes.Add(dummyBack);

        var lines = new List<HtsOneLine>();
        for (int iN = 1; iN < notes.Count - 1; iN++)
        {
            var prevNote = notes[iN - 1];
            var note = notes[iN];
            var nextNote = notes[iN + 1];
            var prevSyl = prevNote.Syllables[0];
            var syl = note.Syllables[0];
            var nextSyl = nextNote.Syllables[0];

            foreach (var phoneme in syl.Phonemes)
            {
                lines.Add(new HtsOneLine
                {
                    PreviousNote = prevNote,
                    Note = note,
                    NextNote = nextNote,
                    PreviousSyllable = prevSyl,
                    Syllable = syl,
                    NextSyllable = nextSyl,
                    Phoneme = phoneme,
                    Song = song,
                });
            }
        }

        // fill_phonemes(): link before-previous/previous/next/after-next phoneme,
        // padding with 2 default (all-"xx") phonemes on each side.
        var pad = new HtsPhoneme();
        for (int i = 0; i < lines.Count; i++)
        {
            var ol = lines[i];
            ol.BeforePreviousPhoneme = i - 2 >= 0 ? lines[i - 2].Phoneme : pad;
            ol.PreviousPhoneme = i - 1 >= 0 ? lines[i - 1].Phoneme : pad;
            ol.NextPhoneme = i + 1 < lines.Count ? lines[i + 1].Phoneme : pad;
            ol.AfterNextPhoneme = i + 2 < lines.Count ? lines[i + 2].Phoneme : pad;
        }

        return lines;
    }

    private static HtsNote MakeDummyNote()
    {
        var note = new HtsNote();
        var syl = new HtsSyllable();
        syl.Phonemes.Add(new HtsPhoneme());
        note.Syllables.Add(syl);
        return note;
    }

    /// <summary>adjust_break_contexts(): blanks e1-e3 for 促音/息継ぎ (break) notes.</summary>
    public static void AdjustBreakContexts(List<HtsOneLine> lines)
    {
        foreach (var ol in lines)
        {
            if (ol.Note.IsBreak())
            {
                ol.Note.Contexts[0] = "xx";
                ol.Note.Contexts[1] = "xx";
                ol.Note.Contexts[2] = "xx";
            }
        }
    }

    /// <summary>adjust_pau_contexts(strict=False): blanks b1-b3 and e1-e3 for rest notes.
    /// This is the branch used by simple_enunu.py (strict_sinsy_style=False).</summary>
    public static void AdjustPauContextsNonStrict(List<HtsOneLine> lines)
    {
        foreach (var ol in lines)
        {
            if (ol.Note.IsRest())
            {
                ol.Syllable.Contexts[0] = "xx";
                ol.Syllable.Contexts[1] = "xx";
                ol.Syllable.Contexts[2] = "xx";
                ol.Note.Contexts[0] = "xx";
                ol.Note.Contexts[1] = "xx";
                ol.Note.Contexts[2] = "xx";
            }
        }
    }
}
