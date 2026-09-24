using System.Globalization;

namespace NnsvsOnnx;

/// <summary>
/// Minimal C# port of utaupy.ust (Ust/Note classes), just enough to read a
/// .ust file and expose per-note Length/Lyric/NoteNum/Tempo/Flags, including
/// the local-tempo-inheritance ("_hidden_dict['Tempo']") algorithm from
/// Ust.reload_tempo/reload_local_value.
/// </summary>
public sealed class UstNote
{
    /// <summary>Raw key=value pairs as found in the file (order preserved), e.g. "Length", "Lyric", "NoteNum", "Tempo", "Flags".</summary>
    public Dictionary<string, string> Fields { get; } = new();

    /// <summary>Resolved local tempo used when this note has no explicit "Tempo" entry (mirrors note._hidden_dict['Tempo']).</summary>
    public double HiddenTempo { get; set; }

    public int Length => int.Parse(Fields["Length"], CultureInfo.InvariantCulture);

    public string Lyric => Fields.TryGetValue("Lyric", out var v) ? v : "";

    public void SetLyric(string v) => Fields["Lyric"] = v;

    public int NoteNum => int.Parse(Fields["NoteNum"], CultureInfo.InvariantCulture);

    /// <summary>Mirrors Note.tempo: self.get('Tempo', self._hidden_dict['Tempo']).</summary>
    public double Tempo => Fields.TryGetValue("Tempo", out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : HiddenTempo;

    /// <summary>Mirrors Note.flags: self.get('Flags', '').</summary>
    public string Flags
    {
        get => Fields.TryGetValue("Flags", out var v) ? v : "";
        set => Fields["Flags"] = value;
    }
}

public sealed class UstFile
{
    public List<UstNote> Notes { get; } = new();
    public Dictionary<string, string> Setting { get; } = new();

    /// <summary>
    /// Loads a .ust file. Mirrors utaupy.ust.Ust.load(): tries cp932 first,
    /// then utf-8, then utf-8-sig, on UnicodeDecodeError.
    /// </summary>
    public static UstFile Load(string path)
    {
        string text = ReadWithFallback(path);

        var ust = new UstFile();
        // Split into per-tag blocks the same way as Ust.load(): split on "[#",
        // then re-prefix "[#" to each piece (the first split-piece before any
        // "[#" is discarded, matching the Python `[1:]`).
        var blocks = new List<string>();
        int idx = 0;
        while (true)
        {
            int next = text.IndexOf("[#", idx, StringComparison.Ordinal);
            if (next < 0) break;
            int after = text.IndexOf("[#", next + 2, StringComparison.Ordinal);
            string block = after < 0 ? text.Substring(next) : text.Substring(next, after - next);
            blocks.Add(block.TrimEnd());
            idx = after < 0 ? text.Length : after;
            if (after < 0) break;
        }

        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            string tag = lines[0].TrimEnd('\r').Trim();

            if (tag == "[#VERSION]")
            {
                continue;
            }

            var fields = new Dictionary<string, string>();
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                fields[line.Substring(0, eq)] = line.Substring(eq + 1);
            }

            if (tag == "[#SETTING]")
            {
                foreach (var kv in fields) ust.Setting[kv.Key] = kv.Value;
            }
            else if (tag == "[#TRACKEND]" || tag == "[#PREV]" || tag == "[#NEXT]")
            {
                // Not needed for full-context-label generation of the main note sequence.
            }
            else
            {
                var note = new UstNote();
                foreach (var kv in fields) note.Fields[kv.Key] = kv.Value;
                ust.Notes.Add(note);
            }
        }

        ust.ReloadTempo();
        return ust;
    }

    private static string ReadWithFallback(string path)
    {
        try
        {
            return File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)).Trim();
        }
        catch (System.Text.DecoderFallbackException)
        {
        }
        catch (ArgumentException)
        {
        }

        RegisterCodePages();
        try
        {
            var cp932 = System.Text.Encoding.GetEncoding(932, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
            return File.ReadAllText(path, cp932).Trim();
        }
        catch (System.Text.DecoderFallbackException)
        {
        }

        return File.ReadAllText(path, new System.Text.UTF8Encoding(true)).Trim();
    }

    private static bool s_codePagesRegistered;
    private static void RegisterCodePages()
    {
        if (s_codePagesRegistered) return;
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        s_codePagesRegistered = true;
    }

    /// <summary>
    /// Port of Ust.reload_tempo() / reload_local_value('Tempo'): resolves the
    /// per-note effective tempo (own explicit "Tempo" entry, else inherited
    /// from the previous note) into each note's HiddenTempo field.
    /// </summary>
    private void ReloadTempo()
    {
        if (Notes.Count == 0) return;

        double currentValue = Setting.TryGetValue("Tempo", out var t0) ? double.Parse(t0, CultureInfo.InvariantCulture)
            : Notes[0].Fields.TryGetValue("Tempo", out var nt0) ? double.Parse(nt0, CultureInfo.InvariantCulture) : 120.0;

        if (Notes[0].Fields.TryGetValue("Tempo", out var firstTempo))
        {
            currentValue = double.Parse(firstTempo, CultureInfo.InvariantCulture);
        }

        foreach (var note in Notes)
        {
            if (note.Fields.TryGetValue("Tempo", out var ownTempo))
            {
                double v = double.Parse(ownTempo, CultureInfo.InvariantCulture);
                if (v == currentValue)
                {
                    note.Fields.Remove("Tempo");
                }
                else
                {
                    currentValue = v;
                }
            }
            note.HiddenTempo = currentValue;
        }
    }
}
