using System;
using System.IO;
using System.Text;

namespace UtauEngineNg.Audio
{
    /// <summary>
    /// ニューラル F0 推定器（RMVPE 等）が事前生成する周波数表 "*.frq.l2r" の内容。
    /// UTAU の .frq と同じ「原音に添えるサイドカー」だが、
    /// (1) 有声確信度を持つ、(2) ホップ/レートが可変、(3) 原音との整合性を検証できる、
    /// という点が異なる。
    /// </summary>
    public sealed class L2rF0Data
    {
        /// <summary>推定器が動作したサンプリング周波数 [Hz]（RMVPE なら 16000）。</summary>
        public required int AnalysisRate { get; init; }
        /// <summary>AnalysisRate 上でのフレームホップ [サンプル]（RMVPE なら 160 = 10ms）。</summary>
        public required int HopSamples { get; init; }
        /// <summary>元 WAV のサンプリング周波数 [Hz]（情報用）。</summary>
        public required int SourceRate { get; init; }
        /// <summary>元 WAV のモノラル化後サンプル数（整合性チェック用）。</summary>
        public required long SourceSamples { get; init; }
        /// <summary>元 WAV のモノラル化後サンプル列のハッシュ（整合性チェック用）。</summary>
        public required ulong SourceHash { get; init; }
        /// <summary>推定器の識別子（例 "rmvpe-v1"）。</summary>
        public required string Model { get; init; }
        /// <summary>フレーム毎の F0 [Hz]（無声は 0）。</summary>
        public required float[] F0 { get; init; }
        /// <summary>フレーム毎の有声確信度 [0,1]。</summary>
        public required float[] Confidence { get; init; }
        /// <summary>フレーム毎の RMS 振幅。</summary>
        public required float[] Rms { get; init; }

        public int FrameCount => F0.Length;
        /// <summary>フレーム間隔 [秒]。</summary>
        public double HopSeconds => (double)HopSamples / AnalysisRate;
        public bool HasData => F0.Length > 0;
    }

    /// <summary>
    /// "*.frq.l2r" の読み書き。
    ///
    /// ヘッダ（80 バイト、リトルエンディアン）:
    /// <code>
    ///   0  8  magic  "L2RF0\0\0\0"
    ///   8  4  u32    version (=1)
    ///  12  4  u32    analysisRate
    ///  16  4  u32    hopSamples
    ///  20  4  u32    frameCount
    ///  24  4  u32    sourceRate
    ///  28  4  u32    reserved (=0)
    ///  32  8  i64    sourceSamples
    ///  40  8  u64    sourceHash
    ///  48 32  ascii  model（ゼロ埋め）
    ///  80        f32 f0[n] / f32 conf[n] / f32 rms[n]
    /// </code>
    /// </summary>
    public static class L2rF0File
    {
        /// <summary>".frq" の glob に引っかからないよう、末尾に独自拡張子を足す。</summary>
        public const string Extension = ".frq.l2r";
        public const int Version = 1;
        public const int HeaderSize = 80;
        public const int ModelNameSize = 32;

        private static readonly byte[] Magic = { (byte)'L', (byte)'2', (byte)'R', (byte)'F', (byte)'0', 0, 0, 0 };

        /// <summary>wav パスに対応する表のパス（"foo.wav" → "foo_wav.frq.l2r"）。</summary>
        public static string PathFor(string wavPath)
        {
            string dir = Path.GetDirectoryName(wavPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(wavPath);
            return Path.Combine(dir, name + "_wav" + Extension);
        }

        /// <summary>
        /// モノラル化後サンプル列の FNV-1a 64bit ハッシュ。
        /// 原音が差し替えられた表を掴む事故を防ぐためだけに使う（暗号用途ではない）。
        /// </summary>
        public static ulong ComputeHash(ReadOnlySpan<float> samples)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples);
            foreach (byte b in bytes)
            {
                h ^= b;
                h *= prime;
            }
            return h;
        }

        /// <summary>表を読む。失敗時は null（例外を投げない）。</summary>
        public static L2rF0Data? TryRead(string path)
        {
            try { return Read(path); }
            catch { return null; }
        }

        public static L2rF0Data Read(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            var magic = br.ReadBytes(8);
            if (magic.Length != 8 || !MagicMatches(magic))
                throw new InvalidDataException("Not an L2R F0 table");

            int version = br.ReadInt32();
            if (version != Version)
                throw new InvalidDataException($"Unsupported L2R F0 table version {version}");

            int analysisRate = br.ReadInt32();
            int hopSamples = br.ReadInt32();
            int frameCount = br.ReadInt32();
            int sourceRate = br.ReadInt32();
            br.ReadInt32(); // reserved
            long sourceSamples = br.ReadInt64();
            ulong sourceHash = br.ReadUInt64();
            string model = Encoding.ASCII.GetString(br.ReadBytes(ModelNameSize)).TrimEnd('\0');

            if (analysisRate <= 0 || hopSamples <= 0 || frameCount < 0)
                throw new InvalidDataException("Invalid L2R F0 table header");

            long need = (long)frameCount * 3 * sizeof(float);
            if (fs.Length - fs.Position < need)
                throw new InvalidDataException("Truncated L2R F0 table");

            var f0 = ReadFloats(br, frameCount);
            var conf = ReadFloats(br, frameCount);
            var rms = ReadFloats(br, frameCount);

            return new L2rF0Data
            {
                AnalysisRate = analysisRate,
                HopSamples = hopSamples,
                SourceRate = sourceRate,
                SourceSamples = sourceSamples,
                SourceHash = sourceHash,
                Model = model,
                F0 = f0,
                Confidence = conf,
                Rms = rms,
            };
        }

        public static void Write(string path, L2rF0Data data)
        {
            int n = data.FrameCount;
            if (data.Confidence.Length != n || data.Rms.Length != n)
                throw new ArgumentException("F0/Confidence/Rms length mismatch", nameof(data));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");

            // 途中で落ちた場合に壊れた表を残さないよう、一時ファイル経由で差し替える
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write(data.AnalysisRate);
                bw.Write(data.HopSamples);
                bw.Write(n);
                bw.Write(data.SourceRate);
                bw.Write(0); // reserved
                bw.Write(data.SourceSamples);
                bw.Write(data.SourceHash);

                var name = new byte[ModelNameSize];
                var ascii = Encoding.ASCII.GetBytes(data.Model ?? "");
                Array.Copy(ascii, name, Math.Min(ascii.Length, ModelNameSize - 1));
                bw.Write(name);

                WriteFloats(bw, data.F0);
                WriteFloats(bw, data.Confidence);
                WriteFloats(bw, data.Rms);
            }
            File.Move(tmp, path, overwrite: true);
        }

        private static bool MagicMatches(byte[] m)
        {
            for (int i = 0; i < Magic.Length; i++)
                if (m[i] != Magic[i]) return false;
            return true;
        }

        private static float[] ReadFloats(BinaryReader br, int count)
        {
            var a = new float[count];
            var bytes = br.ReadBytes(count * sizeof(float));
            Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
            return a;
        }

        private static void WriteFloats(BinaryWriter bw, float[] a)
        {
            var bytes = new byte[a.Length * sizeof(float)];
            Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
            bw.Write(bytes);
        }
    }
}
