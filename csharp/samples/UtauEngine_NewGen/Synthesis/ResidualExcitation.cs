using System;
using System.Collections.Generic;
using UtauEngineNg.Llsm;

namespace UtauEngineNg.Synthesis
{
    /// <summary>
    /// 残差励振（提案 2）。解析残差（原音 − 調波再合成）を「本物の雑音励振」として使う。
    ///
    /// libllsm2 の雑音は既定では乱数を eenv/edc 包絡で変調して作るが、息のパルス同期バーストや
    /// 無声子音の過渡（破裂・摩擦の粒立ち）はモデル化が粗く、白色雑音の質感になる。
    /// 本クラスは出力フレーム毎の原音参照フレーム（テクスチャ転写と同じ実時間カーソル）に
    /// 従って残差から 2·nhop のグレインを取り、Hann 窓（周期形、hop=nhop で和が 1）で
    /// 重畳加算した励振列を作る。等倍・無シフトなら残差がそのまま復元される。
    /// 合成側 (llsm_filter_noise) は励振をフレーム毎に白色化してから PSD+PSDRES を掛けるので、
    /// 残差からはフレーム内の時間構造だけが残り、スペクトル形状はモデル側（各エフェクト込み）が
    /// 決める。残った調波の漏れも白色化でならされる。
    /// 有声区間は PSOLA: 残差の高域バースト包絡から原音のパルスマークを検出し、目標 F0 で
    /// 進めたマーク位置に原音マーク周りの 2 周期 Hann グレインを置く。マーク間隔は原音の実測
    /// 間隔をピッチ比で縮尺するので、ジッタは原音由来のまま、等倍では恒等になる。
    /// 無声区間は実時間グレインの Hann 重畳（hop=nhop で和が 1）。
    /// PSOLA は既定オフ（試聴で不評）。L2R_RESEXC_PSYNC=1 で有効化。既定は全フレームがグレイン重畳。
    /// </summary>
    public static class ResidualExcitation
    {
        /// <summary>デジタル無音区間で白色化ゲインが暴走しないための微小ディザ（-90dBFS 相当）。</summary>
        private const float DitherAmplitude = 3e-5f;
        /// <summary>時間伸縮比のクランプ（2 オクターブ）。</summary>
        private const float MinRatio = 0.25f, MaxRatio = 4f;
        /// <summary>
        /// PSOLA 再配置は実歌唱の試聴で「ダメ」判定（2026-09-13）のため既定オフ。
        /// L2R_RESEXC_PSYNC=1 で有効化（実験用）。
        /// </summary>
        private static readonly bool PitchSync = Environment.GetEnvironmentVariable("L2R_RESEXC_PSYNC") == "1";

