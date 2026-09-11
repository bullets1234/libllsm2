using System;
using System.Collections.Generic;
using System.Globalization;
using LlsmBindings;
using UtauEngineNg.Llsm;

namespace UtauEngineNg.Diagnostics
{
    /// <summary>
    /// 特定倍音の振幅をパイプライン各段でトレースする診断ツール。
    /// 環境変数 L2R_HMTRACE=&lt;周波数Hz&gt; 指定時のみ有効になり、指定周波数に最も近い
    /// 倍音（HM振幅）または VTMAGN 包絡値を各段でログ出力し、どの段で振幅が落ちるかを
    /// 特定するために使う。読み取り専用で合成結果には一切影響を与えない。
    /// </summary>
    public static class HarmonicTracer
    {
        private const string Stage = "HmTrace";
        private const float NoHarmonicFloorDb = -999f;

        /// <summary>L2R_HMTRACE が非空なら true。</summary>
        public static bool Enabled { get; }

        /// <summary>L2R_HMTRACE の float 値（Hz）。Enabled=false のときは未定義（0）。</summary>
        public static float TargetFreq { get; }

        static HarmonicTracer()
        {
            string? raw = Environment.GetEnvironmentVariable("L2R_HMTRACE");
            if (!string.IsNullOrEmpty(raw) &&
                float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float freq))
            {
                Enabled = true;
                TargetFreq = freq;
            }
            else
            {
                Enabled = false;
                TargetFreq = 0f;
            }
        }

