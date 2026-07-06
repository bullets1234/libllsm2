using System;
using System.Linq;
using LlsmBindings;
using UtauEngineNg.Audio;
using UtauEngineNg.Cli;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Pitch
{
    /// <summary>F0 の供給元。</summary>
    public enum F0Source
    {
        /// <summary>FRQ キャッシュ（必要に応じて PYIN V/UV マスク付き）。</summary>
        Frq,
        /// <summary>PYIN 2パス推定。</summary>
        Pyin,
    }

    /// <summary>F0 推定結果。</summary>
    public sealed class F0Result
    {
        public required float[] F0 { get; init; }
        /// <summary>代表 F0（Hz）。FRQ 経路では暫定平均、後段 LLSM で再計算され得る。</summary>
        public required float SrcF0 { get; init; }
        public required F0Source Source { get; init; }
        /// <summary>FRQ 経路で SrcF0 が暫定値（LLSM 再計算が必要）か。</summary>
        public bool SrcF0IsProvisional { get; init; }
    }

    /// <summary>
    /// セグメントの F0 を推定する。FRQ があり P フラグ未指定なら FRQ を、
    /// それ以外は PYIN 2パスを用いる。
    /// （UtauEngine の F0 取得ロジックを移植）
    /// </summary>
    public sealed class F0Provider
    {
        private const string Stage = "Pitch";
        private readonly DiagnosticsContext _diag;

        public F0Provider(DiagnosticsContext diag) => _diag = diag;

        public F0Result Estimate(
            float[] segment, int fs, int nhop, float thopSec,
            FrqData? frqData, int offsetSamples, int totalLength,
            float targetF0, FlagSet flags)
        {
            var log = _diag.Log;

            if (frqData != null && frqData.F0Values.Length > 0 && !flags.BypassFrq)
                return FromFrq(segment, fs, nhop, thopSec, frqData, offsetSamples, totalLength, flags, log);

            return FromPyin(segment, fs, nhop, targetF0, flags, frqData, log);
        }

        private F0Result FromFrq(
            float[] segment, int fs, int nhop, float thopSec,
            FrqData frqData, int offsetSamples, int totalLength, FlagSet flags, ILogger log)
        {
            int frqStartFrame = offsetSamples / frqData.SamplesPerFrame;
            int llsmFrameCount = totalLength / nhop + 1;

            var f0 = new float[llsmFrameCount];
            for (int i = 0; i < llsmFrameCount; i++)
            {
                float frqIdx = frqStartFrame + (float)i * nhop / frqData.SamplesPerFrame;
                int idx = (int)frqIdx;
                float frac = frqIdx - idx;
                int idx1 = Math.Min(idx + 1, frqData.F0Values.Length - 1);
                if (idx >= 0 && idx < frqData.F0Values.Length)
                {
                    float v0 = (float)frqData.F0Values[idx];
                    float v1 = (float)frqData.F0Values[idx1];
                    f0[i] = (v0 > 0 && v1 > 0) ? v0 * (1f - frac) + v1 * frac : v0; // V/UV境界保護
                }
            }

            float srcF0 = (float)frqData.AverageF0;
            log.Info(Stage, $"Initial F0 from FRQ: {srcF0:F1}Hz");

            if (flags.PureFrq)
            {
                log.Info(Stage, "Z flag: pure FRQ mode (PYIN V/UV mask disabled)");
            }
            else
            {
                F0Stabilizer.ApplyPyinVuvMask(f0, segment, fs, nhop, thopSec, log);
            }

            _diag.Dump.DumpF0("f0_frq", f0, thopSec);

            return new F0Result
            {
                F0 = f0,
                SrcF0 = srcF0,
                Source = F0Source.Frq,
                SrcF0IsProvisional = true, // LLSM 解析後に再計算
            };
        }

        private F0Result FromPyin(
            float[] segment, int fs, int nhop, float targetF0, FlagSet flags, FrqData? frqData, ILogger log)
        {
            // 1パス目: 広レンジ
            var f0 = Pyin.Analyze(segment, fs, nhop, 60, 800);
            var voicedPass1 = f0.Where(x => x > 0).ToArray();

            if (voicedPass1.Length > 2)
            {
                Array.Sort(voicedPass1);
                float median1 = voicedPass1[voicedPass1.Length / 2];
                float fmin2 = MathF.Max(50f, median1 * 0.55f);
                float fmax2 = MathF.Min(1000f, median1 * 1.8f);

                if (fmax2 / fmin2 < 800f / 60f * 0.9f)
                {
                    var f0Pass1 = (float[])f0.Clone();
                    f0 = Pyin.Analyze(segment, fs, nhop, fmin2, fmax2);

                    int restored = 0;
                    for (int i = 0; i < Math.Min(f0.Length, f0Pass1.Length); i++)
                    {
                        if (f0[i] <= 0 && f0Pass1[i] > 0) { f0[i] = f0Pass1[i]; restored++; }
                    }
                    log.Info(Stage, $"PYIN 2-pass: range narrowed to {fmin2:F0}-{fmax2:F0}Hz " +
                                    $"(median={median1:F1}Hz)" + (restored > 0 ? $", restored {restored} frames from pass1" : ""));
                }
            }

            var voiced = f0.Where(x => x > 0).ToArray();
            float srcF0 = voiced.Length > 0 ? voiced.Average() : targetF0;

            if (F0Stabilizer.CorrectOctaveJumps(f0, log))
            {
                var corrected = f0.Where(x => x > 0).ToArray();
                srcF0 = corrected.Length > 0 ? corrected.Average() : targetF0;
            }

            if (flags.BypassFrq && frqData != null)
                log.Info(Stage, $"Initial F0 from PYIN (P flag, bypassing FRQ): {srcF0:F1}Hz");
            else
                log.Info(Stage, $"Initial F0 from PYIN: {srcF0:F1}Hz");

            return new F0Result
            {
                F0 = f0,
                SrcF0 = srcF0,
                Source = F0Source.Pyin,
                SrcF0IsProvisional = false,
            };
        }
    }
}