        /// <summary>
        /// <paramref name="sourceFrame"/>[k] = 出力フレーム k が参照する原音フレーム。
        /// 出力長は libllsm2 の規約 (nfrm+1)·nhop。
        /// </summary>
        /// <param name="srcF0">原音フレーム毎の F0（無声 0）。</param>
        /// <param name="dstF0">出力フレーム毎の目標 F0（無声 0）。sourceFrame と同じ長さ。</param>
        /// <param name="source">原音波形（残差と同じ整列）。位相マーク検出に使う。null なら残差包絡マーク。</param>
        public static float[] Build(float[] residual, int[] sourceFrame, int nhop, int srcNfrm, float[]? srcF0 = null, float[]? dstF0 = null, float[]? source = null)
        {
            int nfrm = sourceFrame.Length;
            int ny = (nfrm + 1) * nhop;
            var y = new float[ny];

            // 有声で PSOLA を使える出力フレーム（原音側にマークがある場合のみ true に更新）
            var voiced = new bool[nfrm];
            int[]? marks = null;
            int[]? markOfFrame = null; // 原音フレーム s に最も近いマークのインデックス（無ければ -1）
            if (PitchSync && srcF0 != null && dstF0 != null && srcF0.Length >= srcNfrm)
            {
                marks = source != null && source.Length > 0
                    ? FindPhaseMarks(source, srcF0, srcNfrm, nhop)
                    : FindSourceMarks(residual, srcF0, srcNfrm, nhop);
                if (marks.Length > 0)
                {
                    markOfFrame = NearestMarkPerFrame(marks, srcNfrm, nhop);
                    for (int k = 0; k < nfrm; k++)
                    {
                        int sk = Math.Clamp(sourceFrame[k], 0, Math.Max(0, srcNfrm - 1));
                        voiced[k] = k < dstF0.Length && dstF0[k] > 0 && srcF0[sk] > 0 && markOfFrame[sk] >= 0
                                    && Math.Abs(marks[markOfFrame[sk]] - sk * nhop) <= 2 * nhop;
                    }
                }
            }

            // 1. 無声（または PSOLA 不可）フレーム: 実時間グレインの Hann 重畳
            int glen = 2 * nhop;
            var w = new float[glen];
            for (int n = 0; n < glen; n++) w[n] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * n / glen);
            for (int k = 0; k < nfrm; k++)
            {
                if (voiced[k]) continue;
                int sidx = Math.Clamp(sourceFrame[k], 0, Math.Max(0, srcNfrm - 1));
                int srcStart = sidx * nhop - nhop;
                int dstStart = k * nhop - nhop;
                for (int n = 0; n < glen; n++)
                {
                    int si = srcStart + n, di = dstStart + n;
                    if (di < 0 || di >= ny) continue;
                    float v = si >= 0 && si < residual.Length ? residual[si] : 0f;
                    y[di] += v * w[n];
                }
            }

            // 2. 有声フレーム: PSOLA。目標マーク列 t を目標周期で進め、各マークで原音の最寄り
            //    マーク周りの 2 周期グレインを置く。次のマーク間隔は原音の実測間隔をピッチ比で
            //    縮尺する（ジッタは原音由来のまま、等倍なら恒等）。
            if (marks != null && markOfFrame != null)
            {
                double t = -1;
                for (int k = 0; k < nfrm; k++)
                {
                    if (!voiced[k]) { t = -1; continue; }
                    double frameStart = k * nhop - nhop / 2.0, frameEnd = k * nhop + nhop / 2.0;
                    int sk = Math.Clamp(sourceFrame[k], 0, srcNfrm - 1);
                    if (t < 0)
                    {
                        // 有声区間の開始: 原音マークの位相をそのまま使う（等倍恒等）
                        int j0 = markOfFrame[sk];
                        t = frameStart + Mod(marks[j0] - (sk * nhop - nhop / 2.0), fs_period(srcF0![sk], dstF0![k], nhop));
                    }
                    while (t < frameEnd)
                    {
                        int j = markOfFrame[sk];
                        // 実時間カーソルの位置からずれた分だけ隣のマークへ寄せる（出力時刻とフレーム中心の差）
                        j = ShiftMark(marks, j, t - k * nhop, srcF0![sk], nhop);
                        int m = marks[j];
                        int period = PeriodSamples(srcF0[sk], nhop);
                        float r = Math.Clamp(dstF0![k] / srcF0[sk], MinRatio, MaxRatio);
                        // グレイン半幅 = min(原音周期, 目標周期): ピッチ上げでは目標周期より短くして
                        // 隣のバーストを含めない（含めると目標周期と無関係な副バーストが並び、
                        // 包絡の周期性が崩れる。実測: +7 半音で周期が半分に見えた）
                        int half = Math.Min(period, PeriodSamples(dstF0[k], nhop));
                        PlaceGrain(y, residual, m, half, (int)Math.Round(t));
                        int next = j + 1 < marks.Length ? marks[j + 1] - m : period;
                        if (next <= 0 || next > 4 * period) next = period;
                        t += next / r;
                    }
                }
            }

            // 決定論的ディザ
            for (int i = 0; i < ny; i++)
                y[i] += DeterministicNoise.Hash(i, 7919) * DitherAmplitude;
            return y;
        }

