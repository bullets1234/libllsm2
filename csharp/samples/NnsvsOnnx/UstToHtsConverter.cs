using System.Globalization;

namespace NnsvsOnnx;

/// <summary>
/// Top-level UST → HTS full-context-label converter. C# port of
/// enulib.utauplugin2score.utauplugin2score() + utaupy.utils._ust2hts
/// (ustnote2htsnote/ustobj2songobj) + utaupy.hts.Song.write(strict_sinsy_style=False),
/// which is exactly the path used by simple_enunu.py's ENUNU pipeline.
/// </summary>
public static class UstToHtsConverter
{
    /// <summary>Converts a .ust file to HTS full-context-label lines (3-column
    /// "start end context_string" format, 100ns units), matching the real
    /// ENUNU/utaupy output for strict_sinsy_style=False.</summary>
    public static List<string> Convert(string ustPath, string tablePath)
    {
        var ust = UstFile.Load(ustPath);
        var table = KanaTable.Load(tablePath);

        if (ust.Notes.Count < 2)
        {
            throw new InvalidDataException("ENUNU requires at least 2 notes.");
        }

        // enulib.utauplugin2score: blank empty lyrics to 'R'; escape '-'/'+' in flags.
        foreach (var note in ust.Notes)
        {
            if (note.Lyric.Trim(' ', '\u3000').Length == 0)
            {
                note.SetLyric("R");
            }
            if (note.Flags.Length != 0)
            {
                note.Flags = note.Flags.Replace('-', 'n').Replace('+', 'p');
            }
        }

        // Model extension "flag_separator" (ust_editor stage): notes with no
        // flags (except rests) get the model default intensity flag p9=0300
        // ('p9:0300/p16:s3s0r0b0' default; the p9 part is written back to the
        // UST note flags). This drives the hed's loud/normal/soft voice-color
        // questions; without it the model sings weakly and unstably.
        foreach (var note in ust.Notes)
        {
            if (note.Flags.Length == 0 && note.Lyric != "R")
            {
                note.Flags = "0300";
            }
        }

        var song = new HtsSong();
        foreach (var ustNote in ust.Notes)
        {
            song.Notes.Add(UstNoteToHtsNote(ustNote, table));
        }

        song.Autofill();
        song.ResetTime();

        var lines = HtsFullLabelBuilder.FillContextsFromSong(song);
        HtsFullLabelBuilder.AdjustBreakContexts(lines);
        HtsFullLabelBuilder.AdjustPauContextsNonStrict(lines);

        return lines.Select(l => l.ToString()).ToList();
    }

    /// <summary>utaupy.utils._ust2hts.ustnote2htsnote (key_of_the_note is never
    /// used by this pipeline, so e2/e3/e4 stay "xx").</summary>
    private static HtsNote UstNoteToHtsNote(UstNote ustNote, Dictionary<string, List<string>> table)
    {
        var note = new HtsNote
        {
            AbsolutePitch = HtsSong.NotenumToAbspitch(ustNote.NoteNum),
            Tempo = RoundHalfEven(ustNote.Tempo).ToString(CultureInfo.InvariantCulture),
            Length = RoundHalfEven(ustNote.Length / 20.0).ToString(CultureInfo.InvariantCulture),
        };

        var kanaLyrics = ustNote.Lyric.Replace("っ", " っ ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var phonemeStrings = new List<string>();
        foreach (var lyric in kanaLyrics)
        {
            if (table.TryGetValue(lyric, out var mapped)) phonemeStrings.AddRange(mapped);
            else phonemeStrings.Add(lyric);
        }

        var syllable = new HtsSyllable();
        foreach (var phonemeStr in phonemeStrings)
        {
            var phoneme = new HtsPhoneme { Identity = phonemeStr };
            if (ustNote.Flags.Length != 0) phoneme.Flag = ustNote.Flags;
            syllable.Phonemes.Add(phoneme);
        }
        note.Syllables.Add(syllable);

        return note;
    }

    /// <summary>Python's round(): round-half-to-even.</summary>
    private static long RoundHalfEven(double x) => (long)Math.Round(x, MidpointRounding.ToEven);
}
