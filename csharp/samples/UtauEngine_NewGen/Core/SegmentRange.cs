using System;

namespace UtauEngineNg.Core
{
    /// <summary>
    /// オフセット・カットオフ・子音長から原音の使用区間を算出する。
    /// UTAU リサンプラー仕様: offset=開始(ms)、cutoff&lt;0 は offset から |cutoff| ms の長さ、
    /// cutoff&gt;0 はファイル末尾からの余白(ms)、cutoff=0 は末尾まで。
    /// （UtauEngine の区間切り出しロジックの移植）
    /// </summary>
    public readonly struct SegmentRange
    {
        public int StartSample { get; init; }
        public int TotalLength { get; init; }
        public int OffsetSamples { get; init; }
        public int ConsonantSamples { get; init; }

        public static SegmentRange Compute(float[] samples, int fs, float offsetMs, float cutoffMs, float consonantMs)
        {
            int offsetSamples = (int)(offsetMs / 1000.0 * fs);
            int endSamples;
            if (cutoffMs < 0)
                endSamples = offsetSamples + (int)(Math.Abs(cutoffMs) / 1000.0 * fs);
            else if (cutoffMs > 0)
                endSamples = samples.Length - (int)(cutoffMs / 1000.0 * fs);
            else
                endSamples = samples.Length;

            int startSample = Math.Max(0, offsetSamples);
            int totalLength = Math.Max(0, Math.Min(endSamples - startSample, samples.Length - startSample));
            int consonantSamples = Math.Min((int)(consonantMs / 1000.0 * fs), totalLength);

            return new SegmentRange
            {
                StartSample = startSample,
                TotalLength = totalLength,
                OffsetSamples = offsetSamples,
                ConsonantSamples = consonantSamples,
            };
        }
    }
}
