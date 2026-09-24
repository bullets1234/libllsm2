using System.Text;
using System.Text.RegularExpressions;

namespace NnsvsOnnx;

/// <summary>
/// Minimal reader for .npy (v1.0/v2.0) files: float32/float64, C-order,
/// 1-D or 2-D arrays only (sufficient for the MinMax/Standard scaler
/// coefficient files shipped with the NNSVS model).
/// </summary>
public static class Npy
{
    public sealed record NpyArray(int[] Shape, double[] Data);

    public static NpyArray Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        var magic = br.ReadBytes(6);
        if (magic.Length != 6 || magic[0] != 0x93 || magic[1] != (byte)'N' || magic[2] != (byte)'U'
            || magic[3] != (byte)'M' || magic[4] != (byte)'P' || magic[5] != (byte)'Y')
        {
            throw new InvalidDataException($"'{path}' is not a valid .npy file");
        }

        byte major = br.ReadByte();
        br.ReadByte(); // minor, unused

        int headerLen = major == 1 ? br.ReadUInt16() : (int)br.ReadUInt32();
        string header = Encoding.ASCII.GetString(br.ReadBytes(headerLen));

        string descr = ExtractString(header, "descr");
        bool fortranOrder = ExtractBool(header, "fortran_order");
        if (fortranOrder)
        {
            throw new NotSupportedException("fortran_order=True .npy files are not supported");
        }

        int[] shape = ExtractShape(header);
        long count = 1;
        foreach (var s in shape) count *= s;

        var data = new double[count];
        if (descr.Contains("f4"))
        {
            for (long i = 0; i < count; i++) data[i] = br.ReadSingle();
        }
        else if (descr.Contains("f8"))
        {
            for (long i = 0; i < count; i++) data[i] = br.ReadDouble();
        }
        else
        {
            throw new NotSupportedException($"unsupported npy dtype '{descr}' in '{path}'");
        }

        return new NpyArray(shape, data);
    }

    private static string ExtractString(string header, string key)
    {
        var m = Regex.Match(header, $"'{key}'\\s*:\\s*'([^']*)'");
        if (!m.Success) throw new InvalidDataException($"npy header missing '{key}'");
        return m.Groups[1].Value;
    }

    private static bool ExtractBool(string header, string key)
    {
        var m = Regex.Match(header, $"'{key}'\\s*:\\s*(True|False)");
        if (!m.Success) throw new InvalidDataException($"npy header missing '{key}'");
        return m.Groups[1].Value == "True";
    }

    private static int[] ExtractShape(string header)
    {
        var m = Regex.Match(header, "'shape'\\s*:\\s*\\(([^)]*)\\)");
        if (!m.Success) throw new InvalidDataException("npy header missing 'shape'");
        var inner = m.Groups[1].Value.Trim();
        if (inner.Length == 0) return new[] { 1 };
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s.Trim()))
            .ToArray();
    }
}
