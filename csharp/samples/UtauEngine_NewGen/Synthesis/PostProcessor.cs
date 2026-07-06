using System;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Synthesis
{
    /// <summary>
    /// 出力後処理：ボリューム適用とピーク正規化。UTAU ミキサーのオーバーラップ加算で
    /// クリップしないよう、ノート単体で -12dB〜-3dB の範囲へ収める。
    /// （UtauEngine の出力ボリューム・ピーク調整ブロックの移植）
    /// </summary>
    public static class PostProcessor
    {
        private const string Stage = "Volume";
        private const float MinPeak = 0.25f;  // -12dB
        private const float MaxPeak = 0.70f;  // -3dB

        /// <summary>ボリューム適用＋ピーク正規化（in-place）。</summary>
        public static void Apply(float[] output, int volume, ILogger log)
        {
            if (output.Length == 0) return;

            if (volume != 100)
            {
                float volScale = volume / 100.0f;
                for (int i = 0; i < output.Length; i++) output[i] *= volScale;
            }

            float peak = 0f;
            for (int i = 0; i < output.Length; i++)
            {
                float a = Math.Abs(output[i]);
                if (a > peak) peak = a;
            }

            if (peak > MaxPeak)
            {
                float gain = MaxPeak / peak;
                for (int i = 0; i < output.Length; i++) output[i] *= gain;
                log.Debug(Stage, $"Peak {peak:F3} too loud -> {MaxPeak:F3} (gain {gain:F3})");
            }
            else if (peak < MinPeak && peak > 0.0f)
            {
                float gain = MinPeak / peak;
                for (int i = 0; i < output.Length; i++) output[i] *= gain;
                log.Debug(Stage, $"Peak {peak:F3} too quiet -> {MinPeak:F3} (gain {gain:F3})");
            }
            else
            {
                log.Debug(Stage, $"Peak {peak:F3} within range, no adjustment");
            }
        }
    }
}
