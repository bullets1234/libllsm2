using System;
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
    /// 既知の限界: 息のバースト周期は原音の F0 のまま（大きなピッチシフトでは第 2 段階として
    /// ピッチ同期のグレイン再配置が必要）。
    /// </summary>
    public static class ResidualExcitation
    {
        /// <summary>デジタル無音区間で白色化ゲインが暴走しないための微小ディザ（-90dBFS 相当）。</summary>
        private const float DitherAmplitude = 3e-5f;

        /// <summary>
        /// <paramref name="sourceFrame"/>[k] = 出力フレーム k が参照する原音フレーム。
        /// 出力長は libllsm2 の規約 (nfrm+1)·nhop。
        /// </summary>
        public static float[] Build(float[] residual, int[] sourceFrame, int nhop, int srcNfrm)
        {
            int nfrm = sourceFrame.Length;
            int ny = (nfrm + 1) * nhop;
            var y = new float[ny];
            int glen = 2 * nhop;
            var w = new float[glen];
            for (int n = 0; n < glen; n++) w[n] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * n / glen);

            for (int k = 0; k < nfrm; k++)
            {
                int s = Math.Clamp(sourceFrame[k], 0, Math.Max(0, srcNfrm - 1));
                int srcStart = s * nhop - nhop;   // グレイン先頭（中心 = s·nhop）
                int dstStart = k * nhop - nhop;   // 出力側（中心 = k·nhop）
                for (int n = 0; n < glen; n++)
                {
                    int si = srcStart + n, di = dstStart + n;
                    if (di < 0 || di >= ny) continue;
                    float v = si >= 0 && si < residual.Length ? residual[si] : 0f;
                    y[di] += v * w[n];
                }
            }

            // 決定論的ディザ
            for (int i = 0; i < ny; i++)
                y[i] += DeterministicNoise.Hash(i, 7919) * DitherAmplitude;
            return y;
        }
    }
}
