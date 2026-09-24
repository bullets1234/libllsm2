using System;
using System.IO;
using LlsmBindings;
using UtauEngineNg.Audio;
using UtauEngineNg.Cli;
using UtauEngineNg.Diagnostics;
using UtauEngineNg.Effects;
using UtauEngineNg.Llsm;
using UtauEngineNg.Pitch;
using UtauEngineNg.Synthesis;

namespace UtauEngineNg.Core
{
    /// <summary>
    /// エンジンのトップレベル・オーケストレーター。引数解析後の 1 リクエストを
    /// 「読み込み → 区間切り出し → F0 推定 → LLSM 解析 → 合成 → 後処理 → 書き出し」の
    /// 各ステージに分解し、各段でログ・プロファイル・トレース・ダンプを通す。
    /// （UtauEngine の Resample 関数を再構成）
    /// </summary>
    public sealed class EnginePipeline
    {
        private const string Stage = "Pipeline";
        private const float ThopSeconds = 0.005f; // 5ms
        /// <summary>
        /// 解析の端パディング（フレーム）。区間の外側に実音声があればその分を余分に切り出して
        /// 解析し、後で捨てる。区間端で解析窓がゼロ埋めを見ることによる先頭/末尾フレームの
        /// HM/NM 崩れ（先頭のクリック・息のスパイク）を防ぐ。L2R_EDGEPAD=0 / N32 で無効化。
        /// </summary>
        private const int AnalysisPadFrames = 4;

        private readonly DiagnosticsContext _diag;

        public EnginePipeline(DiagnosticsContext diag) => _diag = diag;

