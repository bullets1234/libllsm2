using System;
using System.IO;
using System.Runtime.InteropServices;

public static class Wav
{
    public static void WriteMono16(string path, float[] samples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        int numChannels = 1;
        short bitsPerSample = 16;
        int byteRate = sampleRate * numChannels * bitsPerSample / 8;
        short blockAlign = (short)(numChannels * bitsPerSample / 8);

        // Convert to 16-bit PCM
        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = MathF.Max(-1f, MathF.Min(1f, samples[i]));
            pcm[i] = (short)MathF.Round(s * 32767f);
        }
        int dataSize = pcm.Length * sizeof(short);

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // PCM fmt chunk size
        bw.Write((short)1); // PCM format
        bw.Write((short)numChannels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        // data chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        // write PCM data
        var span = MemoryMarshal.AsBytes(pcm.AsSpan());
        bw.Write(span);
    }

    public static float[] ReadMonoFloat(string path, out int sampleRate)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        // Read RIFF
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not RIFF");
        br.ReadInt32(); // file size
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not WAVE");

        short audioFormat = 1;
        short numChannels = 1;
        short bitsPerSample = 16;
        sampleRate = 0;
        byte[]? data = null;

        // Iterate chunks
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            long next = br.BaseStream.Position + size;
            if (id == "fmt ")
            {
                audioFormat = br.ReadInt16();
                numChannels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32(); // byteRate
                br.ReadInt16(); // blockAlign
                bitsPerSample = br.ReadInt16();
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            br.BaseStream.Position = next;
        }

        if (data == null) throw new InvalidDataException("No data chunk");
        if (numChannels < 1) throw new InvalidDataException("Invalid channels");

        if (audioFormat == 1 && bitsPerSample == 16)
        {
            // PCM16
            int samples = data.Length / 2 / numChannels;
            var output = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                int offset = i * numChannels * 2;
                short s = BitConverter.ToInt16(data, offset);
                output[i] = MathF.Max(-1f, MathF.Min(1f, s / 32768f));
            }
            return output;
        }
        else if (audioFormat == 3 && bitsPerSample == 32)
        {
            // IEEE float
            int samples = data.Length / 4 / numChannels;
            var output = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                int offset = i * numChannels * 4;
                output[i] = BitConverter.ToSingle(data, offset);
            }
            return output;
        }
        else
        {
            throw new NotSupportedException($"Unsupported WAV: format={audioFormat}, bits={bitsPerSample}");
        }
    }

    /// <summary>
    /// ReadMonoFloat のタプル版（samples, sampleRate を返す）
    /// </summary>
    public static (float[] samples, int sampleRate) ReadMono(string path)
    {
        var samples = ReadMonoFloat(path, out int sr);
        return (samples, sr);
    }
}
