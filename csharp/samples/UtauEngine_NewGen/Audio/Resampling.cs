using System;

namespace UtauEngineNg.Audio
{
    /// <summary>
    /// 波形・配列のリサンプリング DSP。
    /// 解析前の 2x オーバーサンプリングや合成後のダウンサンプル、
    /// 配列の任意比率リサンプル（Catmull-Rom）を提供する。
    /// （UtauEngine の Upsample2x / Downsample / ResampleArray / CreateLowpassFIR を移植）
    /// </summary>
    public static class Resampling
    {
        /// <summary>
        /// 2x アップサンプリング（ゼロ挿入 + 129タップ Blackman 窓 sinc ローパス）。
        /// CZT 調波解析の精度向上のため、解析前に常時適用される。
        /// </summary>
        public static float[] Upsample2x(float[] input)
        {
            int outputLength = input.Length * 2;
            const int filterLength = 129;
            float[] fir = CreateLowpassFir(filterLength, 0.5f);
            int groupDelay = filterLength / 2;

            var output = new float[outputLength];
            for (int i = 0; i < outputLength; i++)
            {
                float sum = 0;
                for (int j = 0; j < filterLength; j++)
                {
                    int srcIdx = i - groupDelay + j;
                    if (srcIdx >= 0 && srcIdx < outputLength && (srcIdx & 1) == 0)
                    {
                        int origIdx = srcIdx / 2;
                        if (origIdx >= 0 && origIdx < input.Length)
                            sum += input[origIdx] * fir[j] * 2.0f; // ゼロ挿入ゲイン補正
                    }
                }
                output[i] = sum;
            }
            return output;
        }

        /// <summary>
        /// 整数倍ダウンサンプリング（windowed-sinc ローパス + 群遅延補正 + ミラー境界）。
        /// </summary>
        public static float[] Downsample(float[] input, int factor)
        {
            if (factor <= 1) return input;

            int outputLength = input.Length / factor;
            var output = new float[outputLength];

            int filterLength = factor * 16 + 1;
            float[] fir = CreateLowpassFir(filterLength, 0.95f / factor);
            int groupDelay = filterLength / 2;

            for (int i = 0; i < outputLength; i++)
            {
                int centerIdx = i * factor;
                float sum = 0, weightSum = 0;
                for (int j = 0; j < filterLength; j++)
                {
                    int idx = centerIdx - groupDelay + j;
                    int mirrored = idx;
                    if (mirrored < 0) mirrored = -mirrored;
                    else if (mirrored >= input.Length) mirrored = 2 * input.Length - mirrored - 2;
                    mirrored = Math.Clamp(mirrored, 0, input.Length - 1);

                    sum += input[mirrored] * fir[j];
                    weightSum += fir[j];
                }
                output[i] = weightSum > 0 ? sum / weightSum : sum;
            }
            return output;
        }

        /// <summary>
        /// 任意比率で配列をリサンプル（出力長は元と同じ、Catmull-Rom 補間）。
        /// ピッチベンドや包絡など滑らかさが重要な系列に用いる。
        /// </summary>
        public static float[] ResampleArray(float[] source, float ratio)
        {
            var result = new float[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                float srcIdx = i / ratio;
                if (srcIdx <= 0)
                {
                    result[i] = source[0];
                }
                else if (srcIdx >= source.Length - 1)
                {
                    result[i] = source[^1];
                }
                else
                {
                    int idx1 = (int)srcIdx;
                    int idx0 = Math.Max(0, idx1 - 1);
                    int idx2 = Math.Min(source.Length - 1, idx1 + 1);
                    int idx3 = Math.Min(source.Length - 1, idx1 + 2);

                    float t = srcIdx - idx1;
                    float t2 = t * t;
                    float t3 = t2 * t;
                    float v0 = source[idx0], v1 = source[idx1], v2 = source[idx2], v3 = source[idx3];

                    result[i] = 0.5f * (
                        2 * v1 +
                        (-v0 + v2) * t +
                        (2 * v0 - 5 * v1 + 4 * v2 - v3) * t2 +
                        (-v0 + 3 * v1 - 3 * v2 + v3) * t3);
                }
            }
            return result;
        }

        /// <summary>Windowed-sinc ローパス FIR 係数（Blackman 窓、ゲイン1正規化）。</summary>
        public static float[] CreateLowpassFir(int length, float cutoffRatio)
        {
            var coeffs = new float[length];
            int center = length / 2;
            float sum = 0;

            for (int i = 0; i < length; i++)
            {
                int n = i - center;
                float sinc;
                if (n == 0)
                {
                    sinc = 1.0f;
                }
                else
                {
                    float x = MathF.PI * cutoffRatio * n;
                    sinc = MathF.Sin(x) / x;
                }

                float t = 2.0f * MathF.PI * i / (length - 1);
                float window = 0.42f - 0.5f * MathF.Cos(t) + 0.08f * MathF.Cos(2.0f * t);

                coeffs[i] = sinc * window * cutoffRatio;
                sum += coeffs[i];
            }

            for (int i = 0; i < length; i++)
                coeffs[i] /= sum;

            return coeffs;
        }
    }
}