        public void Run(ResamplerArgs args)
        {
            var log = _diag.Log;
            using var _ = _diag.Profiler.Measure("total");

            log.Info(Stage, $"Input={Path.GetFileName(args.InputWav)} Pitch={args.PitchName}({args.TargetF0:F1}Hz) Vel={args.Velocity} Flags='{args.Flags}'");
            if (args.ParsedFlags.DiagDisable != 0)
                log.Info(Stage, $"N{args.ParsedFlags.DiagDisable}: diag disable -" +
                    (args.ParsedFlags.DisableVsphseExtension ? " vsphse-ext" : "") +
                    (args.ParsedFlags.DisableVsphseSmoother ? " smoother" : "") +
                    (args.ParsedFlags.DisableResidualCorrection ? " residual" : "") +
                    (args.ParsedFlags.DisableEenvClamp ? " eenv-clamp" : "") +
                    (args.ParsedFlags.DisableNoiseTexture ? " nm-texture" : "") +
                    (args.ParsedFlags.DisableEdgePad ? " edge-pad" : "") +
                    (args.ParsedFlags.DisableHarmonicityGate ? " harm-gate" : "") +
                    (args.ParsedFlags.DisableResidualExcitation ? " res-exc" : ""));

            // 1. WAV 読み込み
            var (samples, fs) = WavIo.ReadMono(args.InputWav);
            log.Debug(Stage, $"WAV {samples.Length} samples @ {fs}Hz ({samples.Length / (float)fs * 1000:F1}ms)");

            // 2. 周波数表の読み込み
            //    ニューラル表 (*.frq.l2r) があれば優先。なければ従来の .frq。
            L2rF0Data? neuralF0 = TryLoadL2rTable(args.InputWav, samples, log);
            FrqData? frqData = TryLoadFrq(args.InputWav, log);

            // 3. 区間切り出し（offset / cutoff / consonant）
            var seg = SegmentRange.Compute(samples, fs, args.Offset, args.Cutoff, args.Consonant);
            if (seg.TotalLength <= 0)
            {
                log.Warn(Stage, "Empty usable segment, writing silence");
                WavIo.WriteMono16(args.OutputWav, new float[1], fs);
                return;
            }
            float[] segment = new float[seg.TotalLength];
            Array.Copy(samples, seg.StartSample, segment, 0, seg.TotalLength);
            log.Debug(Stage, $"Segment {seg.TotalLength} samples ({seg.TotalLength / (float)fs * 1000:F1}ms), consonant {seg.ConsonantSamples} samples");

            int nhop = Math.Max(1, (int)MathF.Round(ThopSeconds * fs));
            // 実サンプルホップと完全に一致した thop を下流へ渡す。5ms ちょうどを渡すと
            // 44.1kHz では nhop=220(4.9887ms) との差が累積し、約 2.2 秒で F0 系列と
            // 解析フレームが 1 フレーム分ドリフトする。
            float thopSec = nhop / (float)fs;

            // 3.5 解析用の端パディング: 区間外の実音声を前後に足す（利用可能な範囲で、フレーム単位）
            bool edgePad = Environment.GetEnvironmentVariable("L2R_EDGEPAD") != "0" && !args.ParsedFlags.DisableEdgePad;
            int padStartFrames = edgePad ? Math.Min(AnalysisPadFrames, seg.StartSample / nhop) : 0;
            int padEndFrames = edgePad ? Math.Min(AnalysisPadFrames, (samples.Length - (seg.StartSample + seg.TotalLength)) / nhop) : 0;
            int padStart = padStartFrames * nhop, padEnd = padEndFrames * nhop;
            float[] analysisSegment = segment;
            if (padStart > 0 || padEnd > 0)
            {
                analysisSegment = new float[seg.TotalLength + padStart + padEnd];
                Array.Copy(samples, seg.StartSample - padStart, analysisSegment, 0, analysisSegment.Length);
                // パディング区間のレベルを区間端のレベル以下に制限する。オフセットが破裂音の閉鎖（無音）に
                // あるとき、直前の母音の尾が解析窓へ漏れ込んでノート先頭に「ブツ」が出た（実測で先頭 5ms
                // が最大 +11.6dB）。母音の途中から切る場合は両者同レベルなので従来通りの効果を保つ。
                float headScale = LimitPadLevel(analysisSegment, 0, padStart, padStart, Math.Min(padStart, seg.TotalLength));
                float tailScale = LimitPadLevel(analysisSegment, padStart + seg.TotalLength, padEnd,
                    padStart + Math.Max(0, seg.TotalLength - padEnd), Math.Min(padEnd, seg.TotalLength));
                log.Debug(Stage, $"Analysis edge padding: +{padStartFrames}f head (x{headScale:F2}), +{padEndFrames}f tail (x{tailScale:F2})");
            }
            int expectedFrames = seg.TotalLength / nhop + 1; // パディングなしの場合のフレーム数

            // 4. F0 推定（パディング込みの区間で行い、後でパディング分を捨てる）
            F0Result f0Result;
            using (_diag.Profiler.Measure("f0_estimate"))
                f0Result = new F0Provider(_diag).Estimate(
                    analysisSegment, fs, nhop, thopSec, frqData, seg.OffsetSamples - padStart, analysisSegment.Length,
                    args.TargetF0, args.ParsedFlags, neuralF0);
            float[] f0Padded = f0Result.F0;
            int keepFrames = Math.Min(expectedFrames, f0Padded.Length - padStartFrames - padEndFrames);
            if (keepFrames < 1) { padStartFrames = 0; keepFrames = f0Padded.Length; }
            float[] f0 = new float[keepFrames];
            Array.Copy(f0Padded, padStartFrames, f0, 0, keepFrames);
            var micro = f0Result.Micro.Slice(padStartFrames, keepFrames);
            _diag.Dump.DumpF0("f0_final", f0, thopSec);

            // 5. LLSM 解析（2x オーバーサンプリング、パディング込み → フレーム切り詰め）
            AnalysisResult analysis;
            using (_diag.Profiler.Measure("analyze"))
                analysis = new Analyzer(_diag).Analyze(analysisSegment, fs, f0Padded, thopSec, f0Result.SrcF0, args.ParsedFlags,
                    padStartFrames, keepFrames);
            using var chunk = analysis.Chunk; // 解析チャンクは Run 完了時に確定的に解放
            int nfrm = analysis.NumFrames;
            float actualThop = analysis.ThopSeconds;

            // srcF0 を解析チャンクの有声中央値から確定（FRQ 仮値を置換）
            float srcF0 = MedianVoicedF0(chunk, nfrm, f0Result.SrcF0);
            log.Info(Stage, $"srcF0={srcF0:F1}Hz (provisional={f0Result.SrcF0:F1}Hz)");

            // 6. 解析後のノイズモデル品質処理
            var nmProc = new NoiseModelProcessor(log);
            // L2R_PSDMED=0 / L2R_PSDBOUND=0 で無効化（A/B 用）
            if (Environment.GetEnvironmentVariable("L2R_PSDMED") != "0") nmProc.ApplyMedianFilterToPsd(chunk, nfrm);
            if (Environment.GetEnvironmentVariable("L2R_PSDBOUND") != "0") nmProc.ConstrainBoundaryPsd(chunk, nfrm);
            // 有声部の低域ノイズ塊（弾き音・渡りの「ブッ」）を定常レベル基準で頭打ち（L2R_LFCAP=0 で無効化）
            if (Environment.GetEnvironmentVariable("L2R_LFCAP") != "0") nmProc.CapVoicedLowFreqPsd(chunk, nfrm);
            // 原音倍音位置の櫛形の谷を均す（試験・既定オフ。L2R_DECOMB=1 で有効）
            if (Environment.GetEnvironmentVariable("L2R_DECOMB") == "1") nmProc.DecombVoicedPsd(chunk, nfrm);

            // 7. ストレッチ係数
            float consonantStretch = MathF.Pow(2.0f, (100.0f - args.Velocity) / 100.0f);
            int consonantFrames = Math.Min((int)(args.Consonant / 1000.0 / actualThop), nfrm);
            int stretchableFrames = nfrm - consonantFrames;
            float stretchRatio = ComputeStretchRatio(args.LengthMs, consonantFrames, stretchableFrames, actualThop, consonantStretch);

            float overlapMs = 0;
            if (args.ParsedFlags.ModulationPlus && args.Cutoff < 0)
                overlapMs = Math.Max(0, Math.Abs(args.Cutoff) - args.Consonant);

            log.Info(Stage, $"Stretch: consonant {consonantFrames}f x{consonantStretch:F2}, stretchable {stretchableFrames}f x{stretchRatio:F3}");

            // 8. HNR 改善（D フラグ、Layer0/HM が存在する解析直後の状態で）
            new HnrEffect(args.ParsedFlags.HnrEnhancement, log).Apply(chunk, nfrm, fs);

            // 8.5 高域ハイブリッド励振（Y フラグ・試験実装、HM が存在する Layer1 変換前に）
            new HybridExcitationEffect(args.ParsedFlags.HybridExcitation, log).Apply(chunk, nfrm, fs);

            // 9. 合成
            var sp = SynthesisParams.FromFlags(
                args.ParsedFlags, srcF0, args.TargetF0, new System.Collections.Generic.List<int>(args.PitchBend),
                args.Tempo, consonantFrames, consonantStretch, stretchRatio, actualThop, overlapMs, args.Modulation,
                micro);

            SynthesisResult synth;
            using (_diag.Profiler.Measure("synthesize"))
                synth = new StandardSynthesizer(_diag).Synthesize(chunk, fs, sp, analysis.Residual, segment);
            float[] output = synth.Output;

            // 10. 子音原音ブレンド（C フラグ）
            ConsonantBlend.Apply(
                output, segment, f0, fs, seg.ConsonantSamples, consonantFrames, consonantStretch, actualThop,
                args.ParsedFlags.ConsonantBlend, synth.Sinusoid, synth.Noise, log);

            // 11. 後処理（基準レベル正規化・ボリューム・ピークリミット）
            // 基準は原音・出力とも有声フレームのみの実効レベル（無音/子音/ノイズ床の割合に非依存）
            PostProcessor.Apply(output, args.Volume, log, PostProcessor.VoicedLevelDb(segment, f0, nhop), synth.FrameF0, nhop);

            // 12. 書き出し
            _diag.Dump.DumpWav("output", output, fs);
            if (synth.Sinusoid != null) _diag.Dump.DumpWav("sinusoid", synth.Sinusoid, fs);
            if (synth.Noise != null) _diag.Dump.DumpWav("noise", synth.Noise, fs);
            WavIo.WriteMono16(args.OutputWav, output, fs);
            log.Info(Stage, $"Wrote {Path.GetFileName(args.OutputWav)} ({output.Length} samples, {output.Length / (float)fs * 1000:F1}ms)");
        }

