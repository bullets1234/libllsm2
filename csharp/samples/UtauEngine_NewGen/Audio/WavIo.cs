using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace UtauEngineNg.Audio
{
    /// <summary>
    /// モノラル WAV の読み書き。8/16/24/32bit PCM・32/64bit float・
    /// WAVE_FORMAT_EXTENSIBLE の読み込みに対応（ステレオはチャンネル平均でモノラル化）。
    /// 書き込みは TPDF ディザリング付き 16bit PCM。
    /// （UtauEngine の Wav クラスを移植・堅牢化したもの）
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
            // 量子化ステップ（=1 LSB）振幅の三角分布を「32767 スケール後」に加える。
            // （旧実装は 1/32768 を加えており実質ディザなしだった）
            var rng = new Random(42);
            for (int i = 0; i < samples.Length; i++)
            {
                float s = MathF.Max(-1f, MathF.Min(1f, samples[i]));
                float tpdf = (float)(rng.NextDouble() - rng.NextDouble()); // ±1 LSB
                pcm[i] = (short)Math.Clamp(MathF.Round(s * 32767f + tpdf), short.MinValue, short.MaxValue);
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

        private const int WaveFormatPcm = 1;
        private const int WaveFormatFloat = 3;
        private const int WaveFormatExtensible = 0xFFFE;

        /// <summary>
        /// モノラル化（チャンネル平均）して読み込む。
        /// 8/16/24/32bit PCM・32/64bit float・EXTENSIBLE、奇数長チャンクのパディングに対応。
        /// </summary>
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
            // チャンク ID は生バイトで読む（ReadChars は UTF-8 デコードで 0x80 以上の
            // バイトを含む ID でストリームがずれる）
            string ReadId() => Encoding.ASCII.GetString(br.ReadBytes(4));

            if (ReadId() != "RIFF") throw new InvalidDataException("Not RIFF");
            br.ReadUInt32();
            if (ReadId() != "WAVE") throw new InvalidDataException("Not WAVE");

            int audioFormat = 0;
            int numChannels = 0;
            int bitsPerSample = 0;
            int sampleRate = 0;
            bool haveFmt = false;
            byte[]? data = null;

            while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
            {
                string id = ReadId();
                uint size = br.ReadUInt32();
                // RIFF 規約: 奇数長チャンクは 1 バイトパディング（LIST/ICMT 等の
                // メタデータ付き音源でパーサがずれる原因だった）
                long next = br.BaseStream.Position + size + (size & 1);
                if (next < br.BaseStream.Position || next > br.BaseStream.Length)
                    next = br.BaseStream.Length; // 破損サイズは以降を打ち切り

                if (id == "fmt " && size >= 16)
                {
                    audioFormat = br.ReadUInt16();
                    numChannels = br.ReadUInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32();  // byteRate
                    br.ReadInt16();  // blockAlign
                    bitsPerSample = br.ReadUInt16();
                    haveFmt = true;

                    // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID の先頭 2 バイトが実フォーマット
                    if (audioFormat == WaveFormatExtensible && size >= 40)
                    {
                        br.ReadUInt16(); // cbSize
                        br.ReadUInt16(); // validBitsPerSample
                        br.ReadUInt32(); // channelMask
                        audioFormat = br.ReadUInt16();
                    }
                }
                else if (id == "data")
                {
                    long avail = br.BaseStream.Length - br.BaseStream.Position;
                    data = br.ReadBytes((int)Math.Min(size, (uint)Math.Min(avail, int.MaxValue)));
                }
                br.BaseStream.Position = next;
            }

            if (!haveFmt || sampleRate <= 0 || numChannels <= 0)
                throw new InvalidDataException("No valid fmt chunk");
            if (data == null) throw new InvalidDataException("No data chunk");

            bool supported =
                (audioFormat == WaveFormatPcm && bitsPerSample is 8 or 16 or 24 or 32) ||
                (audioFormat == WaveFormatFloat && bitsPerSample is 32 or 64);
            if (!supported)
                throw new NotSupportedException(
                    $"Unsupported WAV: format={audioFormat}, bits={bitsPerSample}");

            int bytesPerSample = bitsPerSample / 8;
            int frameSize = bytesPerSample * numChannels;
            int nFrames = data.Length / frameSize;
            var output = new float[nFrames];

            for (int i = 0; i < nFrames; i++)
            {
                float acc = 0;
                for (int ch = 0; ch < numChannels; ch++)
                    acc += DecodeSample(data, i * frameSize + ch * bytesPerSample, audioFormat, bitsPerSample);
                output[i] = Math.Clamp(acc / numChannels, -1f, 1f);
            }
            return (output, sampleRate);
        }

        private static float DecodeSample(byte[] d, int off, int fmt, int bits)
        {
            if (fmt == WaveFormatPcm)
            {
                switch (bits)
                {
                    case 16:
                        return BitConverter.ToInt16(d, off) / 32768f;
                    case 24:
                    {
                        int v = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        return v / 8388608f;
                    }
                    case 32:
                        return BitConverter.ToInt32(d, off) / 2147483648f;
                    default: // 8bit は unsigned
                        return (d[off] - 128) / 128f;
                }
            }
            return bits == 32
                ? BitConverter.ToSingle(d, off)
                : (float)BitConverter.ToDouble(d, off);
        }
    }
}
