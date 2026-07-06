using System;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Synthesis
{
    /// <summary>
    /// 子音原音ブレンド（C フラグ）。LLSM 再合成で劣化しやすい子音部を原音 PCM で補強する。
    /// 分解出力（正弦波/ノイズ分離）が利用可能なら無声フレームのノイズ成分のみ置換し、
    /// 不可なら従来 PCM ブレンドにフォールバックする。母音の正弦波成分はピッチシフト済みを維持。
    /// （UtauEngine の C フラグブロックの移植）
    /// </summary>
    public static class ConsonantBlend
    {
        private const string Stage = "ConsonantBlend";

        /// <summary>
        /// <paramref name="output"/> の子音部へ原音ブレンドを適用する（in-place）。
        /// </summary>
        public static void Apply(
            float[] output, float[] segment, float[] f0, int fs,
            int consonantSamples, int consonantFrames, float consonantStretch, float thopSeconds,
            int blendStrength, float[]? synthSin, float[]? synthNoise, ILogger log)
        {
            if (blendStrength <= 0 || consonantSamples <= 0 || output.Length == 0) return;

            int nhop = (int)(thopSeconds * fs);
            int dstConsonantFrames = (int)(consonantFrames * consonantStretch);
            int dstConsonantSamples = dstConsonantFrames * nhop;
            if (dstConsonantSamples <= 0 || dstConsonantSamples >= output.Length) return;

            float blendRatio = blendStrength / 100.0f;

            int srcConsonantLen = Math.Min(consonantSamples, segment.Length);
            float[] originalConsonant = new float[srcConsonantLen];
            Array.Copy(segment, 0, originalConsonant, 0, srcConsonantLen);

            // velocity 適用（時間伸縮、線形補間）
            float[] adjustedConsonant;
            if (Math.Abs(consonantStretch - 1.0f) > 0.01f)
            {
                adjustedConsonant = new float[dstConsonantSamples];
                for (int i = 0; i < dstConsonantSamples; i++)
                {
                    float srcPos = (float)i / dstConsonantSamples * srcConsonantLen;
                    int idx = (int)srcPos;
                    float frac = srcPos - idx;
                    if (idx + 1 < srcConsonantLen)
                        adjustedConsonant[i] = originalConsonant[idx] * (1 - frac) + originalConsonant[idx + 1] * frac;
                    else if (idx < srcConsonantLen)
                        adjustedConsonant[i] = originalConsonant[idx];
                }
            }
            else
            {
                adjustedConsonant = originalConsonant;
                dstConsonantSamples = srcConsonantLen;
            }

            int blendEnd = Math.Min(dstConsonantSamples, output.Length);
            float crossfadeMs = Math.Max(5.0f, 15.0f * consonantStretch);
            int crossfadeSamples = (int)(crossfadeMs / 1000.0f * fs);
            crossfadeSamples = Math.Min(crossfadeSamples, blendEnd / 2);
            int crossfadeStart = Math.Max(0, blendEnd - crossfadeSamples);

            bool hasDecomposed = synthSin != null && synthNoise != null &&
                                 synthSin.Length >= blendEnd && synthNoise.Length >= blendEnd;

            if (hasDecomposed)
                BlendDecomposed(output, adjustedConsonant, f0, synthSin!, synthNoise!,
                    blendEnd, dstConsonantSamples, srcConsonantLen, nhop, crossfadeStart, crossfadeSamples, blendRatio);
            else
                BlendPcm(output, adjustedConsonant, f0,
                    blendEnd, dstConsonantSamples, srcConsonantLen, nhop, crossfadeStart, crossfadeSamples, blendRatio);

            log.Info(Stage, $"C{blendStrength}: {(hasDecomposed ? "decomposed noise-only" : "PCM fallback")}, {blendEnd} samples ({blendEnd / (float)fs * 1000:F1}ms)");
        }

        private static void BlendDecomposed(
            float[] output, float[] adjustedConsonant, float[] f0, float[] synthSin, float[] synthNoise,
            int blendEnd, int dstConsonantSamples, int srcConsonantLen, int nhop,
            int crossfadeStart, int crossfadeSamples, float blendRatio)
        {
            float origRms = 0, noiseRms = 0;
            for (int i = 0; i < blendEnd; i++)
            {
                float o = i < adjustedConsonant.Length ? adjustedConsonant[i] : 0;
                origRms += o * o;
                noiseRms += synthNoise[i] * synthNoise[i];
            }
            origRms = MathF.Sqrt(origRms / blendEnd);
            noiseRms = MathF.Sqrt(noiseRms / blendEnd);
            float volumeMatch = Math.Clamp(origRms > 0.001f ? noiseRms / origRms : 1.0f, 0.1f, 5.0f);

            for (int i = 0; i < blendEnd; i++)
            {
                if (i >= adjustedConsonant.Length) break;
                if (!IsUnvoiced(i, dstConsonantSamples, srcConsonantLen, nhop, f0)) continue;

                float localBlend = blendRatio;
                if (i >= crossfadeStart && crossfadeSamples > 0)
                {
                    float t = (float)(i - crossfadeStart) / crossfadeSamples;
                    localBlend *= 1.0f - 0.5f * (1.0f - MathF.Cos(MathF.PI * t));
                }
                float origNoise = adjustedConsonant[i] * volumeMatch;
                float blendedNoise = synthNoise[i] * (1.0f - localBlend) + origNoise * localBlend;
                output[i] = synthSin[i] + blendedNoise;
            }
        }

        private static void BlendPcm(
            float[] output, float[] adjustedConsonant, float[] f0,
            int blendEnd, int dstConsonantSamples, int srcConsonantLen, int nhop,
            int crossfadeStart, int crossfadeSamples, float blendRatio)
        {
            float origRms = 0, llsmRms = 0;
            for (int i = 0; i < blendEnd; i++)
            {
                float o = i < adjustedConsonant.Length ? adjustedConsonant[i] : 0;
                origRms += o * o;
                llsmRms += output[i] * output[i];
            }
            origRms = MathF.Sqrt(origRms / blendEnd);
            llsmRms = MathF.Sqrt(llsmRms / blendEnd);
            float volumeMatch = Math.Clamp(origRms > 0.001f ? llsmRms / origRms : 1.0f, 0.3f, 3.0f);

            for (int i = 0; i < blendEnd; i++)
            {
                if (i >= adjustedConsonant.Length) break;
                float origSample = adjustedConsonant[i] * volumeMatch;
                float localBlend = IsUnvoiced(i, dstConsonantSamples, srcConsonantLen, nhop, f0) ? blendRatio : 0.0f;
                if (i >= crossfadeStart && crossfadeSamples > 0)
                {
                    float t = (float)(i - crossfadeStart) / crossfadeSamples;
                    localBlend *= 1.0f - 0.5f * (1.0f - MathF.Cos(MathF.PI * t));
                }
                output[i] = output[i] * (1.0f - localBlend) + origSample * localBlend;
            }
        }

        private static bool IsUnvoiced(int i, int dstConsonantSamples, int srcConsonantLen, int nhop, float[] f0)
        {
            float srcSamplePos = dstConsonantSamples > 0 ? (float)i / dstConsonantSamples * srcConsonantLen : i;
            int frameIdx = Math.Clamp((int)(srcSamplePos / nhop), 0, f0.Length - 1);
            return f0[frameIdx] <= 0;
        }
    }
}
