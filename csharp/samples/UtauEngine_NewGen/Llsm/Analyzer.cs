using System;
using System.Linq;
using LlsmBindings;
using UtauEngineNg.Audio;
using UtauEngineNg.Cli;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>LLSM 解析の結果（チャンク・実 Thop・フレーム数）。</summary>
    public sealed class AnalysisResult
    {
        public required ChunkHandle Chunk { get; init; }
        public required float ThopSeconds { get; init; }
        public required int NumFrames { get; init; }
        public required int AnalysisFs { get; init; }
    }

    /// <summary>
    /// セグメントを LLSM Layer1 へ解析する。常時 2x オーバーサンプリングで CZT 調波推定と
    /// フォルマント分解能を高め、解析直後に eenv 変調深度クランプを適用する。
    /// 検証済み定数（maxnhar=800, maxnhar_e=24, npsd=512, 5chノイズ境界{2k,4k,8k,12k}）を保持。
    /// （UtauEngine の解析オプション構築〜Llsm.Analyze〜ClampEenv を再構成）
    /// </summary>
    public sealed class Analyzer
    {
        private const string Stage = "Analyze";

        // 検証済み標準定数
        private const int DefaultMaxnhar = 800;
        private const int DefaultMaxnharE = 24;
        private const int Npsd = 512;
        private const int HmMethodCzt = 1;
        private const float DefaultRelWinsize = 4.0f;

        // 1kHz 境界は Chebyshev 不安定のため使わない（2x解析でクリック源）
        private static readonly float[] NoiseChannelFreq = { 2000f, 4000f, 8000f, 12000f };

        private readonly DiagnosticsContext _diag;

        public Analyzer(DiagnosticsContext diag) => _diag = diag;

        /// <summary>
        /// セグメントを解析する。<paramref name="f0"/> は解析フレーム間隔(thopSec)に一致した F0 列。
        /// </summary>
        public AnalysisResult Analyze(float[] segment, int fs, float[] f0, float thopSec, float srcF0, FlagSet flags)
        {
            var log = _diag.Log;
            using var aopt = LlsmBindings.Llsm.CreateAnalysisOptions();

            (int maxnhar, int maxnharE) = ResolveHarmonics(srcF0, flags, log);

            // ノイズ帯域 5ch（境界4本 + 1）
            NativeLLSM.llsm_aoptions_set_chanfreq(
                aopt.DangerousGetHandle(), NoiseChannelFreq, NoiseChannelFreq.Length + 1);
            log.Debug(Stage, $"Noise {NoiseChannelFreq.Length + 1} channels (2k/4k/8k/12k Hz)");

            float relWinsize = ResolveWindow(f0, flags, log);

            unsafe
            {
                var p = (NativeLLSM.llsm_aoptions*)aopt.DangerousGetHandle().ToPointer();
                p->thop = thopSec;            // f0 フレーム間隔(nhop)と一致必須
                p->npsd = Npsd;
                p->maxnhar = maxnhar;
                p->maxnhar_e = maxnharE;
                p->hm_method = HmMethodCzt;
                p->f0_refine = 1;
                p->rel_winsize = relWinsize;
            }

            // 2x オーバーサンプリング解析
            int analysisFs = fs * 2;
            float[] upsampled = Resampling.Upsample2x(segment);
            log.Debug(Stage, $"Oversampled analysis {fs}Hz -> {analysisFs}Hz ({segment.Length} -> {upsampled.Length} samples)");

            ChunkHandle chunk;
            using (_diag.Profiler.Measure("llsm_analyze"))
                chunk = LlsmBindings.Llsm.Analyze(aopt, upsampled, analysisFs, f0, f0.Length);

            // eenv 変調深度クランプ（子音過渡の誤フィット抑制）
            // N8 フラグ / L2R_EENVCLAMP=0 で無効化可能（切り分け用）
            if (Environment.GetEnvironmentVariable("L2R_EENVCLAMP") != "0" && !flags.DisableEenvClamp)
            {
                var nmProc = new NoiseModelProcessor(log);
                nmProc.ClampEenvModulationDepth(chunk, f0, f0.Length);
            }

            // 倍音振幅の原音直接再推定補正（実験的・既定OFF）: 等倍・同時刻比較の実測で
            // llsm_analyze のHM振幅は原音と±1dB以内で一致することが確定したため不要。
            // 過去に疑われた高域帯域欠損は時間圧縮比較と測定スクリプトのdB平均による
            // アーティファクトだった。L2R_HARMFIX=1 の明示指定時のみ有効。
            if (Environment.GetEnvironmentVariable("L2R_HARMFIX") == "1")
                HarmonicRefinementCorrector.Apply(chunk, f0.Length, upsampled, analysisFs, thopSec, log);

            // 帯域エネルギー較正（実験的・既定OFF）: NM PSDブーストは帯域エネルギー数値こそ
            // 原音に近づくが、倍音をノイズで置き換えるため聴感はホワイトノイズ化する（実声で確認）。
            // L2R_BANDCAL=1 の明示指定時のみ有効。
            if (Environment.GetEnvironmentVariable("L2R_BANDCAL") == "1")
                BandEnergyCalibrator.Apply(chunk, f0.Length, segment, fs, thopSec, log);

            var conf = LlsmBindings.Llsm.GetConf(chunk);
            float actualThop = LlsmBindings.Llsm.GetThopSeconds(conf);
            int nfrm = LlsmBindings.Llsm.GetNumFrames(chunk);

            log.Info(Stage, $"Analyzed {nfrm} frames (thop={actualThop * 1000f:F2}ms, maxnhar={maxnhar}, maxnhar_e={maxnharE})");

            return new AnalysisResult
            {
                Chunk = chunk,
                ThopSeconds = actualThop,
                NumFrames = nfrm,
                AnalysisFs = analysisFs,
            };
        }

        /// <summary>maxnhar / maxnhar_e を決める（H フラグ時は F0 依存の動的調整）。</summary>
        private static (int maxnhar, int maxnharE) ResolveHarmonics(float srcF0, FlagSet flags, ILogger log)
        {
            if (!flags.HighResolution || srcF0 < 1f)
                return (DefaultMaxnhar, DefaultMaxnharE);

            const float maxFreq = 21000.0f; // ナイキスト22.05kの95%
            int maxnhar = Math.Clamp((int)Math.Ceiling(maxFreq / srcF0), 100, 2000);

            float ratio = srcF0 < 120f ? 0.12f
                        : srcF0 < 200f ? 0.08f
                        : srcF0 < 350f ? 0.05f
                                       : 0.03f;
            int maxnharE = Math.Clamp((int)(maxnhar * ratio), 12, 32);

            log.Info(Stage, $"High-Res dynamic harmonics: maxnhar={maxnhar}, maxnhar_e={maxnharE} (F0={srcF0:F1}Hz)");
            return (maxnhar, maxnharE);
        }

        /// <summary>rel_winsize を決める（W フラグ時は F0 統計から適応調整）。</summary>
        private static float ResolveWindow(float[] f0, FlagSet flags, ILogger log)
        {
            if (!flags.AdaptiveWindow) return DefaultRelWinsize;

            var valid = f0.Where(x => x > 0).ToArray();
            if (valid.Length == 0) return DefaultRelWinsize;

            float avg = valid.Average();
            float min = valid.Min();
            float max = valid.Max();
            float refF0 = (max - min > 200) ? (min + max) / 2 : avg;

            float logF0 = MathF.Log(refF0 / 200.0f);
            float rel = Math.Clamp(4.0f * MathF.Exp(-0.2f * logF0), 2.5f, 6.0f);

            log.Info(Stage, $"Adaptive window: F0={refF0:F1}Hz -> rel_winsize={rel:F2}");
            return rel;
        }
    }
}