        /// <summary>
        /// 各フレームの HM（調波モデル）振幅から、TargetFreq に最も近い倍音
        /// （k = round(TargetFreq/f0), 1-based, ampl[k-1]）の dB を読み、
        /// 有声フレーム全体の中央値を1行でログ出力する。
        /// </summary>
        public static void TraceHm(ChunkHandle chunk, int nfrm, string stage, ILogger log)
        {
            if (!Enabled) return;
            try
            {
                var dbValues = new List<float>(nfrm);

                for (int i = 0; i < nfrm; i++)
                {
                    var fr = LlsmBindings.Llsm.GetFrame(chunk, i);
                    float f0 = LlsmBindings.Llsm.GetFrameF0(fr);
                    if (f0 <= 0) continue;

                    var hm = FrameAccess.TryGetHm(fr);
                    if (!hm.HasValue || !hm.Value.HasAmplitudes) continue;

                    int k = (int)MathF.Round(TargetFreq / f0);
                    if (k < 1 || k > hm.Value.NHar) continue;

                    float[] ampl = hm.Value.ReadAmplitudes();
                    float a = ampl[k - 1];
                    float db = a > 0 ? 20f * MathF.Log10(a) : NoHarmonicFloorDb;
                    dbValues.Add(db);
                }

                if (dbValues.Count == 0)
                {
                    log.Info(Stage, $"[{stage}] har@{TargetFreq}Hz: no HM frames");
                    return;
                }

                float median = Median(dbValues);
                log.Info(Stage, $"[{stage}] har@{TargetFreq}Hz: median={median:F1}dB (frames={dbValues.Count})");
            }
            catch (Exception ex)
            {
                log.Warn(Stage, $"[{stage}] TraceHm failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 各フレームの VTMAGN（声道スペクトル, dB）から TargetFreq における値を線形補間で
        /// 読み、有声フレーム全体の中央値を1行でログ出力する。
        /// bin freq = b * fnyq/(nspec-1), fnyq = fs/2。
        /// </summary>
        public static void TraceVtmagn(ChunkHandle chunk, int nfrm, string stage, ILogger log, int fs)
        {
            if (!Enabled) return;
            try
            {
                var dbValues = new List<float>(nfrm);
                float fnyq = fs / 2f;

                for (int i = 0; i < nfrm; i++)
                {
                    var fr = LlsmBindings.Llsm.GetFrame(chunk, i);
                    float f0 = LlsmBindings.Llsm.GetFrameF0(fr);
                    if (f0 <= 0) continue;

                    float[] vtmagn = FrameAccess.ReadVtMagn(fr);
                    int nspec = vtmagn.Length;
                    if (nspec < 2) continue;

                    float binPos = TargetFreq * (nspec - 1) / fnyq;
                    binPos = Math.Clamp(binPos, 0f, nspec - 1);
                    int b0 = (int)MathF.Floor(binPos);
                    int b1 = Math.Min(b0 + 1, nspec - 1);
                    float t = binPos - b0;
                    float db = vtmagn[b0] + (vtmagn[b1] - vtmagn[b0]) * t;
                    dbValues.Add(db);
                }

                if (dbValues.Count == 0)
                {
                    log.Info(Stage, $"[{stage}] vtmagn@{TargetFreq}Hz: no VTMAGN frames");
                    return;
                }

                float median = Median(dbValues);
                log.Info(Stage, $"[{stage}] vtmagn@{TargetFreq}Hz: median={median:F1}dB (frames={dbValues.Count})");
            }
            catch (Exception ex)
            {
                log.Warn(Stage, $"[{stage}] TraceVtmagn failed: {ex.Message}");
            }
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            int c = values.Count;
            return c % 2 == 0 ? (values[c / 2 - 1] + values[c / 2]) / 2f : values[c / 2];
        }

        /// <summary>
        /// 隣接する有声フレーム対 i→i+1 について、対象倍音 k = round(TargetFreq/f0) の
        /// 位相 phse[k-1] のフレーム間差分（wrap 済み）を集計し、その円環標準偏差
        /// （フレーム間位相コヒーレンスの乱れ度合い）をログ出力する。
        /// 位相伝播（ChunkPhasePropagate）後の段では理論位相進み分が既に織り込まれて
        /// いるため、差分そのもの（追加の理論値減算なし）が残差となる。
        /// std が大きいほど、フレーム間で位相が揃わずOLA時に打ち消し合いやすいことを示す。
        /// </summary>
        public static void TracePhaseCoherence(ChunkHandle chunk, int nfrm, string stage, ILogger log, float thopSec)
        {
            if (!Enabled) return;
            try
            {
                var residuals = new List<float>(nfrm);

                for (int i = 0; i < nfrm - 1; i++)
                {
                    var fr0 = LlsmBindings.Llsm.GetFrame(chunk, i);
                    var fr1 = LlsmBindings.Llsm.GetFrame(chunk, i + 1);
                    float f0a = LlsmBindings.Llsm.GetFrameF0(fr0);
                    float f0b = LlsmBindings.Llsm.GetFrameF0(fr1);
                    if (f0a <= 0 || f0b <= 0) continue;

                    var hm0 = FrameAccess.TryGetHm(fr0);
                    var hm1 = FrameAccess.TryGetHm(fr1);
                    if (!hm0.HasValue || !hm1.HasValue || !hm0.Value.HasPhases || !hm1.Value.HasPhases) continue;

                    float f0 = (f0a + f0b) * 0.5f;
                    int k = (int)MathF.Round(TargetFreq / f0);
                    if (k < 1 || k > hm0.Value.NHar || k > hm1.Value.NHar) continue;

                    float[] ph0 = hm0.Value.ReadPhases();
                    float[] ph1 = hm1.Value.ReadPhases();
                    float diff = WrapPi(ph1[k - 1] - ph0[k - 1]);
                    residuals.Add(diff);
                }

                if (residuals.Count == 0)
                {
                    log.Info(Stage, $"[{stage}] phase-coherence@{TargetFreq}Hz: no voiced frame pairs");
                    return;
                }

                // 円環平均を中心とした円環標準偏差（rad）を計算する。
                double sx = 0, sy = 0;
                foreach (float r in residuals) { sx += Math.Cos(r); sy += Math.Sin(r); }
                float meanAngle = (float)Math.Atan2(sy, sx);

                double sumSq = 0;
                foreach (float r in residuals)
                {
                    float d = WrapPi(r - meanAngle);
                    sumSq += (double)d * d;
                }
                float std = (float)Math.Sqrt(sumSq / residuals.Count);

                log.Info(Stage, $"[{stage}] phase-coherence@{TargetFreq}Hz: std={std:F3}rad (pairs={residuals.Count})");
            }
            catch (Exception ex)
            {
                log.Warn(Stage, $"[{stage}] TracePhaseCoherence failed: {ex.Message}");
            }
        }

        /// <summary>角度を (-π, π] へ wrap する。</summary>
        private static float WrapPi(float a)
        {
            a %= 2f * MathF.PI;
            if (a > MathF.PI) a -= 2f * MathF.PI;
            if (a <= -MathF.PI) a += 2f * MathF.PI;
            return a;
        }
    }
}
