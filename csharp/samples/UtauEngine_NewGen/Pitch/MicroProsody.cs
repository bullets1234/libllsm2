using System;
using UtauEngineNg.Llsm;

namespace UtauEngineNg.Pitch
{
    /// <summary>
    /// マイクロプロソディ（微細揺らぎ）トラック。
    /// 原音から抽出した F0 ジッター（セント）と振幅シマー（dB）を保持し、
    /// 出力フレームへ「等速パリンドローム参照」で再適用する。
    /// タイムストレッチしても揺れの変調速度が変わらず（生歌のジッター速度は
    /// ノート長と無関係）、原音より長いノートでも往復参照で統計を保ったまま延長できる。
    /// </summary>
    public sealed class MicroProsodyData
    {
        public static readonly MicroProsodyData Empty =
            new(Array.Empty<float>(), Array.Empty<float>());

        /// <summary>フレーム毎の F0 揺らぎ（セント、無声・無音は較正ノイズで充填済み）。</summary>
        public float[] PitchCents { get; }
        /// <summary>フレーム毎の振幅揺らぎ（dB、同上）。</summary>
        public float[] ShimmerDb { get; }

        public MicroProsodyData(float[] pitchCents, float[] shimmerDb)
        {
            PitchCents = pitchCents;
            ShimmerDb = shimmerDb;
        }

        /// <summary>出力フレーム i の等速参照（パリンドローム延長）。</summary>
        public float PitchAt(int i) =>
            PitchCents.Length == 0 ? 0f : PitchCents[Palindrome(i, PitchCents.Length)];

        public float ShimmerAt(int i) =>
            ShimmerDb.Length == 0 ? 0f : ShimmerDb[Palindrome(i, ShimmerDb.Length)];

        internal static int Palindrome(int i, int n)
        {
            if (n <= 1) return 0;
            int period = 2 * (n - 1);
            int k = i % period;
            return k < n ? k : period - k;
        }
    }

    /// <summary>
    /// 原音からマイクロプロソディを抽出する。
    /// F0 生トラック（PYIN 等）の移動平均残差（>約7Hz、ビブラート帯は抑制）を
    /// セントで、切り出しセグメントのフレーム RMS 残差を dB で取得する。
    /// 同一時間軸で抽出するためピッチ・振幅の生理的相関が保存される。
    /// 無声・無音区間は「実測統計（標準偏差）に較正した決定論バリューノイズ」で
    /// 充填し、連続トラックにする（伸長時の参照切れ防止）。
    /// </summary>
    public static class MicroProsody
    {
        // 75ms 移動平均の残差 ≒ 約7Hz ハイパス。ビブラート帯(4-7Hz)は大部分が
        // 基準側に残り、mod=0 で消える従来挙動と両立する
        private const int HighpassWindowFrames = 15;
        private const float MaxPitchCents = 25f;
        private const float MaxShimmerDb = 2.0f;
        private const float DefaultPitchStdCents = 5f;
        private const float DefaultShimmerStdDb = 0.4f;
        private const float SilenceFloorDb = -80f;
        // コサイン補間バリューノイズ（2スケール合成）の実効標準偏差 ≒ 0.18
        private const float ValueNoiseStdInv = 5.5f;

