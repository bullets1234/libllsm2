using System;
using System.Collections.Generic;
using System.Linq;
using LlsmBindings;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// スペクトラルフラックスに基づくトランジェント（子音など急激な変化）検出。
    /// 閾値 = 平均 + 2σ。タイムストレッチ時の子音保護判定に用いる。
    /// （UtauEngine の DetectTransientFrames を移植）
    /// </summary>
    public static class TransientDetector
    {
        /// <summary>各フレームがトランジェントか否かを返す。</summary>
        public static bool[] Detect(IReadOnlyList<ContainerRef> frames)
        {
            int n = frames.Count;
            var isTransient = new bool[n];
            if (n < 2) return isTransient;

            var flux = new float[n];
            for (int i = 1; i < n; i++)
            {
                float[] prev = FrameAccess.ReadVtMagn(frames[i - 1]);
                float[] curr = FrameAccess.ReadVtMagn(frames[i]);
                int minLen = Math.Min(prev.Length, curr.Length);
                if (minLen <= 0) continue;

                float f = 0;
                for (int j = 0; j < minLen; j++)
                {
                    float diff = curr[j] - prev[j];
                    if (diff > 0) f += diff; // 正の差分（エネルギー増加）のみ
                }
                flux[i] = f;
            }

            float mean = flux.Average();
            float variance = 0;
            foreach (float v in flux) variance += (v - mean) * (v - mean);
            variance /= n;
            float threshold = mean + 2.0f * MathF.Sqrt(variance);

            for (int i = 0; i < n; i++)
                isTransient[i] = flux[i] > threshold;

            return isTransient;
        }
    }
}
