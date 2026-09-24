using System;
using System.IO;
using System.Text;

namespace UtauEngineNg.Audio
{
    /// <summary>
    /// UTAU の .frq ファイル（F0キャッシュ）の内容。
    /// ヘッダ "FREQ0003" / フレームあたりサンプル数 / 平均F0 / フレーム毎 (F0, 振幅)。
    /// </summary>
    public sealed class FrqData
    {
        public string Header { get; init; } = "";
        public int SamplesPerFrame { get; init; }
        public double AverageF0 { get; init; }
        public double[] F0Values { get; init; } = Array.Empty<double>();
        public double[] Amplitudes { get; init; } = Array.Empty<double>();

        public bool HasData => F0Values.Length > 0;
    }

    /// <summary>.frq ファイルの読み込み。</summary>
    public static class FrqFile
    {
        /// <summary>
        /// wav パスに対応する .frq の標準パス（"foo.wav" → "foo_wav.frq"）。
        /// </summary>
        public static string PathFor(string wavPath)
        {
            string dir = Path.GetDirectoryName(wavPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(wavPath);
            return Path.Combine(dir, name + "_wav.frq");
        }

        /// <summary>.frq を読む。失敗時は null を返す（例外を投げない）。</summary>
        public static FrqData? TryRead(string path)
        {
            try
            {
                return Read(path);
            }
            catch
            {
                return null;
            }
        }

        public static FrqData Read(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            string header = Encoding.ASCII.GetString(br.ReadBytes(8)); // "FREQ0003"
            if (!header.StartsWith("FREQ", StringComparison.Ordinal))
                throw new InvalidDataException($"Not a FRQ file: header='{header}'");

            int samplesPerFrame = br.ReadInt32();
            double averageF0 = br.ReadDouble();
            br.ReadBytes(16);                              // 予約領域
            int frameCount = br.ReadInt32();

            if (samplesPerFrame <= 0)
                throw new InvalidDataException($"Invalid FRQ: samplesPerFrame={samplesPerFrame}");
            if (frameCount < 0)
                throw new InvalidDataException($"Invalid FRQ: frameCount={frameCount}");

            // 末尾が欠けた破損ファイルは読める分だけに切り詰める
            long remaining = fs.Length - fs.Position;
            int readable = (int)Math.Min(frameCount, remaining / 16);
            frameCount = readable;

            var f0 = new double[frameCount];
            var ampl = new double[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                f0[i] = br.ReadDouble();
                ampl[i] = br.ReadDouble();
            }

            return new FrqData
            {
                Header = header,
                SamplesPerFrame = samplesPerFrame,
                AverageF0 = averageF0,
                F0Values = f0,
                Amplitudes = ampl,
            };
        }
    }
}