        /// <summary>
        /// <paramref name="f0Raw"/> は平滑化前の F0 トラック（PYIN 推奨。null なら
        /// ピッチ揺らぎは全て較正ノイズになる）。nfrm は合成側 f0 グリッドと同じ長さ。
        /// </summary>
        public static MicroProsodyData Extract(
            float[]? f0Raw, float[] segment, int nhop, int nfrm)
        {
            if (nfrm <= 0) return MicroProsodyData.Empty;

            // --- ピッチジッター（セント） ---
            var pitch = new float[nfrm];
            var pitchValid = new bool[nfrm];
            if (f0Raw != null)
            {
                int n = Math.Min(nfrm, f0Raw.Length);
                for (int i = 0; i < n; i++)
                {
                    if (f0Raw[i] <= 0) continue;
                    float sum = 0; int cnt = 0;
                    for (int k = Math.Max(0, i - HighpassWindowFrames);
                         k <= Math.Min(n - 1, i + HighpassWindowFrames); k++)
                    {
                        if (f0Raw[k] > 0) { sum += f0Raw[k]; cnt++; }
                    }
                    if (cnt < 3) continue;
                    float cents = 1200f * MathF.Log2(f0Raw[i] * cnt / sum);
                    if (!float.IsFinite(cents)) continue;
                    pitch[i] = Math.Clamp(cents, -MaxPitchCents, MaxPitchCents);
                    pitchValid[i] = true;
                }
            }

            // --- 振幅シマー（dB）: F0 と同一時間軸 → 相関保存 ---
            var rmsDb = new float[nfrm];
            for (int i = 0; i < nfrm; i++)
            {
                long c = (long)i * nhop;
                int lo = (int)Math.Max(0, c - nhop);
                int hi = (int)Math.Min(segment.Length, c + nhop);
                double e = 0;
                for (int s = lo; s < hi; s++) e += (double)segment[s] * segment[s];
                int m = hi - lo;
                rmsDb[i] = m > 0 ? 10f * (float)Math.Log10(e / m + 1e-12) : -120f;
            }
            var shimmer = new float[nfrm];
            var shimmerValid = new bool[nfrm];
            for (int i = 0; i < nfrm; i++)
            {
                if (rmsDb[i] < SilenceFloorDb) continue;
                float sum = 0; int cnt = 0;
                for (int k = Math.Max(0, i - HighpassWindowFrames);
                     k <= Math.Min(nfrm - 1, i + HighpassWindowFrames); k++)
                {
                    if (rmsDb[k] >= SilenceFloorDb) { sum += rmsDb[k]; cnt++; }
                }
                if (cnt < 3) continue;
                shimmer[i] = Math.Clamp(rmsDb[i] - sum / cnt, -MaxShimmerDb, MaxShimmerDb);
                shimmerValid[i] = true;
            }

            // --- 欠損充填（実測統計に較正したフォールバック） ---
            FillGaps(pitch, pitchValid,
                MeasuredStd(pitch, pitchValid, DefaultPitchStdCents), salt: 101);
            FillGaps(shimmer, shimmerValid,
                MeasuredStd(shimmer, shimmerValid, DefaultShimmerStdDb), salt: 202);

            return new MicroProsodyData(pitch, shimmer);
        }

        private static float MeasuredStd(float[] x, bool[] valid, float fallback)
        {
            double s = 0, s2 = 0; int n = 0;
            for (int i = 0; i < x.Length; i++)
                if (valid[i]) { s += x[i]; s2 += (double)x[i] * x[i]; n++; }
            if (n < 20) return fallback;
            double var = s2 / n - (s / n) * (s / n);
            return var > 1e-9 ? (float)Math.Sqrt(var) : fallback;
        }

        private static void FillGaps(float[] x, bool[] valid, float std, int salt)
        {
            for (int i = 0; i < x.Length; i++)
            {
                if (valid[i]) continue;
                x[i] = FractalNoise(i, salt) * std * ValueNoiseStdInv;
            }
        }

        /// <summary>knot 間隔 4/2 フレームの決定論バリューノイズ（約8〜50Hz 帯相当）。</summary>
        private static float FractalNoise(int i, int salt) =>
            ValueNoise(i, 4, salt) * 0.7f + ValueNoise(i, 2, salt + 7) * 0.3f;

        private static float ValueNoise(int i, int interval, int salt)
        {
            int k = i / interval;
            float t = (float)(i % interval) / interval;
            float b = (1f - MathF.Cos(t * MathF.PI)) * 0.5f;
            return DeterministicNoise.Hash(k, salt) * (1f - b)
                 + DeterministicNoise.Hash(k + 1, salt) * b;
        }
    }
}
