using System.Collections.Generic;
using UtauEngineNg.Cli;
using UtauEngineNg.Pitch;

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
        public required float Tempo { get; init; }

        // タイムストレッチ
        public required int ConsonantFrames { get; init; }
        public required float ConsonantStretch { get; init; }
        public required float StretchRatio { get; init; }
        public required float ThopSeconds { get; init; }
        public float OverlapMs { get; init; }

        // モジュレーション（UTAU 既定 0 = フラットピッチ。ResamplerArgs と統一）
        public int Modulation { get; init; }

        // マイクロプロソディ（原音由来ジッター/シマー、J フラグで深度制御。既定オフ）
        public MicroProsodyData Micro { get; init; } = MicroProsodyData.Empty;
        public int JitterDepth { get; init; }
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
        /// <summary>Q: 動的声質カーブ強度（0=オフ）。</summary>
        public int VoiceQuality { get; init; }

        // N フラグ（診断用の機能無効化）
        public bool DisableVsphseExtension { get; init; }
        public bool DisableVsphseSmoother { get; init; }
        public bool DisableResidualCorrection { get; init; }
        public bool DisableNoiseTexture { get; init; }
        public bool DisableEdgePad { get; init; }
        public bool DisableResidualExcitation { get; init; }
        /// <summary>p: 残差励振の PSOLA 再配置を有効化（試験）。</summary>
        public bool PsolaExcitation { get; init; }
        /// <summary>r: 残差励振を全フレームに（既定は無声のみ）。</summary>
        public bool ResidualFull { get; init; }
        public bool ResidualUnvoiced { get; init; }
        public bool TextureTransfer { get; init; }
        public bool SmootherAndResidualCorrection { get; init; }

        // 合成オプション
        public bool UseOversampling { get; init; }

        /// <summary>フラグ集合から SynthesisParams を組み立てる。</summary>
        public static SynthesisParams FromFlags(
            FlagSet flags, float srcF0, float targetF0, List<int> pitchBend, float tempo,
            int consonantFrames, float consonantStretch, float stretchRatio, float thopSeconds, float overlapMs,
            int modulation, MicroProsodyData? micro = null)
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
                Micro = micro ?? MicroProsodyData.Empty,
                JitterDepth = flags.Jitter,
                UseModPlus = flags.ModulationPlus,
                Breathiness = flags.Breathiness,
                GenderFactor = flags.GenderFactor,
                FormantFollow = flags.FormantFollow,
                SpectralTilt = flags.SpectralTilt,
                UnvoicedAttenuation = flags.UnvoicedAttenuation,
                GrowlStrength = flags.GrowlStrength,
                GlottalClosure = flags.GlottalClosure,
                UseGlottalAutoEstimate = flags.GlottalAutoEstimate,
                VoiceQuality = flags.VoiceQuality,
                DisableVsphseExtension = flags.DisableVsphseExtension,
                DisableVsphseSmoother = flags.DisableVsphseSmoother,
                DisableResidualCorrection = flags.DisableResidualCorrection,
                DisableNoiseTexture = flags.DisableNoiseTexture,
                DisableEdgePad = flags.DisableEdgePad,
                DisableResidualExcitation = flags.DisableResidualExcitation,
                PsolaExcitation = flags.PsolaExcitation,
                ResidualFull = flags.ResidualFull,
                ResidualUnvoiced = flags.ResidualUnvoiced,
                TextureTransfer = flags.TextureTransfer,
                SmootherAndResidualCorrection = flags.SmootherAndResidualCorrection,
                UseOversampling = flags.Oversampling,
            };
        }
    }
}