        /// <summary>
        /// パディング区間 [padStart, padStart+padLen) の RMS が参照区間 [refStart, refStart+refLen) の RMS を
        /// 超える場合、パディング区間を参照レベルまで減衰する。適用した倍率を返す（1 なら無変更）。
        /// </summary>
        private static float LimitPadLevel(float[] x, int padStart, int padLen, int refStart, int refLen)
        {
            if (padLen <= 0 || refLen <= 0) return 1f;
            double Rms(int a, int n) { double e = 0; for (int i = a; i < a + n; i++) e += (double)x[i] * x[i]; return Math.Sqrt(e / n); }
            double padRms = Rms(padStart, padLen), refRms = Rms(refStart, refLen);
            if (padRms <= refRms || padRms <= 1e-9) return 1f;
            float scale = (float)(refRms / padRms);
            for (int i = padStart; i < padStart + padLen; i++) x[i] *= scale;
            return scale;
        }

        private static FrqData? TryLoadFrq(string inputWav, ILogger log)
        {
            string direct = inputWav + ".frq";
            string underscore = FrqFile.PathFor(inputWav);
            string path = File.Exists(direct) ? direct : underscore;
            var frq = FrqFile.TryRead(path);
            if (frq != null) log.Debug(Stage, $"FRQ loaded: {Path.GetFileName(path)} ({frq.F0Values.Length} frames, avg {frq.AverageF0:F1}Hz)");
            return frq;
        }

