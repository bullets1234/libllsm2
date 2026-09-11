using System;
using System.Collections.Generic;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// 倍音らしさ（harmonicity）に基づく HM/NM の振り分けゲート。
    ///
    /// llsm_analyze は F0 の整数倍位置すべてに正弦波をフィットするため、原音の高域が
    /// 息由来のノイズ（倍音構造なし）であっても、その帯域のエネルギーの一部が「安定した
    /// 正弦波の束」として HM に入る。合成すると本来ノイズだった帯域が横縞の倍音として鳴り、
    /// 金属的な異音になる（実測: 母音の立ち上がり 100ms、8〜12kHz で顕著。原音のピーク対谷
    /// ≈ 0dB に対し調波成分に横縞）。
    ///
    /// 本ゲートは解析直後（Layer1 変換前、HM が存在する状態）に、各有声フレーム・各倍音について
    /// 原音 STFT のピーク対谷（peak − floor, dB）を測り、倍音らしさの重み w∈[0,1] を
    /// 時間方向・倍音方向に平滑化したうえで
    ///   - HM 振幅を √w 倍（パワーで w 倍）に減らし
    ///   - 減らしたパワー (1−w)·a²/2 を NM PSD の同帯域へ密度加算する（総エネルギー保存）
    /// 純粋な倍音（peak−floor ≥ <see cref="HarmonicDb"/>）は無変更、ノイズ（≤ <see cref="NoiseDb"/>）は
    /// 全量ノイズへ移す。<see cref="HybridExcitationEffect"/>（固定クロスオーバー）の測定駆動版。
    /// L2R_HARMGATE=0 / N64 で無効化（A/B 用）。
    /// </summary>
    public static class HarmonicityGate
    {
        private const string Stage = "HarmGate";

        /// <summary>これ以上のピーク対谷なら完全に倍音（w=1）。</summary>
        private const float HarmonicDb = 14f;
        /// <summary>これ以下のピーク対谷ならノイズ（w=0）。Hann 窓のノイズで max/median の偏りが約 +5dB。</summary>
        private const float NoiseDb = 6f;
        /// <summary>これ未満の倍音は対象外（低域は常に倍音で、窓の分解能も相対的に粗い）。</summary>
        private const float MinFreqHz = 1500f;
        /// <summary>解析窓長（F0 周期の倍数）。谷測定位置 ±0.5F0 が主ローブ外になるよう 5 周期。</summary>
        private const float WindowPeriods = 5f;
        private const float WindowMinSec = 0.020f, WindowMaxSec = 0.060f;
        private const int TimeSmoothRadius = 2;
        private const int HarmonicSmoothRadius = 1;
        /// <summary>layer0.c:412 の PSD 規約 10·log10(psd·44100/fs) に対応する物理スケール（他エフェクトと同一）。</summary>
        private const double PsdPhysScale = 2.0 / 44100.0;
        private const double Eps = 1e-9;

        // 調整用の環境変数（A/B 用）: L2R_HARMGATE_MINHZ=<Hz>, L2R_HARMGATE_DB=<noise>,<harmonic>
        private static readonly float MinFreq = EnvFloat("L2R_HARMGATE_MINHZ", MinFreqHz, 0f, 20000f);
        private static readonly (float noise, float harmonic) GateDb = ResolveGateDb();

        private static float EnvFloat(string name, float def, float lo, float hi)
        {
            var s = Environment.GetEnvironmentVariable(name);
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= lo && v <= hi ? v : def;
        }

        private static (float, float) ResolveGateDb()
        {
            var s = Environment.GetEnvironmentVariable("L2R_HARMGATE_DB");
            if (s != null)
            {
                var parts = s.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)
                    && b > a && a >= 0f && b <= 60f)
                    return (a, b);
            }
            return (NoiseDb, HarmonicDb);
        }

        /// <summary>
        /// <paramref name="signal"/> は解析に使った波形（<paramref name="analysisFs"/> でサンプリング、
        /// チャンクのフレーム i の中心は i·thop·analysisFs）。
        /// </summary>
        public static void Apply(ChunkHandle chunk, int nfrm, float[] signal, int analysisFs, float thopSec, float audibleNyq, ILogger log)
        {
            if (nfrm <= 0 || signal.Length == 0 || analysisFs <= 0) return;

            var f0s = new float[nfrm];
            var hmFrames = new HmView?[nfrm];
            int maxNhar = 0;
            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                float f0 = FrameAccess.GetF0(frame);
                f0s[i] = f0;
                if (f0 <= 0) continue;
                var hm = FrameAccess.TryGetHm(frame);
                if (hm is not { HasAmplitudes: true } hmv) continue;
                hmFrames[i] = hmv;
                maxNhar = Math.Max(maxNhar, hmv.NHar);
            }
            if (maxNhar <= 0) return;

            // --- パス1: 倍音らしさ w を測る（対象外は NaN） ---
            var w = new float[nfrm, maxNhar];
            for (int i = 0; i < nfrm; i++) for (int h = 0; h < maxNhar; h++) w[i, h] = float.NaN;

            var floorScratch = new List<double>();
            for (int i = 0; i < nfrm; i++)
            {
                float f0 = f0s[i];
                if (f0 <= 0 || hmFrames[i] == null) continue;
                int nhar = hmFrames[i]!.Value.NHar;

                float winSec = Math.Clamp(WindowPeriods / f0, WindowMinSec, WindowMaxSec);
                int windowLen = Math.Max(64, (int)Math.Round(analysisFs * winSec));
                int fftLen = HarmonicRefinementCorrector.NextPow2(windowLen) * 4;
                int halfLen = fftLen / 2;
                float[] window = HarmonicRefinementCorrector.HannWindow(windowLen);
                var re = new double[fftLen];
                var im = new double[fftLen];
                var mag = new double[halfLen + 1];

                int center = (int)Math.Round((double)i * thopSec * analysisFs);
                HarmonicRefinementCorrector.FillWindowedSpectrum(signal, center, windowLen, window, fftLen, re, im);
                HarmonicRefinementCorrector.Fft(re, im);
                for (int b = 0; b <= halfLen; b++) mag[b] = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);

                for (int h = 0; h < nhar; h++)
                {
                    float fk = f0 * (h + 1);
                    if (fk > audibleNyq) break;
                    if (fk < MinFreq) continue;

                    double peak = HarmonicRefinementCorrector.FindHarmonicPeakMagnitude(mag, halfLen, fk, f0, analysisFs, fftLen);
                    double floor = HarmonicRefinementCorrector.HarmonicFloorMedian(mag, halfLen, fk, f0, analysisFs, fftLen, floorScratch);
                    if (peak <= Eps || floor <= Eps) { w[i, h] = 0f; continue; }
                    float r = (float)(20.0 * Math.Log10(peak / floor));
                    float t = Math.Clamp((r - GateDb.noise) / (GateDb.harmonic - GateDb.noise), 0f, 1f);
                    w[i, h] = t * t * (3f - 2f * t); // smoothstep
                }
            }

            // --- 平滑化: 時間 ±2 フレーム、倍音 ±1（対象値のみで平均） ---
            var ws = new float[nfrm, maxNhar];
            for (int i = 0; i < nfrm; i++)
            {
                for (int h = 0; h < maxNhar; h++)
                {
                    if (float.IsNaN(w[i, h])) { ws[i, h] = float.NaN; continue; }
                    double sum = 0; int cnt = 0;
                    for (int di = -TimeSmoothRadius; di <= TimeSmoothRadius; di++)
                    {
                        int ii = i + di;
                        if (ii < 0 || ii >= nfrm) continue;
                        for (int dh = -HarmonicSmoothRadius; dh <= HarmonicSmoothRadius; dh++)
                        {
                            int hh = h + dh;
                            if (hh < 0 || hh >= maxNhar || float.IsNaN(w[ii, hh])) continue;
                            sum += w[ii, hh]; cnt++;
                        }
                    }
                    ws[i, h] = cnt > 0 ? (float)(sum / cnt) : w[i, h];
                }
            }

            // --- パス2: HM を減らし、減らした分を NM PSD へ移す ---
            var conf = LlsmBindings.Llsm.GetConf(chunk);
            float fnyq = LlsmBindings.Llsm.GetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ);
            if (fnyq <= 0) fnyq = analysisFs / 2f;

            int touchedFrames = 0;
            double movedTotal = 0, harmonicTotal = 0;
            double[] bandMoved = new double[4], bandTotal = new double[4]; // 1.5-3k, 3-6k, 6-10k, 10k+
            double wSum = 0; int wCnt = 0;

            for (int i = 0; i < nfrm; i++)
            {
                if (hmFrames[i] == null) continue;
                var hmv = hmFrames[i]!.Value;
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                var nm = FrameAccess.TryGetNm(frame);
                if (nm is not { HasPsd: true } nmv || nmv.NPsd < 2) continue;

                float f0 = f0s[i];
                float[] ampl = hmv.ReadAmplitudes();
                int npsd = nmv.NPsd;
                double binHz = fnyq / (npsd - 1);
                var addDensity = new double[npsd];
                bool touched = false;

                for (int h = 0; h < ampl.Length; h++)
                {
                    float keep = ws[i, h];
                    if (float.IsNaN(keep)) continue;
                    wSum += keep; wCnt++;
                    float fk = f0 * (h + 1);
                    double power = (double)ampl[h] * ampl[h] / 2.0;
                    int band = fk < 3000 ? 0 : fk < 6000 ? 1 : fk < 10000 ? 2 : 3;
                    bandTotal[band] += power; harmonicTotal += power;
                    if (keep >= 0.999f) continue;

                    double moved = power * (1.0 - keep);
                    ampl[h] *= MathF.Sqrt(Math.Max(keep, 0f));
                    bandMoved[band] += moved; movedTotal += moved;

                    // 密度 ΔP/f0 を [fk-f0/2, fk+f0/2] へ一様加算（単一ビン加算は櫛状になる）
                    int lo = Math.Max(0, (int)((fk - f0 / 2) / binHz));
                    int hi = Math.Min(npsd - 1, (int)((fk + f0 / 2) / binHz));
                    double density = moved / f0;
                    for (int j = lo; j <= hi; j++) addDensity[j] += density;
                    touched = true;
                }
                if (!touched) continue;

                float[] psd = nmv.ReadPsd();
                for (int j = 0; j < npsd; j++)
                {
                    if (addDensity[j] <= 0) continue;
                    double lin = Math.Pow(10.0, psd[j] / 10.0) * PsdPhysScale + addDensity[j];
                    psd[j] = (float)(10.0 * Math.Log10(lin / PsdPhysScale + 1e-30));
                }
                hmv.WriteAmplitudes(ampl);
                nmv.WritePsd(psd);
                touchedFrames++;
            }

            string Pct(int b) => bandTotal[b] > 0 ? $"{100.0 * bandMoved[b] / bandTotal[b]:F0}%" : "-";
            log.Info(Stage, $"Harmonicity gate (>= {MinFreq:F0}Hz, {GateDb.noise:F0}..{GateDb.harmonic:F0}dB): {touchedFrames}/{nfrm} frames, mean w={(wCnt > 0 ? wSum / wCnt : 1):F2}, " +
                            $"HM power moved to NM: 1.5-3k {Pct(0)}, 3-6k {Pct(1)}, 6-10k {Pct(2)}, 10k+ {Pct(3)} " +
                            $"(total {(harmonicTotal > 0 ? 100.0 * movedTotal / harmonicTotal : 0):F1}%)");
        }
    }
}
