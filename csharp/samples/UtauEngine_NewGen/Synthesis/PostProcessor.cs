using System;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Synthesis
{
    /// <summary>
    /// 出力後処理：基準レベル正規化 → ボリューム適用 → ピークリミット。
    ///
    /// UTAU 音源は録音レベルがまちまちで、合成チェーン（ピッチ比エネルギー補償等）の
    /// ゲインもノート毎に異なるため、出力の実効音量（無音除外 RMS）を基準レベル
    /// （-16dBFS）へ正規化してノート間・音源間のバランスを揃える。
    /// ゲインは ±12dB でクランプするため、囁き・息・語尾などの極端に静かな素材が
    /// 通常ノートと同じ音量まで爆音化することはない（+12dB 止まり）。
    /// 最後に UTAU ミキサーのオーバーラップ加算でクリップしないよう
    /// -3dB を超えるピークのみ抑える。
    /// </summary>
    public static class PostProcessor
    {
        private const string Stage = "Volume";
        private const float MaxPeak = 0.70f;        // -3dB
        private const float DefaultTargetRmsDb = -16f;
        private const float MaxNormGainDb = 12f;    // 正規化ゲインの安全クランプ ±12dB
        private const int RmsFrameLen = 441;        // 10ms @44.1k 相当（絶対時間でなくてよい）

        /// <summary>
        /// 基準レベル（実効 RMS, dBFS）。UTAU ミックスで音割れする場合は
        /// 環境変数 L2R_TARGET_DB=-18 等でヘッドルームを増やせる。
        /// </summary>
        private static readonly float TargetRmsDb = ResolveTargetDb();

        private static float ResolveTargetDb()
        {
            var s = Environment.GetEnvironmentVariable("L2R_TARGET_DB");
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var v)
                   && v > -40f && v < 0f
                ? v : DefaultTargetRmsDb;
        }

        /// <summary>基準レベル正規化＋ボリューム適用＋ピークリミット（in-place）。</summary>
        public static void Apply(float[] output, int volume, ILogger log)
        {
            if (output.Length == 0) return;

            // 1. 基準レベルへの正規化（±12dB クランプ）
            float outRms = ActiveRms(output);
            if (outRms > 1e-6f)
            {
                float gainDb = Math.Clamp(
                    TargetRmsDb - 20f * MathF.Log10(outRms), -MaxNormGainDb, MaxNormGainDb);
                if (MathF.Abs(gainDb) > 0.1f)
                {
                    float gain = MathF.Pow(10f, gainDb / 20f);
                    for (int i = 0; i < output.Length; i++) output[i] *= gain;
                    log.Debug(Stage, $"Normalize: {20f * MathF.Log10(outRms):F1}dBFS -> " +
                                     $"target {TargetRmsDb:F0}dBFS (gain {gainDb:+0.0;-0.0}dB)");
                }
            }

            // 2. volume 引数
            if (volume != 100)
            {
                float volScale = volume / 100.0f;
                for (int i = 0; i < output.Length; i++) output[i] *= volScale;
            }

            // 3. ピークリミッタ
            float peak = 0f;
            for (int i = 0; i < output.Length; i++)
            {
                float a = Math.Abs(output[i]);
                if (a > peak) peak = a;
            }

            if (peak > MaxPeak)
            {
                float limiterGain = MaxPeak / peak;
                for (int i = 0; i < output.Length; i++) output[i] *= limiterGain;
                log.Debug(Stage, $"Peak {peak:F3} too loud -> {MaxPeak:F3} (gain {limiterGain:F3})");
            }
            else
            {
                log.Debug(Stage, $"Peak {peak:F3} within range, no adjustment");
            }
        }

        /// <summary>
        /// 無音を除外した実効 RMS。10ms フレームの RMS を取り、最大フレームの
        /// -20dB 以上のフレームのみで平均する（囁き素材でも相対閾値なので有効音が残る）。
        /// タイムストレッチで無音・子音比率が変わっても「鳴っている部分」同士で
        /// 比較できるようにするための処理。
        /// </summary>
        private static float ActiveRms(float[] x)
        {
            if (x.Length == 0) return 0f;
            int nFrames = (x.Length + RmsFrameLen - 1) / RmsFrameLen;
            var frameMs = new double[nFrames];
            double maxMs = 0;
            for (int f = 0; f < nFrames; f++)
            {
                int lo = f * RmsFrameLen;
                int hi = Math.Min(x.Length, lo + RmsFrameLen);
                double e = 0;
                for (int i = lo; i < hi; i++) e += (double)x[i] * x[i];
                frameMs[f] = e / Math.Max(1, hi - lo);
                if (frameMs[f] > maxMs) maxMs = frameMs[f];
            }
            if (maxMs <= 0) return 0f;

            double threshold = maxMs * 0.01; // パワー比 -20dB
            double sum = 0; int cnt = 0;
            for (int f = 0; f < nFrames; f++)
            {
                if (frameMs[f] < threshold) continue;
                sum += frameMs[f]; cnt++;
            }
            return cnt > 0 ? (float)Math.Sqrt(sum / cnt) : 0f;
        }
    }
}
