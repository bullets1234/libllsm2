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

        private readonly DiagnosticsContext _diag;

        public EnginePipeline(DiagnosticsContext diag) => _diag = diag;

        public void Run(ResamplerArgs args)
        {
            var log = _diag.Log;
            using var _ = _diag.Profiler.Measure("total");

            log.Info(Stage, $"Input={Path.GetFileName(args.InputWav)} Pitch={args.PitchName}({args.TargetF0:F1}Hz) Vel={args.Velocity} Flags='{args.Flags}'");

            // 1. WAV 読み込み
            var (samples, fs) = WavIo.ReadMono(args.InputWav);
            log.Debug(Stage, $"WAV {samples.Length} samples @ {fs}Hz ({samples.Length / (float)fs * 1000:F1}ms)");

            // 2. FRQ 読み込み（"foo.wav.frq" → "foo_wav.frq" の順に探索）
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

            int nhop = (int)(ThopSeconds * fs);

            // 4. F0 推定
            F0Result f0Result;
            using (_diag.Profiler.Measure("f0_estimate"))
                f0Result = new F0Provider(_diag).Estimate(
                    segment, fs, nhop, ThopSeconds, frqData, seg.OffsetSamples, seg.TotalLength, args.TargetF0, args.ParsedFlags);
            float[] f0 = f0Result.F0;
            _diag.Dump.DumpF0("f0_final", f0, ThopSeconds);

            // 5. LLSM 解析（2x オーバーサンプリング）
            AnalysisResult analysis;
            using (_diag.Profiler.Measure("analyze"))
                analysis = new Analyzer(_diag).Analyze(segment, fs, f0, ThopSeconds, f0Result.SrcF0, args.ParsedFlags);
            var chunk = analysis.Chunk;
            int nfrm = analysis.NumFrames;
            float actualThop = analysis.ThopSeconds;

            // srcF0 を解析チャンクの有声中央値から確定（FRQ 仮値を置換）
            float srcF0 = MedianVoicedF0(chunk, nfrm, f0Result.SrcF0);
            log.Info(Stage, $"srcF0={srcF0:F1}Hz (provisional={f0Result.SrcF0:F1}Hz)");

            // 6. 解析後のノイズモデル品質処理
            var nmProc = new NoiseModelProcessor(log);
            nmProc.ApplyMedianFilterToPsd(chunk, nfrm);
            nmProc.ConstrainBoundaryPsd(chunk, nfrm);

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

            // 9. 合成
            var sp = SynthesisParams.FromFlags(
                args.ParsedFlags, srcF0, args.TargetF0, new System.Collections.Generic.List<int>(args.PitchBend),
                args.Tempo, consonantFrames, consonantStretch, stretchRatio, actualThop, overlapMs, args.Modulation);

            SynthesisResult synth;
            using (_diag.Profiler.Measure("synthesize"))
                synth = new StandardSynthesizer(_diag).Synthesize(chunk, fs, sp);
            float[] output = synth.Output;

            // 10. 子音原音ブレンド（C フラグ）
            ConsonantBlend.Apply(
                output, segment, f0, fs, seg.ConsonantSamples, consonantFrames, consonantStretch, actualThop,
                args.ParsedFlags.ConsonantBlend, synth.Sinusoid, synth.Noise, log);

            // 11. 後処理（ボリューム・正規化）
            PostProcessor.Apply(output, args.Volume, log);

            // 12. 書き出し
            _diag.Dump.DumpWav("output", output, fs);
            if (synth.Sinusoid != null) _diag.Dump.DumpWav("sinusoid", synth.Sinusoid, fs);
            if (synth.Noise != null) _diag.Dump.DumpWav("noise", synth.Noise, fs);
            WavIo.WriteMono16(args.OutputWav, output, fs);
            log.Info(Stage, $"Wrote {Path.GetFileName(args.OutputWav)} ({output.Length} samples, {output.Length / (float)fs * 1000:F1}ms)");
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