        /// <summary>
        /// ニューラル周波数表 (*.frq.l2r) を読む。原音と一致しない古い表を掴まないよう
        /// サンプル数とハッシュを検証し、不一致なら黙って FRQ / PYIN へフォールバックする。
        /// L2R_NOL2R=1 で強制無効化（代替手段との A/B 用）。
        /// </summary>
        private static L2rF0Data? TryLoadL2rTable(string inputWav, float[] samples, ILogger log)
        {
            if (Environment.GetEnvironmentVariable("L2R_NOL2R") == "1") return null;

            string path = L2rF0File.PathFor(inputWav);
            if (!File.Exists(path))
            {
                string alt = inputWav + L2rF0File.Extension;
                if (!File.Exists(alt)) return null;
                path = alt;
            }

            var table = L2rF0File.TryRead(path);
            if (table == null || !table.HasData)
            {
                log.Warn(Stage, $"L2R table unreadable, ignoring: {Path.GetFileName(path)}");
                return null;
            }
            if (table.SourceSamples != samples.Length)
            {
                log.Warn(Stage, $"L2R table stale (samples {table.SourceSamples} != {samples.Length}), ignoring");
                return null;
            }
            if (table.SourceHash != L2rF0File.ComputeHash(samples))
            {
                log.Warn(Stage, "L2R table stale (hash mismatch), ignoring");
                return null;
            }

            log.Debug(Stage, $"L2R table loaded: {Path.GetFileName(path)} " +
                             $"({table.FrameCount} frames @ {table.HopSeconds * 1000:F1}ms, model={table.Model})");
            return table;
        }

        private static float ComputeStretchRatio(float lengthReq, int consonantFrames, int stretchableFrames, float actualThop, float consonantStretch)
        {
            if (lengthReq <= 0 || stretchableFrames <= 0) return 1.0f;
            float consonantMs = consonantFrames * actualThop * 1000f * consonantStretch;
            float stretchableMs = stretchableFrames * actualThop * 1000f;
            float targetStretchableMs = lengthReq - consonantMs;
            return targetStretchableMs > 0 ? targetStretchableMs / stretchableMs : 1.0f;
        }

        private static float MedianVoicedF0(ChunkHandle chunk, int nfrm, float fallback)
        {
            var voiced = new System.Collections.Generic.List<float>(nfrm);
            for (int i = 0; i < nfrm; i++)
            {
                float f = LlsmBindings.Llsm.GetFrameF0(LlsmBindings.Llsm.GetFrame(chunk, i));
                if (f > 0) voiced.Add(f);
            }
            if (voiced.Count == 0) return fallback;
            voiced.Sort();
            int c = voiced.Count;
            return c % 2 == 0 ? (voiced[c / 2 - 1] + voiced[c / 2]) / 2f : voiced[c / 2];
        }
    }
}