        private static double Mod(double a, double m) { double v = a % m; return v < 0 ? v + m : v; }
        private static double fs_period(float srcF0, float dstF0, int nhop) => Math.Max(8.0, SampleRateOf(nhop) / Math.Max(50f, dstF0));
        /// <summary>nhop=5ms 規約からサンプリング周波数を復元する（thop は 5ms 固定）。</summary>
        private static double SampleRateOf(int nhop) => nhop / 0.005;
        private static int PeriodSamples(float f0, int nhop) => Math.Max(8, (int)Math.Round(SampleRateOf(nhop) / Math.Max(50f, f0)));

        /// <summary>フレーム中心からの出力時刻ずれ (offset サンプル) に応じて、マーク列を前後へずらす。</summary>
        private static int ShiftMark(int[] marks, int j, double offset, float f0, int nhop)
        {
            int period = PeriodSamples(f0, nhop);
            int steps = (int)Math.Round(offset / period);
            return Math.Clamp(j + steps, 0, marks.Length - 1);
        }

        /// <summary>原音マーク m を中心とする半幅 half（長さ 2·half）の Hann グレインを、出力位置 center へ加算する。</summary>
        private static void PlaceGrain(float[] y, float[] residual, int m, int half, int center)
        {
            int glen = 2 * half;
            for (int n = 0; n < glen; n++)
            {
                int di = center - half + n, si = m - half + n;
                if (di < 0 || di >= y.Length) continue;
                float v = si >= 0 && si < residual.Length ? residual[si] : 0f;
                float wn = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * n / glen);
                y[di] += v * wn;
            }
        }

        /// <summary>
        /// 基本波の位相からパルスマークを作る。各有声フレームで原音を 2 周期 Hann 窓で切り、
        /// F0 の DFT で基本波の位相 φ を求め、基本波が正のピークになる時刻
        /// t0 = c − φ·fs/(2π·f0) を基準に周期 T で並べる。バースト包絡の最大値を使う方式と違い、
        /// グレインが常に「エネルギーの山」を中心に持つ偏り（人工的な振幅変調＝ブツブツ）が出ない。
        /// </summary>
        private static int[] FindPhaseMarks(float[] x, float[] srcF0, int srcNfrm, int nhop)
        {
            double fs = SampleRateOf(nhop);
            var marks = new List<int>();
            int n = x.Length;
            double lastMark = double.NegativeInfinity;
            for (int s = 0; s < srcNfrm; s++)
            {
                float f0 = srcF0[s];
                if (f0 <= 0) { lastMark = double.NegativeInfinity; continue; }
                double T = fs / f0;
                int c = s * nhop;
                int half = (int)Math.Round(T);                 // 2 周期窓
                double re = 0, im = 0;
                for (int i = -half; i <= half; i++)
                {
                    int idx = c + i;
                    if (idx < 0 || idx >= n) continue;
                    double w = 0.5 + 0.5 * Math.Cos(Math.PI * i / (half + 1));
                    double ph = 2.0 * Math.PI * f0 * i / fs;
                    re += x[idx] * w * Math.Cos(ph);
                    im -= x[idx] * w * Math.Sin(ph);
                }
                if (re == 0 && im == 0) continue;
                double phi = Math.Atan2(im, re);               // x ≈ A cos(2π f0 (t−c)/fs + φ)
                double t0 = c - phi / (2.0 * Math.PI * f0) * fs; // 基本波ピーク時刻
                // このフレームの担当区間 [c − nhop/2, c + nhop/2) 内のマークを並べる
                double lo = c - nhop / 2.0, hi = c + nhop / 2.0;
                double t = t0 - Math.Ceiling((t0 - lo) / T) * T;
                for (; t < hi; t += T)
                {
                    if (t < lo) continue;
                    if (t - lastMark < 0.5 * T) continue;      // 前フレームとの重複除去
                    marks.Add((int)Math.Round(t));
                    lastMark = t;
                }
            }
            return marks.ToArray();
        }

        /// <summary>
        /// 残差の高域バースト包絡から原音のパルスマーク（バースト位置）を検出する。
        /// 有声区間ごとに、最初のフレーム中心 ±T/2 で包絡最大を取り、以後は予測位置 ±20% で追跡する。
        /// </summary>
        private static int[] FindSourceMarks(float[] residual, float[] srcF0, int srcNfrm, int nhop)
        {
            int n = residual.Length;
            if (n == 0) return Array.Empty<int>();
            double fs = SampleRateOf(nhop);
            // 高域包絡: 2ms 移動平均を引いて低域を除き、二乗を 0.3ms 平滑
            int hpHalf = Math.Max(1, (int)(fs * 0.001));
            int smHalf = Math.Max(1, (int)(fs * 0.00015));
            var hp = new float[n];
            // 移動平均（前方累積で近似）
            var cum = new double[n + 1];
            for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + residual[i];
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - hpHalf), hi = Math.Min(n, i + hpHalf + 1);
                float mean = (float)((cum[hi] - cum[lo]) / (hi - lo));
                hp[i] = residual[i] - mean;
            }
            var cum2 = new double[n + 1];
            for (int i = 0; i < n; i++) cum2[i + 1] = cum2[i] + (double)hp[i] * hp[i];
            float Env(int i)
            {
                int lo = Math.Max(0, i - smHalf), hi = Math.Min(n, i + smHalf + 1);
                return (float)((cum2[hi] - cum2[lo]) / (hi - lo));
            }
            float F0At(int pos) { int f = Math.Clamp((int)Math.Round((double)pos / nhop), 0, srcNfrm - 1); return srcF0[f]; }

            var marks = new List<int>();
            int s = 0;
            while (s < srcNfrm)
            {
                if (srcF0[s] <= 0) { s++; continue; }
                // 有声区間 [s, e)
                int e = s; while (e < srcNfrm && srcF0[e] > 0) e++;
                int c = s * nhop;
                int T = PeriodSamples(srcF0[s], nhop);
                int m = ArgMax(Env, Math.Max(0, c - T / 2), Math.Min(n - 1, c + T / 2));
                marks.Add(m);
                while (true)
                {
                    float f0 = F0At(m);
                    if (f0 <= 0) break;
                    int Tn = PeriodSamples(f0, nhop);
                    int pred = m + Tn;
                    if (pred >= Math.Min(n, e * nhop)) break;
                    int lo = pred - Tn / 5, hi = pred + Tn / 5;
                    m = ArgMax(Env, Math.Max(0, lo), Math.Min(n - 1, hi));
                    marks.Add(m);
                }
                s = e;
            }
            return marks.ToArray();
        }

        private static int ArgMax(Func<int, float> f, int lo, int hi)
        {
            int best = lo; float bv = float.MinValue;
            for (int i = lo; i <= hi; i++) { float v = f(i); if (v > bv) { bv = v; best = i; } }
            return best;
        }

        /// <summary>各原音フレーム中心に最も近いマークのインデックス（マーク無しなら -1）。</summary>
        private static int[] NearestMarkPerFrame(int[] marks, int srcNfrm, int nhop)
        {
            var res = new int[srcNfrm];
            int j = 0;
            for (int s = 0; s < srcNfrm; s++)
            {
                int c = s * nhop;
                while (j + 1 < marks.Length && Math.Abs(marks[j + 1] - c) <= Math.Abs(marks[j] - c)) j++;
                res[s] = marks.Length > 0 ? j : -1;
            }
            return res;
        }

        /// <summary>Catmull-Rom 補間（範囲外は 0）。</summary>
        private static float SampleCubic(float[] x, double pos)
        {
            int i1 = (int)Math.Floor(pos);
            float t = (float)(pos - i1);
            float v0 = Get(x, i1 - 1), v1 = Get(x, i1), v2 = Get(x, i1 + 1), v3 = Get(x, i1 + 2);
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * v1 + (-v0 + v2) * t + (2f * v0 - 5f * v1 + 4f * v2 - v3) * t2 + (-v0 + 3f * v1 - 3f * v2 + v3) * t3);
        }

        private static float Get(float[] x, int i) => i >= 0 && i < x.Length ? x[i] : 0f;
    }
}
