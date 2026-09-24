using System;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// llsm_analyze の HM/NM 分解は 2k-16kHz 帯で実測 3〜7dB のエネルギーを取りこぼす
    /// （調波モデル・雑音PSDの両方が過小評価。オリジナルエンジンでも同一現象＝解析アルゴリズム
    /// 自体の限界）。本クラスは解析直後、フレーム毎に源信号の帯域エネルギーとモデル
    /// （HM調波のΣampl²/2 + NM PSD積分）を比較し、不足分を NM PSD へブースト（dB加算）する。
    ///
    /// PSD の物理スケール規約（layer0.c 実装から導出）:
    ///   llsm_estimate_psd (dsputils.c) は |FFT|² / Σw² という「生の周期グラム」を返す
    ///   （Parseval: Σ_half |X_k|² ≈ (nfft/2) * Px, Px=窓内平均パワー）。
    ///   layer0.c の llsm_analyze_noise_psd は最終的に
    ///     dst_nm->psd[j] = 10*log10(dst_psd[j] * 44100/analysisFs + 1e-12)
    ///   として格納する（dst_psd は上記の生周期グラムを対数領域スムージング後 exp() で戻したもの）。
    ///   したがって物理PSD（∫PSD(f)df = 分散 になる規約）は
    ///     PSD_phys(f) = 10^(psd_dB(f)/10) * (2 / 44100)
    ///   という analysisFs に依存しない定数 2/44100 で書ける
    ///   （"* 44100/analysisFs" が周期グラム→PSD変換の 1/analysisFs 依存性をちょうど打ち消すため）。
    ///   帯域エネルギー = Σ_{bin in band} PSD_phys(freq_bin) * Δf, Δf = fnyq/(npsd-1)。
    ///
    /// HM レンダリング損失の補正（実測較正）:
    ///   解析直後の HM 振幅（Σampl²/2）は源信号の帯域パワーとほぼ一致する（診断実測で 1-3dB 差）。
    ///   しかし Layer1 包絡フィット→LFモデル分解→Layer0 オーバーラップアド再構成を経た「実際に
    ///   鳴る」調波成分は、diag/dump_baseline/sinusoid.wav を source と比較（codec_ceiling.ps1）
    ///   した実測で 2k-20kHz 帯域ごとに追加で 3〜9dB 失われることを確認した（Layer1包絡フィット単体
    ///   の残差は 0.42dB しかない＝ResidualEnvelopeCorrector既知、よって主因は Layer0 再構成/
    ///   オーバーラップアド段。オリジナルエンジンにも同一現象があり、ボイス非依存の構造的損失と
    ///   判断）。この「解析時 HM 振幅からは見えない実際の損失」を考慮しないと、NM 側の不足分計算が
    ///   過小評価され、ブーストがほぼ無効果になる（実測で確認済み）。そのため解析時 HM 振幅を
    ///   下記テーブル分だけ割り引いた「実効 HM」を不足分計算の基準として使う。
    /// </summary>
    public static class BandEnergyCalibrator
    {
        private const string Stage = "BandCal";

        // layer0.c 由来の固定スケール定数（analysisFs に依存しない）。上記コメント参照。
        private const double PsdPhysScale = 2.0 / 44100.0;

        // 実測済み HM レンダリング損失（dB、帯域順は BandEdges と対応）。
        // 由来: diag/dump_baseline/sinusoid.wav vs diag/voice_src.wav (codec_ceiling.ps1, A#4 100 g0)。
        // 注: 2-4k, 12-16k は NM のベースライン寄与が小さく、+15dB クランプ及び nmE 自体の
        // 小ささが律速するため、この定数を増やしても効果は頭打ち（実測確認済み、下部レポート参照）。
        private static readonly double[] HmRenderLossDb = { 5.31, 3.55, 3.08, 6.48, 9.14, 6.85 };

        // 補正対象の帯域境界（Hz）。6 帯域。低域(100-2kHz)はほぼ完璧なので対象外。
        private static readonly float[] BandEdges = { 2000f, 4000f, 6000f, 8000f, 12000f, 16000f, 20000f };
        private const int NumBands = 6;

        // ゲイン補間ノードの周波数（帯域中心）。0Hz/2kHz で 0、20kHz で最終帯域値を保持、
        // それ以外は帯域中心を通る区分線形（階段状にならないようにする）。
        private static readonly float[] GainNodeFreq = { 2000f, 3000f, 5000f, 7000f, 10000f, 14000f, 18000f, 20000f };

        private const float MinBoostDb = 0f;
        private const float MaxBoostDb = 24f;
        private const double TinyEnergy = 1e-12;

        /// <summary>
        /// 解析直後のチャンクに対し、フレーム毎の帯域エネルギー較正を適用する。
        /// </summary>
        /// <param name="chunk">解析済みチャンク（analysisFs でのフレーム、Layer1変換前）</param>
        /// <param name="nfrm">フレーム数</param>
        /// <param name="segment">元セグメント（fs, ダウンサンプル前の元波形）</param>
        /// <param name="fs">元セグメントのサンプリング周波数</param>
        /// <param name="thopSec">フレーム間隔（秒）</param>
        /// <param name="log">ログ出力先</param>
        public static void Apply(ChunkHandle chunk, int nfrm, float[] segment, int fs, float thopSec, UtauEngineNg.Diagnostics.ILogger log)
        {
            var conf = LlsmBindings.Llsm.GetConf(chunk);
            float fnyq = LlsmBindings.Llsm.GetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ);
            if (fnyq <= 0 || nfrm <= 0 || segment.Length == 0) return;

            // 解析窓長: 約25ms を2のべき乗へ切り上げ（44.1kHzなら1103→2048）。
            int windowLen = NextPow2((int)Math.Ceiling(0.025 * fs));
            float[] window = HannWindow(windowLen);
            double winPower = 0;
            for (int i = 0; i < windowLen; i++) winPower += (double)window[i] * window[i];

            double[] boostSum = new double[NumBands];
            int voicedFrames = 0;

            double[] re = new double[windowLen];
            double[] im = new double[windowLen];
            double[] srcE = new double[NumBands];
            double[] hmE = new double[NumBands];
            double[] nmE = new double[NumBands];
            float[] boostDb = new float[NumBands];
            float[] nodeVal = new float[GainNodeFreq.Length];

            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                float f0 = FrameAccess.GetF0(frame);

                // --- 源信号の帯域パワー(FFT) ---
                int center = (int)(i * thopSec * fs);
                FillWindowedSlice(segment, center, windowLen, window, re, im);
                Fft(re, im);

                Array.Clear(srcE, 0, NumBands);
                Array.Clear(hmE, 0, NumBands);
                Array.Clear(nmE, 0, NumBands);

                int halfLen = windowLen / 2;
                for (int k = 0; k <= halfLen; k++)
                {
                    float freq = (float)k * fs / windowLen;
                    int band = BandIndex(freq);
                    if (band < 0) continue;
                    double p = (re[k] * re[k] + im[k] * im[k]) / winPower;
                    srcE[band] += p;
                }
                for (int b = 0; b < NumBands; b++)
                    srcE[b] *= 2.0 / windowLen;

                // --- HM モデルの帯域パワー（Σampl²/2） ---
                var hm = FrameAccess.TryGetHm(frame);
                if (f0 > 0 && hm.HasValue && hm.Value.HasAmplitudes)
                {
                    float[] ampl = hm.Value.ReadAmplitudes();
                    for (int h = 0; h < ampl.Length; h++)
                    {
                        float freq = (h + 1) * f0;
                        int band = BandIndex(freq);
                        if (band < 0) continue;
                        hmE[band] += (double)ampl[h] * ampl[h] / 2.0;
                    }
                }

                // --- NM モデルの帯域パワー（PSD積分） ---
                var nm = FrameAccess.TryGetNm(frame);
                float[]? psd = null;
                float psdDeltaF = 0;
                if (nm.HasValue && nm.Value.HasPsd && nm.Value.NPsd > 1)
                {
                    psd = nm.Value.ReadPsd();
                    psdDeltaF = fnyq / (nm.Value.NPsd - 1);
                    for (int j = 0; j < psd.Length; j++)
                    {
                        float freq = j * psdDeltaF;
                        int band = BandIndex(freq);
                        if (band < 0) continue;
                        double linear = Math.Pow(10.0, psd[j] / 10.0);
                        nmE[band] += linear * PsdPhysScale * psdDeltaF;
                    }
                }

                if (psd == null) continue; // NM が無ければブースト不可

                // --- 帯域毎の不足分をブースト量(dB)へ変換 ---
                // hmE は解析時の理想値なので、Layer1/Layer0 再構成で実際に失われる分
                // （HmRenderLossDb, 実測較正済み）を差し引いた「実効HM」を基準にする。
                for (int b = 0; b < NumBands; b++)
                {
                    double effectiveHmE = hmE[b] * Math.Pow(10.0, -HmRenderLossDb[b] / 10.0);
                    double denom = Math.Max(effectiveHmE + nmE[b], TinyEnergy);
                    if (srcE[b] > denom && nmE[b] > TinyEnergy)
                    {
                        double numerator = Math.Max(srcE[b] - effectiveHmE, nmE[b] * 0.01);
                        double db = 10.0 * Math.Log10(numerator / Math.Max(nmE[b], TinyEnergy));
                        boostDb[b] = (float)Math.Clamp(db, MinBoostDb, MaxBoostDb);
                    }
                    else
                    {
                        boostDb[b] = 0f;
                    }

                    if (f0 > 0) boostSum[b] += boostDb[b];
                }
                if (f0 > 0) voicedFrames++;

                // ノード値: 2000Hzは0（立ち上がり基点）、以降は帯域中心の値、20000Hzは最終帯域値で保持。
                nodeVal[0] = 0f;
                for (int b = 0; b < NumBands; b++) nodeVal[b + 1] = boostDb[b];
                nodeVal[^1] = boostDb[NumBands - 1];

                // --- NM PSD へゲインを加算（区分線形補間、階段状にならない） ---
                for (int j = 0; j < psd.Length; j++)
                {
                    float freq = j * psdDeltaF;
                    psd[j] += InterpolateGain(nodeVal, freq);
                }
                nm!.Value.WritePsd(psd);
            }

            if (voicedFrames > 0)
            {
                log.Info(Stage,
                    $"Boosted NM PSD (mean dB over {voicedFrames} voiced frames): " +
                    $"2-4k={boostSum[0] / voicedFrames:F2}, 4-6k={boostSum[1] / voicedFrames:F2}, " +
                    $"6-8k={boostSum[2] / voicedFrames:F2}, 8-12k={boostSum[3] / voicedFrames:F2}, " +
                    $"12-16k={boostSum[4] / voicedFrames:F2}, 16-20k={boostSum[5] / voicedFrames:F2}");
            }
        }

        /// <summary>周波数が属する補正対象帯域のインデックス(0-5)。対象外なら -1。</summary>
        private static int BandIndex(float freq)
        {
            for (int b = 0; b < NumBands; b++)
                if (freq >= BandEdges[b] && freq < BandEdges[b + 1]) return b;
            return -1;
        }

        /// <summary>帯域中心を通る区分線形補間ゲイン(dB)。2kHz未満/20kHz超は 0。
        /// <paramref name="nodeVal"/> は呼び出し側でフレーム毎に一度だけ構築された
        /// ノード値配列（<see cref="GainNodeFreq"/> と対応）。</summary>
        private static float InterpolateGain(float[] nodeVal, float freq)
        {
            if (freq < GainNodeFreq[0] || freq > GainNodeFreq[^1]) return 0f;

            for (int n = 1; n < GainNodeFreq.Length; n++)
            {
                if (freq <= GainNodeFreq[n])
                {
                    float f0v = GainNodeFreq[n - 1], f1v = GainNodeFreq[n];
                    float v0 = nodeVal[n - 1], v1 = nodeVal[n];
                    float t = f1v > f0v ? (freq - f0v) / (f1v - f0v) : 0f;
                    return v0 + (v1 - v0) * t;
                }
            }
            return nodeVal[^1];
        }

        /// <summary>segment から center を中心に windowLen 分のハン窓済みスライスを FFT 入力へ書き込む
        /// （範囲外はゼロ埋め）。</summary>
        private static void FillWindowedSlice(float[] segment, int center, int windowLen, float[] window, double[] re, double[] im)
        {
            int start = center - windowLen / 2;
            for (int i = 0; i < windowLen; i++)
            {
                int idx = start + i;
                float x = (idx >= 0 && idx < segment.Length) ? segment[idx] : 0f;
                re[i] = x * window[i];
                im[i] = 0.0;
            }
        }

        private static float[] HannWindow(int n)
        {
            var w = new float[n];
            if (n == 1) { w[0] = 1f; return w; }
            for (int i = 0; i < n; i++)
                w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (n - 1));
            return w;
        }

        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        /// <summary>反復基数2 Cooley-Tukey FFT（in-place, 未正規化の順変換）。n は2のべき乗。</summary>
        private static void Fft(double[] re, double[] im)
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
