using System;
using System.Collections.Generic;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// llsm_analyze が推定する調波(HM)振幅は高域で構造的に過小評価される
    /// （実測: 2-4k -5.1dB, 4-6k -3.5dB, 6-8k -3.2dB, 8-12k -4.5dB, 12-16k -6.8dB。
    /// 100Hz-2kHz はほぼ完璧 ±0.1dB。診断: diag/codec_ceiling.ps1）。
    /// 本クラスは解析直後、原音の STFT から各フレームの倍音ピーク振幅を直接測定し、
    /// HM 振幅とのdB差をゲート付き・時間/倍音両方向に平滑化してから HM 振幅そのものへ適用する。
    ///
    /// 【教訓】過去に NM(雑音) PSD への大ブーストで帯域エネルギーは源信号に一致させられたが、
    /// 倍音をノイズで置換した形になり聴感はホワイトノイズ化して失敗した（BandEnergyCalibrator.cs
    /// 参照、既定OFF）。本補正は調波成分そのもの（HM振幅）を直接補正するため、この失敗を回避する。
    /// また倍音振幅の時間方向ジッタは倍音±フレームレートのサイドバンド（ジリジリ音）を生むため、
    /// 適用前に時間方向の移動平均を必須で行う。
    /// </summary>
    public static class HarmonicRefinementCorrector
    {
        private const string Stage = "HarmFix";

        private const float LowFreqSkipHz = 1500f;   // これ未満の倍音は補正しない（低域は解析がほぼ正確）
        private const float NyquistMargin = 0.95f;   // 倍音探索の上限（ナイキストの95%）
        private const float PeakSearchFrac = 0.2f;   // ピーク探索幅 ±f0*0.2
        private const float FloorOffsetFrac = 0.5f;  // 倍音間フロアの中心オフセット ±f0*0.5
        private const float FloorWidthFrac = 0.1f;   // 倍音間フロアの探索幅 ±f0*0.1
        private const float GateDb = 10f;            // ピークがフロアよりこれ以上高くないと無効な倍音とみなす
        private const float DeltaClampDb = 10f;      // 1倍音あたりのdB差クランプ [-10,+10]（谷倍音の実測欠損は約10dB）
        private const int TimeSmoothRadius = 2;      // 時間方向移動平均半径（フレーム）
        // 倍音方向の平滑は行わない: 谷倍音の欠損は倍音単位で局在するため、隣接倍音
        // (補正不要でdelta≈0)と平均すると必要な補正が1/3に希釈され効果が消える（実測）。
        // ジッタ対策は時間方向平滑のみで担保する。
        private const double Eps = 1e-9;

        /// <summary>
        /// 解析済みチャンクに対し、原音STFTから測定した倍音振幅とのdB差でHM振幅を補正する。
        /// </summary>
        /// <param name="chunk">解析済みチャンク（analysisFs でのフレーム、Layer1変換前）</param>
        /// <param name="nfrm">フレーム数</param>
        /// <param name="signal">解析に使われた原音波形（analysisFs でサンプリング済み。2xオーバーサンプル後の配列）</param>
        /// <param name="analysisFs">signal のサンプリング周波数（解析は常に fs*2 で行われる）</param>
        /// <param name="thopSec">フレーム間隔（秒）</param>
        /// <param name="log">ログ出力先</param>
        public static void Apply(ChunkHandle chunk, int nfrm, float[] signal, int analysisFs, float thopSec, ILogger log)
        {
            if (nfrm <= 0 || signal == null || signal.Length == 0 || analysisFs <= 0) return;

            float nyquist = analysisFs / 2f;
            float cutoffHz = nyquist * NyquistMargin;

            // 解析窓: 正確に25ms（2の冪への切り上げはしない — 2xオーバーサンプル解析では
            // 切り上げで窓が46msに膨らみ、ビブラートの周波数変調（第k倍音はF0偏移のk倍動く）で
            // エネルギーが窓内スミアして谷倍音を過小測定していた。これは llsm_analyze 自身の
            // 過小推定と同じ機構であり、本補正の意味を失わせる）。
            // FFT長のみ2の冪（窓の4倍以上）としゼロパディングでパラボラ補間の分解能を確保する。
            // 25ms窓のHannメインローブ幅は 4/(0.025*f0)*f0 ≈ 0.34*f0（F0=460Hz時）で、
            // フロア測定位置（±0.5*f0）はローブ外に収まる。
            int windowLen = Math.Max(1024, (int)Math.Round(analysisFs * 0.025));
            int fftLen = NextPow2(windowLen) * 4;
            int halfLen = fftLen / 2;
            float[] window = HannWindow(windowLen);
            double windowSum = 0;
            for (int i = 0; i < windowLen; i++) windowSum += window[i];

            var f0s = new float[nfrm];
            var nharPerFrame = new int[nfrm];
            var hmFrames = new HmView?[nfrm];
            int maxNhar = 0;

            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                float f0 = FrameAccess.GetF0(frame);
                f0s[i] = f0;
                if (f0 <= 0) continue;

                var hm = FrameAccess.TryGetHm(frame);
                if (!hm.HasValue || !hm.Value.HasAmplitudes) continue;

                hmFrames[i] = hm;
                nharPerFrame[i] = hm.Value.NHar;
                if (hm.Value.NHar > maxNhar) maxNhar = hm.Value.NHar;
            }

            if (maxNhar <= 0) return;

            // --- パス1: 各フレーム・各倍音の dB 差を測定してマトリクスへ格納する ---
            var applicable = new bool[nfrm, maxNhar];
            var measured = new bool[nfrm, maxNhar]; // ゲート通過し実測できたフレームのみ true
            var delta = new float[nfrm, maxNhar];

            var re = new double[fftLen];
            var im = new double[fftLen];
            var mag = new double[halfLen + 1];
            var floorScratch = new List<double>();

            for (int i = 0; i < nfrm; i++)
            {
                float f0 = f0s[i];
                if (f0 <= 0 || hmFrames[i] == null) continue;

                float[] ampl = hmFrames[i]!.Value.ReadAmplitudes();
                int nhar = nharPerFrame[i];

                int center = (int)Math.Round((double)i * thopSec * analysisFs);
                FillWindowedSpectrum(signal, center, windowLen, window, fftLen, re, im);
                Fft(re, im);
                for (int b = 0; b <= halfLen; b++)
                    mag[b] = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);

                for (int h = 0; h < nhar; h++)
                {
                    int k = h + 1;
                    float fk = f0 * k;
                    if (fk > cutoffHz) break;
                    if (fk < LowFreqSkipHz) continue;

                    applicable[i, h] = true; // 対象帯域内＝スコープ内（ゲート不通過でも0扱いで平滑化に参加）

                    double peakMag = FindHarmonicPeakMagnitude(mag, halfLen, fk, f0, analysisFs, fftLen);
                    if (peakMag <= Eps) continue;

                    double floorMag = HarmonicFloorMedian(mag, halfLen, fk, f0, analysisFs, fftLen, floorScratch);
                    if (floorMag <= Eps) continue;

                    double peakDb = 20.0 * Math.Log10(peakMag);
                    double floorDb = 20.0 * Math.Log10(floorMag);
                    if (peakDb - floorDb < GateDb) continue; // 倍音構造ゲート不通過（0扱い）

                    // STFTピーク振幅 → 正弦波振幅への換算（導出は FillWindowedSpectrum のコメント参照）
                    double measuredAmpl = 2.0 * peakMag / windowSum;
                    float hmAmpl = Math.Max(ampl[h], 1e-8f);
                    double d = 20.0 * Math.Log10(measuredAmpl / hmAmpl);
                    delta[i, h] = (float)Math.Clamp(d, -DeltaClampDb, DeltaClampDb);
                    measured[i, h] = true;
                }
            }

            // --- 平滑化: 時間方向のみ（移動平均, 半径2, ゲート通過した実測フレームのみで平均） ---
            // ゲート不通過フレームを0として混ぜると補正が希釈されるため、実測値のみを平均し、
            // 実測が1つもない近傍では補正しない（0のまま）。
            var smoothedFinal = new float[nfrm, maxNhar];
            for (int h = 0; h < maxNhar; h++)
            {
                for (int i = 0; i < nfrm; i++)
                {
                    if (!applicable[i, h]) continue;
                    double sum = 0;
                    int count = 0;
                    for (int di = -TimeSmoothRadius; di <= TimeSmoothRadius; di++)
                    {
                        int ii = i + di;
                        if (ii < 0 || ii >= nfrm) continue;
                        if (!measured[ii, h]) continue; // 実測値のみ平均（0混入による希釈を排除）
                        sum += delta[ii, h];
                        count++;
                    }
                    smoothedFinal[i, h] = count > 0 ? (float)(sum / count) : 0f;
                }
            }

            // --- パス2: 平滑化済み delta を HM 振幅へ適用して書き戻す ---
            int correctedFrames = 0;
            double sumAbsDelta = 0;
            int countAll = 0;
            double sumDelta2to8k = 0;
            int count2to8k = 0;

            for (int i = 0; i < nfrm; i++)
            {
                float f0 = f0s[i];
                if (f0 <= 0 || hmFrames[i] == null) continue;

                int nhar = nharPerFrame[i];
                float[] ampl = hmFrames[i]!.Value.ReadAmplitudes();
                bool touched = false;

                for (int h = 0; h < nhar; h++)
                {
                    if (!applicable[i, h]) continue;

                    float d = smoothedFinal[i, h];
                    ampl[h] *= MathF.Pow(10f, d / 20f);
                    touched = true;

                    sumAbsDelta += Math.Abs(d);
                    countAll++;

                    float fk = f0 * (h + 1);
                    if (fk >= 2000f && fk < 8000f)
                    {
                        sumDelta2to8k += d;
                        count2to8k++;
                    }
                }

                if (touched)
                {
                    hmFrames[i]!.Value.WriteAmplitudes(ampl);
                    correctedFrames++;
                }
            }

            if (countAll > 0)
            {
                double meanAbsDelta = sumAbsDelta / countAll;
                double meanDelta2to8k = count2to8k > 0 ? sumDelta2to8k / count2to8k : 0;
                log.Info(Stage, $"Applied harmonic refinement to {correctedFrames}/{nfrm} frames, mean|delta|={meanAbsDelta:F2}dB, mean delta(2-8k)={meanDelta2to8k:F2}dB");
            }
        }

        /// <summary>
        /// signal から center を中心に windowLen 分の Hann 窓を適用したスライスを、長さ fftLen の
        /// FFT 入力バッファへゼロパディングして書き込む（範囲外はゼロ埋め）。
        ///
        /// 【STFTピーク振幅 → 正弦波振幅への換算式の導出】
        /// 実正弦波 x[n] = A cos(2π f n/fs + φ) を窓 w[n] (n=0..N-1) で切り出した離散時間フーリエ
        /// 変換は、畳み込み定理より X(θ) = (A/2)[e^{jφ} W(θ-θ0) + e^{-jφ} W(θ+θ0)]（θ0=2πf/fs）。
        /// 倍音周波数の近傍（探索窓 fk±f0*0.2 内）では反対側の像 W(θ+θ0) は無視できるほど小さく、
        /// ピーク振幅は |X(θ0)| ≈ (A/2) |W(0)| で近似できる。
        /// W(0) = Σ_{n=0}^{N-1} w[n] = windowSum（Hann窓ではコヒーレントゲイン≈0.5なので
        /// windowSum ≈ N*0.5）。よって |X_peak| ≈ A * windowSum / 2 → A ≈ 2*|X_peak| / windowSum。
        /// ゼロパディング（fftLen = N*4）は W(θ) を θ 方向に sinc 補間して分解能を上げるだけで、
        /// windowSum（=W(0)）自体は変化しないため、この換算式は fftLen に依存しない。
        /// </summary>
        internal static void FillWindowedSpectrum(float[] signal, int center, int windowLen, float[] window, int fftLen, double[] re, double[] im)
        {
            Array.Clear(re, 0, fftLen);
            Array.Clear(im, 0, fftLen);
            int start = center - windowLen / 2;
            for (int i = 0; i < windowLen; i++)
            {
                int idx = start + i;
                float x = (idx >= 0 && idx < signal.Length) ? signal[idx] : 0f;
                re[i] = x * window[i];
            }
        }

        /// <summary>fk ± f0*0.2 の範囲でピークビンを探し、対数振幅のパラボラ補間で真のピーク振幅を推定する。</summary>
        internal static double FindHarmonicPeakMagnitude(double[] mag, int halfLen, float fk, float f0, int analysisFs, int fftLen)
        {
            float loHz = fk - f0 * PeakSearchFrac;
            float hiHz = fk + f0 * PeakSearchFrac;
            int binLo = Math.Max(1, (int)Math.Floor((double)loHz * fftLen / analysisFs));
            int binHi = Math.Min(halfLen - 1, (int)Math.Ceiling((double)hiHz * fftLen / analysisFs));
            if (binLo > binHi) return 0;

            int peakBin = -1;
            double peakVal = -1;
            for (int b = binLo; b <= binHi; b++)
            {
                if (mag[b] > peakVal) { peakVal = mag[b]; peakBin = b; }
            }
            if (peakBin < 0) return 0;

            // 対数振幅の3点パラボラ補間（Jacobsen/Smith の標準式）でビン間の真のピーク振幅を推定。
            double alpha = Math.Log(mag[peakBin - 1] + Eps);
            double beta = Math.Log(mag[peakBin] + Eps);
            double gamma = Math.Log(mag[peakBin + 1] + Eps);
            double denom = alpha - 2.0 * beta + gamma;
            if (Math.Abs(denom) < 1e-12) return Math.Exp(beta);

            double p = 0.5 * (alpha - gamma) / denom;
            double peakLogMag = beta - 0.25 * (alpha - gamma) * p;
            return Math.Exp(peakLogMag);
        }

        /// <summary>倍音間フロア（fk±f0*0.5 の ±f0*0.1 範囲、両側まとめて）の中央値を返す。</summary>
        internal static double HarmonicFloorMedian(double[] mag, int halfLen, float fk, float f0, int analysisFs, int fftLen, List<double> scratch)
        {
            scratch.Clear();
            CollectFloorBins(mag, halfLen, fk - f0 * FloorOffsetFrac, f0, analysisFs, fftLen, scratch);
            CollectFloorBins(mag, halfLen, fk + f0 * FloorOffsetFrac, f0, analysisFs, fftLen, scratch);
            if (scratch.Count == 0) return 0;

            scratch.Sort();
            int n = scratch.Count;
            return (n % 2 == 1) ? scratch[n / 2] : 0.5 * (scratch[n / 2 - 1] + scratch[n / 2]);
        }

        private static void CollectFloorBins(double[] mag, int halfLen, float centerHz, float f0, int analysisFs, int fftLen, List<double> dst)
        {
            if (centerHz <= 0) return;
            float loHz = centerHz - f0 * FloorWidthFrac;
            float hiHz = centerHz + f0 * FloorWidthFrac;
            int binLo = Math.Max(0, (int)Math.Floor((double)loHz * fftLen / analysisFs));
            int binHi = Math.Min(halfLen, (int)Math.Ceiling((double)hiHz * fftLen / analysisFs));
            for (int b = binLo; b <= binHi; b++) dst.Add(mag[b]);
        }

        internal static float[] HannWindow(int n)
        {
            var w = new float[n];
            if (n == 1) { w[0] = 1f; return w; }
            for (int i = 0; i < n; i++)
                w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (n - 1));
            return w;
        }

        internal static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        /// <summary>反復基数2 Cooley-Tukey FFT（in-place, 未正規化の順変換）。n は2のべき乗。</summary>
        internal static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
                int half = len / 2;
                for (int i = 0; i < n; i += len)
                {
                    double curRe = 1.0, curIm = 0.0;
                    for (int k = 0; k < half; k++)
                    {
                        double uRe = re[i + k], uIm = im[i + k];
                        double vRe = re[i + k + half] * curRe - im[i + k + half] * curIm;
                        double vIm = re[i + k + half] * curIm + im[i + k + half] * curRe;
                        re[i + k] = uRe + vRe;
                        im[i + k] = uIm + vIm;
                        re[i + k + half] = uRe - vRe;
                        im[i + k + half] = uIm - vIm;
                        double nextRe = curRe * wRe - curIm * wIm;
                        double nextIm = curRe * wIm + curIm * wRe;
                        curRe = nextRe;
                        curIm = nextIm;
                    }
                }
            }
        }
    }
}
