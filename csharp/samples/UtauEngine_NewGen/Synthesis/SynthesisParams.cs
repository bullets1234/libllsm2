using System.Collections.Generic;
using UtauEngineNg.Cli;

namespace UtauEngineNg.Synthesis
{
    /// <summary>
    /// 合成に必要なパラメータ一式。UtauEngine の巨大な引数リスト（25個超）を 1 つの
    /// 不変オブジェクトに集約し、トレースと拡張を容易にする。
    /// </summary>
    public sealed class SynthesisParams
    {
        // ピッチ
        public required float SrcF0 { get; init; }
        public required float TargetF0 { get; init; }
        public required List<int> PitchBend { get; init; }
        public required int Tempo { get; init; }

        // タイムストレッチ
        public required int ConsonantFrames { get; init; }
        public required float ConsonantStretch { get; init; }
        public required float StretchRatio { get; init; }
        public required float ThopSeconds { get; init; }
        public float OverlapMs { get; init; }

        // モジュレーション
        public int Modulation { get; init; } = 100;
        public bool UseModPlus { get; init; }

        // エフェクト・フラグ
        public int Breathiness { get; init; } = 50;
        public int GenderFactor { get; init; }
        public int FormantFollow { get; init; } = 100;
        public int SpectralTilt { get; init; }
        public int UnvoicedAttenuation { get; init; }
        public int GrowlStrength { get; init; }
        public int GlottalClosure { get; init; } = 50;
        public bool UseGlottalAutoEstimate { get; init; }

        // 合成オプション
        public bool UseOversampling { get; init; }

        /// <summary>フラグ集合から SynthesisParams を組み立てる。</summary>
        public static SynthesisParams FromFlags(
            FlagSet flags, float srcF0, float targetF0, List<int> pitchBend, int tempo,
            int consonantFrames, float consonantStretch, float stretchRatio, float thopSeconds, float overlapMs,
            int modulation)
        {
            return new SynthesisParams
            {
                SrcF0 = srcF0,
                TargetF0 = targetF0,
                PitchBend = pitchBend,
                Tempo = tempo,
                ConsonantFrames = consonantFrames,
                ConsonantStretch = consonantStretch,
                StretchRatio = stretchRatio,
                ThopSeconds = thopSeconds,
                OverlapMs = overlapMs,
                Modulation = modulation,
                UseModPlus = flags.ModulationPlus,
                Breathiness = flags.Breathiness,
                GenderFactor = flags.GenderFactor,
                FormantFollow = flags.FormantFollow,
                SpectralTilt = flags.SpectralTilt,
                UnvoicedAttenuation = flags.UnvoicedAttenuation,
                GrowlStrength = flags.GrowlStrength,
                GlottalClosure = flags.GlottalClosure,
                UseGlottalAutoEstimate = flags.GlottalAutoEstimate,
                UseOversampling = flags.Oversampling,
            };
        }
    }
}
