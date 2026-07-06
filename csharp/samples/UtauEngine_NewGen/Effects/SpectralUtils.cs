using System;
using System.Collections.Generic;
using System.Linq;

namespace UtauEngineNg.Effects
{
    /// <summary>エフェクト共通のスペクトル解析ヘルパ。</summary>
    public static class SpectralUtils
    {
        /// <summary>
        /// VTMAGN（対数振幅包絡）からフォルマントピークを検出する。
        /// 200Hz〜5000Hz、-40dB 以上の局所極大、上位 5 本（F1〜F5 相当）に制限。
        /// </summary>
        public static List<(int index, float magnitude)> DetectFormantPeaks(float[] vtmagn, int fs, int nspec)
        {
            var peaks = new List<(int, float)>();

            int minBin = (int)(200.0f / (fs / 2.0f) * nspec);
            int maxBin = (int)(5000.0f / (fs / 2.0f) * nspec);
            minBin = Math.Max(1, minBin);
            maxBin = Math.Min(nspec - 2, maxBin);

            const float threshold = -40.0f;
            for (int i = minBin; i <= maxBin; i++)
            {
                if (vtmagn[i] > threshold &&
                    vtmagn[i] > vtmagn[i - 1] &&
                    vtmagn[i] > vtmagn[i + 1])
                {
                    peaks.Add((i, vtmagn[i]));
                }
            }

            if (peaks.Count > 5)
                peaks = peaks.OrderByDescending(p => p.Item2).Take(5).ToList();

            return peaks;
        }
    }
}
