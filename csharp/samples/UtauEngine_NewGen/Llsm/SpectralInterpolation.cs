using System;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// フレーム補間に用いるスペクトル/位相の数学ヘルパ群（純関数、ネイティブ依存なし）。
    /// Akima 補間、円環位相補間、VTMAGN のビン毎補間を提供する。
    ///
    /// 注: 旧実装のケプストラム分離補間（部分DCT/IDCT）は、DCT 係数が全帯域から
    /// 計算されるためノイズフロア帯（実倍音のない高域）の乱れがフォルマント帯へ
    /// 漏れ込み、フレームレートの AM 変調（倍音±200Hz のサイドバンド＝ジリジリ音）
    /// を生むことが実測で判明したため、ビン毎の直接補間に置き換えた。
    /// （振幅ジッタ 2.17% → 0.004%、サイドバンド約 30dB 改善）
    /// </summary>
    public static class SpectralInterpolation
    {
        /// <summary>等間隔4点 Akima 補間（区間 [p1,p2] を t∈[0,1] で補間）。</summary>
        public static float AkimaInterp(float p0, float p1, float p2, float p3, float t)
        {
            float d0 = p1 - p0;
            float d1 = p2 - p1;
            float d2 = p3 - p2;
            float dm1 = 2.0f * d0 - d1;
            float d3 = 2.0f * d2 - d1;

            float w1L = MathF.Abs(d2 - d1);
            float w1R = MathF.Abs(d0 - dm1);
            float s1 = (w1L + w1R > 1e-10f) ? (w1L * d0 + w1R * d1) / (w1L + w1R) : (d0 + d1) * 0.5f;

            float w2L = MathF.Abs(d3 - d2);
            float w2R = MathF.Abs(d1 - d0);
            float s2 = (w2L + w2R > 1e-10f) ? (w2L * d1 + w2R * d2) / (w2L + w2R) : (d1 + d2) * 0.5f;

            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return h00 * p1 + h10 * s1 + h01 * p2 + h11 * s2;
        }

        /// <summary>位相を単位円上で線形補間（ラップアラウンド安全）。</summary>
        public static float CircularInterpolatePhase(float phase1, float phase2, float ratio)
        {
            float ax = MathF.Cos(phase1), ay = MathF.Sin(phase1);
            float bx = MathF.Cos(phase2), by = MathF.Sin(phase2);
            float cx = ax * (1.0f - ratio) + bx * ratio;
            float cy = ay * (1.0f - ratio) + by * ratio;
            return MathF.Atan2(cy, cx);
        }

        /// <summary>VTMAGN（dB）のビン毎2点線形補間。</summary>
        public static float[] CepstralInterpolateVtmagn(float[] vtmagn0, float[] vtmagn1, float ratio, int cepOrder = -1)
        {
            int n = Math.Min(vtmagn0.Length, vtmagn1.Length);
            var result = new float[n];
            for (int i = 0; i < n; i++)
                result[i] = vtmagn0[i] * (1 - ratio) + vtmagn1[i] * ratio;
            return result;
        }

        /// <summary>VTMAGN（dB）のビン毎4点 Akima 補間。</summary>
        public static float[] CepstralInterpolateVtmagnCubic(
            float[] vtmagn0, float[] vtmagn1, float[] vtmagn2, float[] vtmagn3, float ratio, int cepOrder = -1)
        {
            int n = Math.Min(Math.Min(vtmagn0.Length, vtmagn1.Length), Math.Min(vtmagn2.Length, vtmagn3.Length));
            var result = new float[n];
            for (int i = 0; i < n; i++)
                result[i] = AkimaInterp(vtmagn0[i], vtmagn1[i], vtmagn2[i], vtmagn3[i], ratio);
            return result;
        }
    }
}
