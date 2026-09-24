namespace NnsvsOnnx;

/// <summary>Minimal port of utaupy.table.load_table_file: a whitespace-separated
/// "kana phoneme1 phoneme2 ..." text table (kana2phonemes.table).</summary>
public static class KanaTable
{
    public static Dictionary<string, List<string>> Load(string path)
    {
        var table = new Dictionary<string, List<string>>();
        foreach (var rawLine in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            table[parts[0]] = parts.Skip(1).ToList();
        }
        return table;
    }
}
