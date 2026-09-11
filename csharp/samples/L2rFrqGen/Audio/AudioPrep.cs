using System;

namespace L2rFrqGen.Audio
{
    /// <summary>
    /// 推定器へ渡す前の信号整形。
    /// </summary>
    public static class AudioPrep
    {
        private const int ZeroCrossings = 24;

        /// <summary>
        /// 窓関数付き sinc 補間によるリサンプリング。
        /// ダウンサンプル時はカットオフを出力ナイキストへ下げるため、
        /// 素の多項式補間と違って折り返しが F0 推定を汚さない。
        /// </summary>
        public static float[] Resample(float[] x, int srcRate, int dstRate)
        {
            if (srcRate == dstRate || x.Length == 0) return x;

            double ratio = (double)dstRate / srcRate;
            // 入力サンプル基準の正規化カットオフ×2（=sinc の帯域）。0.95 は遷移帯の余裕。
            double a = Math.Min(1.0, ratio) * 0.95;
            int half = (int)Math.Ceiling(ZeroCrossings / a);
            int outLen = Math.Max(1, (int)Math.Ceiling(x.Length * ratio));

            var y = new float[outLen];
            for (int i = 0; i < outLen; i++)
            {
                double center = i / ratio;
                int n0 = (int)Math.Floor(center) - half;
                int n1 = (int)Math.Floor(center) + half;
                double acc = 0.0;
                for (int n = n0; n <= n1; n++)
                {
                    if (n < 0 || n >= x.Length) continue;
                    double t = n - center;
                    double w = Blackman(t, half);
                    if (w <= 0.0) continue;
                    acc += x[n] * a * Sinc(a * t) * w;
                }
                y[i] = (float)acc;
            }
            return y;
        }

        /// <summary>ステレオ等は WavIo が平均化済み。ここでは DC 除去のみ行う。</summary>
        public static void RemoveDc(float[] x)
        {
            if (x.Length == 0) return;
            double mean = 0.0;
            foreach (var v in x) mean += v;
            mean /= x.Length;
            for (int i = 0; i < x.Length; i++) x[i] -= (float)mean;
        }

        /// <summary>
        /// フレーム毎の RMS。中心が i*hop、窓長 winLen（範囲外は 0 扱い）。
        /// </summary>
        public static float[] FrameRms(float[] x, int hop, int winLen, int frameCount)
        {
            var rms = new float[frameCount];
            int half = winLen / 2;
            for (int i = 0; i < frameCount; i++)
            {
                int c = i * hop;
                int s = Math.Max(0, c - half);
                int e = Math.Min(x.Length, c + half);
                double acc = 0.0;
                for (int n = s; n < e; n++) acc += (double)x[n] * x[n];
                rms[i] = (float)Math.Sqrt(acc / winLen);
            }
            return rms;
        }

        private static double Sinc(double t)
        {
            if (Math.Abs(t) < 1e-9) return 1.0;
            double px = Math.PI * t;
            return Math.Sin(px) / px;
        }

        private static double Blackman(double t, int half)
        {
            if (Math.Abs(t) > half) return 0.0;
            double p = (t + half) / (2.0 * half); // [0,1]
            return 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * p) + 0.08 * Math.Cos(4.0 * Math.PI * p);
        }
    }
}
