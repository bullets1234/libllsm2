using System;
using System.Collections.Generic;

namespace UtauEngineNg.Pitch
{
    /// <summary>
    /// UTAU ピッチベンド（cent 列）を時間軸グリッドへ補間する。
    /// Akima 傾き + Hermite 補間でオーバーシュートを抑える。
    /// （UtauEngine の InterpolatePitchBend / UnwrapPitchBend を移植）
    /// </summary>
    public static class PitchBendGrid
    {
        /// <summary>
        /// UTAU の等間隔時刻列 <paramref name="utauT"/> に対応する cent 値 <paramref name="utauPb"/> を、
        /// 出力時刻列 <paramref name="outputT"/> へ Akima-Hermite 補間する。
        /// </summary>
        public static float[] Interpolate(float[] utauT, float[] outputT, float[] utauPb)
        {
            if (utauT.Length < 2 || outputT.Length == 0)
                return new float[outputT.Length];

            int n = utauT.Length;
            float span = utauT[1] - utauT[0];

            // 差分
            var m = new float[n - 1];
            for (int i = 0; i < n - 1; i++)
                m[i] = (utauPb[i + 1] - utauPb[i]) / span;

            // 各点の傾き（Akima 重み付け）
            var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (i == 0)
                {
                    s[i] = m[0];
                }
                else if (i == 1)
                {
                    float mMinus1 = 2f * m[0] - m[1];
                    float w1 = MathF.Abs(m[1] - m[0]);
                    float w2 = MathF.Abs(m[0] - mMinus1);
                    s[i] = (w1 + w2 > 1e-10f) ? (w1 * m[0] + w2 * m[1]) / (w1 + w2) : (m[0] + m[1]) * 0.5f;
                }
                else if (i >= n - 2)
                {
                    if (i == n - 1)
                    {
                        s[i] = m[n - 2];
                    }
                    else
                    {
                        float mPlus1 = 2f * m[n - 2] - m[n - 3];
                        float w1 = MathF.Abs(mPlus1 - m[i]);
                        float w2 = MathF.Abs(m[i] - m[i - 1]);
                        s[i] = (w1 + w2 > 1e-10f) ? (w1 * m[i - 1] + w2 * m[i]) / (w1 + w2) : (m[i - 1] + m[i]) * 0.5f;
                    }
                }
                else
                {
                    float w1 = MathF.Abs(m[i + 1] - m[i]);
                    float w2 = MathF.Abs(m[i - 1] - m[i - 2]);
                    s[i] = (w1 + w2 > 1e-10f) ? (w1 * m[i - 1] + w2 * m[i]) / (w1 + w2) : (m[i - 1] + m[i]) * 0.5f;
                }
            }

            var result = new float[outputT.Length];
            for (int i = 0; i < outputT.Length; i++)
            {
                float t = outputT[i];
                int index = (int)((double)(t - utauT[0]) / span);
                if (index < 0) index = 0;
                if (index >= n - 1) index = n - 2;

                float t0 = utauT[index];
                float t1 = utauT[index + 1];
                float h = t1 - t0;
                float u = (t - t0) / h;
                float u2 = u * u;
                float u3 = u2 * u;

                float h00 = 2f * u3 - 3f * u2 + 1f;
                float h10 = u3 - 2f * u2 + u;
                float h01 = -2f * u3 + 3f * u2;
                float h11 = u3 - u2;

                result[i] = h00 * utauPb[index] + h10 * h * s[index]
                          + h01 * utauPb[index + 1] + h11 * h * s[index + 1];
            }

            return result;
        }

        /// <summary>
        /// 4096 境界をまたぐ急変（ラップアラウンド）を連続値に補正する。
        /// </summary>
        public static List<int> Unwrap(IReadOnlyList<int> pitchBend)
        {
            var result = new List<int>(pitchBend.Count);
            if (pitchBend.Count == 0) return result;

            result.Add(pitchBend[0]);
            int offset = 0;
            for (int i = 1; i < pitchBend.Count; i++)
            {
                int diff = pitchBend[i] - pitchBend[i - 1];
                if (diff > 2048) offset -= 4096;
                else if (diff < -2048) offset += 4096;
                result.Add(pitchBend[i] + offset);
            }
            return result;
        }
    }
}
