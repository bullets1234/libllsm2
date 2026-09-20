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
        /// <summary>ニューラル周波数表（*.frq.l2r、L2rFrqGen が事前生成）。</summary>
        Neural,
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
        /// <summary>原音から抽出したマイクロプロソディ（ジッター/シマー）。</summary>
        public MicroProsodyData Micro { get; init; } = MicroProsodyData.Empty;
    }

    /// <summary>
    /// セグメントの F0 を推定する。優先度は
    /// ニューラル周波数表 (*.frq.l2r) &gt; FRQ &gt; PYIN 2パス。
    /// P フラグ指定時はいずれの表も使わず PYIN へ落とす。
    /// （UtauEngine の F0 取得ロジックを移植・拡張）
    /// </summary>
    public sealed class F0Provider
    {
        private const string Stage = "Pitch";
        private readonly DiagnosticsContext _diag;

        public F0Provider(DiagnosticsContext diag) => _diag = diag;

        public F0Result Estimate(
            float[] segment, int fs, int nhop, float thopSec,
            FrqData? frqData, int offsetSamples, int totalLength,
            float targetF0, FlagSet flags, L2rF0Data? neural = null)
        {
            var log = _diag.Log;

            if (neural != null && neural.HasData && !flags.BypassFrq)
                return FromNeural(segment, fs, nhop, thopSec, neural, offsetSamples, totalLength, flags, log);

            if (frqData != null && frqData.F0Values.Length > 0 && frqData.SamplesPerFrame > 0 && !flags.BypassFrq)
                return FromFrq(segment, fs, nhop, thopSec, frqData, offsetSamples, totalLength, flags, log);

            return FromPyin(segment, fs, nhop, targetF0, flags, frqData, log);
        }

        /// <summary>
        /// ニューラル周波数表からの F0。表が有声確信度を持つため、FRQ 経路のような
        /// V/UV 補完用 PYIN 実行が不要になり、精度と速度の両方で有利。
        /// </summary>
        private F0Result FromNeural(
            float[] segment, int fs, int nhop, float thopSec,
            L2rF0Data table, int offsetSamples, int totalLength, FlagSet flags, ILogger log)
        {
            // 表のホップを「元 WAV のサンプル」に換算する。表側は解析レート(16k 等)基準の
            // ホップを持つので、レートが違っても秒に直せば一意に対応が取れる。
            double tableHopInSrc = table.HopSeconds * fs;
            float startFrame = (float)(offsetSamples / tableHopInSrc);
            float step = (float)(nhop / tableHopInSrc);

            int llsmFrameCount = totalLength / nhop + 1;
            var f0 = new float[llsmFrameCount];
            int last = table.FrameCount - 1;

            for (int i = 0; i < llsmFrameCount; i++)
            {
                float idxF = startFrame + i * step;
                int idx = (int)idxF;
                if (idx < 0 || idx > last) continue;
                float frac = idxF - idx;
                int idx1 = Math.Min(idx + 1, last);
                float v0 = table.F0[idx];
                float v1 = table.F0[idx1];
                f0[i] = (v0 > 0 && v1 > 0) ? v0 * (1f - frac) + v1 * frac : v0; // V/UV境界保護
            }

            // 表は生成時の閾値で既に無声を 0 にしているが、確信度を残してあるので
            // 環境変数でより厳しい判定に切り替えられる（かすれ音源の追い込み用）。
            float extraConf = EnvFloat("L2R_L2RCONF", 0f);
            if (extraConf > 0f)
            {
                int masked = 0;
                for (int i = 0; i < llsmFrameCount; i++)
                {
                    if (f0[i] <= 0) continue;
                    int idx = Math.Clamp((int)MathF.Round(startFrame + i * step), 0, last);
                    if (table.Confidence[idx] < extraConf) { f0[i] = 0; masked++; }
                }
                if (masked > 0) log.Info(Stage, $"L2R table: conf<{extraConf:F2} masked {masked} frames");
            }

            var voiced = f0.Where(x => x > 0).ToArray();
            float srcF0 = voiced.Length > 0 ? voiced.Average() : 0f;
            log.Info(Stage, $"Initial F0 from L2R table ({table.Model}, {table.HopSeconds * 1000:F1}ms hop): " +
                            $"{srcF0:F1}Hz, {voiced.Length}/{llsmFrameCount} voiced");

            // 揺らぎ転写(J)を要求されたときだけ PYIN 生値を取りに行く。既定では走らせない。
            var microSource = f0;
            if (flags.Jitter > 0)
            {
                microSource = Pyin.Analyze(segment, fs, nhop, 60, 800);
                log.Debug(Stage, "J flag: PYIN raw track fetched for micro-prosody");
            }

            _diag.Dump.DumpF0("f0_l2r", f0, thopSec);

            return new F0Result
            {
                F0 = f0,
                SrcF0 = srcF0,
                Source = F0Source.Neural,
                SrcF0IsProvisional = true, // LLSM 解析後に再計算
                Micro = MicroProsody.Extract(microSource, segment, nhop, f0.Length),
            };
        }

        private static float EnvFloat(string name, float def)
        {
            var s = Environment.GetEnvironmentVariable(name);
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
        }

        private F0Result FromFrq(
            float[] segment, int fs, int nhop, float thopSec,
            FrqData frqData, int offsetSamples, int totalLength, FlagSet flags, ILogger log)
        {
            // 小数フレーム位置を保持する（整数切り捨てだと最大 1 FRQ ホップ ≈ 6ms
            // F0 軌跡が早い方向へずれ、子音/母音境界で可聴になる）
            float frqStartFrame = (float)offsetSamples / frqData.SamplesPerFrame;
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

            // PYIN 生トラック: V/UV マスクとマイクロプロソディ抽出で共用する。
            // FRQ はフリク生成器側で平滑化済みのため揺らぎ抽出には PYIN 生値を使う
            //（Z フラグ時は PYIN を走らせない従来挙動を維持し、FRQ 補間値で代用）。
            float[]? pyinRaw = null;
            if (flags.PureFrq)
            {
                log.Info(Stage, "Z flag: pure FRQ mode (PYIN V/UV mask disabled)");
            }
            else
            {
                pyinRaw = Pyin.Analyze(segment, fs, nhop, 60, 800);
                F0Stabilizer.ApplyPyinVuvMask(f0, pyinRaw, thopSec, log);
            }

            var micro = MicroProsody.Extract(pyinRaw ?? f0, segment, nhop, f0.Length);

            _diag.Dump.DumpF0("f0_frq", f0, thopSec);

            return new F0Result
            {
                F0 = f0,
                SrcF0 = srcF0,
                Source = F0Source.Frq,
                SrcF0IsProvisional = true, // LLSM 解析後に再計算
                Micro = micro,
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

            // 弾き音直後などの PYIN の追従遅れを波形の局所自己相関で修正（L2R_F0ACF=0 で無効化。A/B 用）
            if (Environment.GetEnvironmentVariable("L2R_F0ACF") != "0"
                && F0Stabilizer.RefineWithAutocorrelation(f0, segment, fs, nhop, log) > 0)
            {
                var refined = f0.Where(x => x > 0).ToArray();
                srcF0 = refined.Length > 0 ? refined.Average() : targetF0;
            }

            if (flags.BypassFrq && frqData != null)
                log.Info(Stage, $"Initial F0 from PYIN (P flag, bypassing FRQ): {srcF0:F1}Hz");
            else
                log.Info(Stage, $"Initial F0 from PYIN: {srcF0:F1}Hz");

            _diag.Dump.DumpF0("f0_pyin", f0, nhop / (float)fs);

            return new F0Result
            {
                F0 = f0,
                SrcF0 = srcF0,
                Source = F0Source.Pyin,
                SrcF0IsProvisional = false,
                // PYIN 経路は f0 自体が生値に近いので、そのまま揺らぎ抽出に使う
                Micro = MicroProsody.Extract(f0, segment, nhop, f0.Length),
            };
        }
    }
}
