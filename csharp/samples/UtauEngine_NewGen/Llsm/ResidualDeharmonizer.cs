using System;
using System.Numerics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// 解析残差（原音 − 調波再合成）から、調波の引き残しを除く。
    ///
    /// 残差には調波フィットの誤差分（ビブラート・遷移・高次倍音の低 SNR）が原音の音高で残る。
    /// 等倍では出力の調波と重なって聞こえないが、ピッチシフトすると残差励振側だけが原音の
    /// 音高のまま鳴り、目標音高の調波とのうなり・ザラつきになる（実歌唱で確認、2026-09-15）。
    /// 本クラスはフレーム毎の F0 で各倍音位置 ±<see cref="NotchHalfWidth"/>·F0 を STFT 上で
    /// 減衰させ、倍音間の雑音だけを残す。無声フレームは無変更。
    /// 合成側のフィルタは励振を ±3 ビン（約 130Hz）で白色化するので、ノッチで空いた帯域は
    /// 隣接雑音の増幅で自然に埋まり、平坦な雑音として再現される。
    /// </summary>
    public static class ResidualDeharmonizer
    {
        /// <summary>ノッチ半幅（F0 比）。この内側は完全減衰、外側 <see cref="RampHalfWidth"/> まで余弦で戻す。</summary>
        private const float NotchHalfWidth = 0.12f;
        private const float RampHalfWidth = 0.22f;
        private const float NotchFloor = 0.03f; // -30dB
        private const int Nfft = 2048;

        /// <summary>
        /// <paramref name="residual"/>（fs、フレーム i の中心 = i·nhop）を in-place で処理する。
        /// STFT: Hann 窓 4·nhop、hop nhop（重畳率 75%、和が一定）。
        /// </summary>
        public static void Apply(float[] residual, float[] f0, int nhop, int fs, out int processedFrames)
        {
            processedFrames = 0;
            int n = residual.Length;
            if (n == 0 || f0.Length == 0) return;
            int win = 4 * nhop;
            if (win > Nfft) win = Nfft;
            var w = new float[win];
            for (int i = 0; i < win; i++) w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / win);
            // 分析窓・合成窓に同じ Hann を使うので、hop = win/4 のとき Σw² = 1.5 で一定
            float wsumsq = 0; for (int i = 0; i < win; i += nhop) wsumsq += w[Math.Min(win - 1, i)] * w[Math.Min(win - 1, i)];
            wsumsq = 0; for (int i = 0; i < win; i++) wsumsq += w[i] * w[i]; wsumsq /= (float)win / nhop; // 1 hop あたりの Σw²

            var y = new float[n];
            var buf = new Complex[Nfft];
            double binHz = (double)fs / Nfft;
            int nfrmTotal = n / nhop + 1;
            for (int i = -1; i <= nfrmTotal; i++)
            {
                int center = i * nhop;
                int start = center - win / 2;
                int fi = Math.Clamp(i, 0, f0.Length - 1);
                float fr = f0[fi];
                Array.Clear(buf, 0, Nfft);
                bool any = false;
                for (int k = 0; k < win; k++)
                {
                    int idx = start + k;
                    if (idx < 0 || idx >= n) continue;
                    buf[k] = residual[idx] * w[k]; any = true;
                }
                if (!any) continue;

                if (fr > 0)
                {
                    Fft(buf, false);
                    int half = Nfft / 2;
                    for (int b = 0; b <= half; b++)
                    {
                        double f = b * binHz;
                        double h = Math.Round(f / fr);
                        if (h < 1) continue;
                        double dist = Math.Abs(f - h * fr) / fr; // 最寄り倍音からの距離（F0 比）
                        float g;
                        if (dist <= NotchHalfWidth) g = NotchFloor;
                        else if (dist >= RampHalfWidth) g = 1f;
                        else
                        {
                            float t = (float)((dist - NotchHalfWidth) / (RampHalfWidth - NotchHalfWidth));
                            g = NotchFloor + (1f - NotchFloor) * (0.5f - 0.5f * MathF.Cos(MathF.PI * t));
                        }
                        if (g >= 1f) continue;
                        buf[b] *= g;
                        if (b > 0 && b < half) buf[Nfft - b] *= g;
                    }
                    Fft(buf, true);
                    processedFrames++;
                }
                for (int k = 0; k < win; k++)
                {
                    int idx = start + k;
                    if (idx < 0 || idx >= n) continue;
                    y[idx] += (float)buf[k].Real * w[k] / wsumsq;
                }
            }
            Array.Copy(y, residual, n);
        }

        /// <summary>基数 2 の in-place FFT（inverse は 1/N 正規化込み）。</summary>
        private static void Fft(Complex[] a, bool inverse)
        {
            int n = a.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (a[i], a[j]) = (a[j], a[i]);
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < n; i += len)
                {
                    var wv = Complex.One;
                    for (int j = 0; j < len / 2; j++)
                    {
                        var u = a[i + j]; var v = a[i + j + len / 2] * wv;
                        a[i + j] = u + v; a[i + j + len / 2] = u - v;
                        wv *= wl;
                    }
                }
            }
            if (inverse) for (int i = 0; i < n; i++) a[i] /= n;
        }
    }
}
