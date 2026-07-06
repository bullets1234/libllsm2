using System;
using System.Collections.Generic;
using UtauEngineNg.Pitch;

namespace UtauEngineNg.Synthesis
{
    /// <summary>
    /// ピッチベンドの出力フレーム軸への展開と、M+ 用の動的モジュレーション包絡を計算する。
    /// （UtauEngine の PB 補間ブロック + GetDynamicModulation の移植）
    /// </summary>
    public static class PitchBendTimeline
    {
        /// <summary>
        /// アンラップ済みピッチベンド（cents）を出力フレーム数 <paramref name="dstNfrm"/> に補間する。
        /// UTAU のピッチ時間軸（96 分音符単位 = 60/96/bpm 秒）を用いる。
        /// </summary>
        public static float[] Interpolate(IReadOnlyList<int> unwrappedPb, int dstNfrm, int tempo, float thopSeconds)
        {
            if (unwrappedPb.Count == 0) return Array.Empty<float>();

            float outputDurationMs = dstNfrm * thopSeconds * 1000f;
            float utauPbIntervalMs = 60.0f / 96.0f / tempo * 1000f;
            int utauPbLength = (int)(outputDurationMs / utauPbIntervalMs) + 1;

            float[] paddedPb = new float[utauPbLength];
            for (int j = 0; j < utauPbLength; j++)
                paddedPb[j] = j < unwrappedPb.Count ? unwrappedPb[j] : 0f;

            float[] utauT = new float[utauPbLength];
            double intervalD = utauPbIntervalMs;
            for (int j = 0; j < utauPbLength; j++)
                utauT[j] = (float)(j * intervalD);

            float[] outputT = new float[dstNfrm];
            double thopMsD = (double)thopSeconds * 1000.0;
            for (int j = 0; j < dstNfrm; j++)
                outputT[j] = (float)(j * thopMsD);

            return PitchBendGrid.Interpolate(utauT, outputT, paddedPb);
        }

        /// <summary>
        /// M+ フラグ用：ノート境界（先頭・末尾）でモジュレーションをフェードした値を返す。
        /// </summary>
        public static float DynamicModulation(int frameIndex, int totalFrames, int baseModulation, float thopSeconds, float overlapMs)
        {
            if (baseModulation == 0) return 0;

            float fadeTimeMs = overlapMs > 0 ? overlapMs : 80.0f;
            float fadeDurationFrames = fadeTimeMs / (thopSeconds * 1000.0f);
            if (fadeDurationFrames <= 0) return baseModulation;

            float fadeIn = frameIndex < fadeDurationFrames ? frameIndex / fadeDurationFrames : 1.0f;
            float fadeOut = frameIndex >= totalFrames - fadeDurationFrames
                ? (totalFrames - 1 - frameIndex) / fadeDurationFrames
                : 1.0f;

            float fadeRatio = Math.Clamp(Math.Min(fadeIn, fadeOut), 0.0f, 1.0f);
            return baseModulation * fadeRatio;
        }
    }
}
