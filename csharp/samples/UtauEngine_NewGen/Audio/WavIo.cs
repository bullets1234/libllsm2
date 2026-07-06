using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace UtauEngineNg.Audio
{
    /// <summary>
    /// モノラル WAV の読み書き。16bit PCM / 32bit float の読み込みに対応し、
    /// 書き込みは TPDF ディザリング付き 16bit PCM。
    /// （UtauEngine の Wav クラスを忠実に移植・整理したもの）
    /// </summary>
    public static class WavIo
    {
        /// <summary>16bit PCM モノラル WAV を書き出す（TPDF ディザリング適用）。</summary>
        public static void WriteMono16(string path, float[] samples, int sampleRate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            const int numChannels = 1;
            const short bitsPerSample = 16;
            int byteRate = sampleRate * numChannels * bitsPerSample / 8;
            short blockAlign = numChannels * bitsPerSample / 8;

            var pcm = new short[samples.Length];
            // TPDF（三角確率密度）ディザ。再現性のため固定シード。
            var rng = new Random(42);
            for (int i = 0; i < samples.Length; i++)
            {
                float s = MathF.Max(-1f, MathF.Min(1f, samples[i]));
                float tpdf = (float)(rng.NextDouble() - rng.NextDouble()) / 32768f;
                pcm[i] = (short)MathF.Round(s * 32767f + tpdf);
            }
            int dataSize = pcm.Length * sizeof(short);

            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)numChannels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write(bitsPerSample);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
            var span = MemoryMarshal.AsBytes(pcm.AsSpan());
            bw.Write(span);
        }

        /// <summary>モノラル化して読み込む。16bit PCM と 32bit float に対応。</summary>
        public static (float[] samples, int sampleRate) ReadMono(string path)
        {
            if (!File.Exists(path))
            {
                var bytes = Encoding.UTF8.GetBytes(path);
                throw new FileNotFoundException(
                    $"File not found: {path} (bytes: {string.Join(",", bytes.Take(50))})");
            }

            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not RIFF");
            br.ReadInt32();
            if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not WAVE");

            short audioFormat = 1;
            short numChannels = 1;
            short bitsPerSample = 16;
            int sampleRate = 0;
            byte[]? data = null;

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
                    br.ReadInt32();
                    br.ReadInt16();
                    bitsPerSample = br.ReadInt16();
                }
                else if (id == "data")
                {
                    data = br.ReadBytes(size);
                }
                br.BaseStream.Position = next;
            }

            if (data == null) throw new InvalidDataException("No data chunk");

            if (audioFormat == 1 && bitsPerSample == 16)
            {
                int samples = data.Length / 2 / numChannels;
                var output = new float[samples];
                for (int i = 0; i < samples; i++)
                {
                    int offset = i * numChannels * 2;
                    short s = BitConverter.ToInt16(data, offset);
                    output[i] = MathF.Max(-1f, MathF.Min(1f, s / 32768f));
                }
                return (output, sampleRate);
            }

            if (audioFormat == 3 && bitsPerSample == 32)
            {
                int samples = data.Length / 4 / numChannels;
                var output = new float[samples];
                for (int i = 0; i < samples; i++)
                {
                    int offset = i * numChannels * 4;
                    output[i] = BitConverter.ToSingle(data, offset);
                }
                return (output, sampleRate);
            }

            throw new NotSupportedException(
                $"Unsupported WAV: format={audioFormat}, bits={bitsPerSample}");
        }
    }
}
