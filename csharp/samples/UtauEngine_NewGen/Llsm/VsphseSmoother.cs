using System;
using System.Runtime.InteropServices;
using LlsmBindings;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// VSPHSE（声帯音源位相）の時間方向平滑化。
    ///
    /// llsm_frame_tolayer1 の vs_phse = phse - vt_phse は、vt_phse（最小位相）が
    /// 2x オーバーサンプル帯域全体（ノイズフロア高次倍音を含む）の包絡から計算される
    /// ため、フレーム毎に ~0.02rad の位相ジッタを含む。合成時 (tolayer0) の vt_phse'
    /// はダウンサンプル後の帯域から計算されるためこのジッタは打ち消されず、
    /// フレームレート(200Hz)の位相変調 → 倍音±200Hz のサイドバンド（ジリジリ音）
    /// として可聴化する。逆位相伝播（デトレンド）後の VSPHSE は定常区間でほぼ一定に
    /// なるため、有声連続区間内で円環移動平均を掛けてジッタを除去する。
    /// </summary>
    public static class VsphseSmoother
    {
        /// <summary>
        /// 分析ジッタの実測値に基づく補正上限。平滑値と原値の差がこれを超える場合は
        /// 「実際の音声変化」とみなし原値を保持する（子音・遷移の鈍り防止）。
        /// </summary>
        private const float PhaseJitterThreshold = 0.15f; // rad（実測ジッタ ~0.02rad）
        private const float MagnJitterThreshold = 1.0f;   // dB （実測ジッタ ~±0.4dB）

        /// <summary>逆位相伝播後の Layer1 チャンクの VSPHSE を時間方向に円環平滑化する。
        /// 補正量が PhaseJitterThreshold を超えるビンは原値を保持（ジッタのみ除去）。</summary>
        /// <param name="radius">平滑化半径（フレーム）。総窓長 2*radius+1。</param>
        public static void Apply(ChunkHandle chunk, int nfrm, int radius = 2)
        {
            // 有声かつ VSPHSE を持つフレームの一覧と配列を先に読む
            var vsArrays = new float[nfrm][];
            for (int i = 0; i < nfrm; i++)
            {
                var fr = LlsmBindings.Llsm.GetFrame(chunk, i);
                if (LlsmBindings.Llsm.GetFrameF0(fr) <= 0) continue;
                var ptr = NativeLLSM.llsm_container_get(fr.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
                if (ptr == IntPtr.Zero) continue;
                int n = NativeLLSM.llsm_fparray_length(ptr);
                if (n <= 0) continue;
                var a = new float[n];
                Marshal.Copy(ptr, a, 0, n);
                vsArrays[i] = a;
            }

            for (int i = 0; i < nfrm; i++)
            {
                var cur = vsArrays[i];
                if (cur == null) continue;

                // 有声連続区間内に窓をクランプ（V/UV 境界を跨がない）
                int lo = i, hi = i;
                while (lo > i - radius && lo > 0 && vsArrays[lo - 1] != null) lo--;
                while (hi < i + radius && hi < nfrm - 1 && vsArrays[hi + 1] != null) hi++;
                if (hi - lo < 2) continue; // 平滑化に足る近傍がない

                int nhar = cur.Length;
                var smoothed = new float[nhar];
                for (int h = 0; h < nhar; h++)
                {
                    float sx = 0, sy = 0;
                    for (int j = lo; j <= hi; j++)
                    {
                        var nb = vsArrays[j];
                        if (nb == null || h >= nb.Length) continue;
                        sx += MathF.Cos(nb[h]);
                        sy += MathF.Sin(nb[h]);
                    }
                    // ベクトル和が短い（位相が散乱＝実際に変化している）場合は元値を保持
                    if ((sx * sx + sy * sy) <= 0.25f) { smoothed[h] = cur[h]; continue; }
                    float avg = MathF.Atan2(sy, sx);
                    // 補正量がジッタ域を超える＝実変化 → 原値保持
                    float d = avg - cur[h];
                    while (d > MathF.PI) d -= 2 * MathF.PI;
                    while (d < -MathF.PI) d += 2 * MathF.PI;
                    smoothed[h] = MathF.Abs(d) <= PhaseJitterThreshold ? avg : cur[h];
                }

                var frOut = LlsmBindings.Llsm.GetFrame(chunk, i);
                var outPtr = NativeLLSM.llsm_container_get(frOut.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
                if (outPtr != IntPtr.Zero)
                    Marshal.Copy(smoothed, 0, outPtr, Math.Min(nhar, NativeLLSM.llsm_fparray_length(outPtr)));
            }
        }

        /// <summary>
        /// VTMAGN（dB）を有声連続区間内で時間方向に移動平均する。
        /// ノイズフロア帯ビンのフレーム毎の乱れが llsm_harmonic_minphase（全域変換）
        /// を通じて低域の位相ジッタ（＝フレームレートPMサイドバンド）になるのを防ぐ。
        /// 補正量が MagnJitterThreshold を超えるビンは原値を保持（フォルマント遷移・
        /// 子音のスペクトル変化を鈍らせない）。
        /// </summary>
        public static void SmoothVtmagn(ChunkHandle chunk, int nfrm, int radius = 2)
        {
            var arrays = new float[nfrm][];
            for (int i = 0; i < nfrm; i++)
            {
                var fr = LlsmBindings.Llsm.GetFrame(chunk, i);
                if (LlsmBindings.Llsm.GetFrameF0(fr) <= 0) continue;
                float[] v = FrameAccess.ReadVtMagn(fr);
                if (v.Length > 0) arrays[i] = v;
            }

            for (int i = 0; i < nfrm; i++)
            {
                var cur = arrays[i];
                if (cur == null) continue;

                int lo = i, hi = i;
                while (lo > i - radius && lo > 0 && arrays[lo - 1] != null) lo--;
                while (hi < i + radius && hi < nfrm - 1 && arrays[hi + 1] != null) hi++;
                if (hi - lo < 2) continue;

                int n = cur.Length;
                var smoothed = new float[n];
                for (int b = 0; b < n; b++)
                {
                    float sum = 0; int cnt = 0;
                    for (int j = lo; j <= hi; j++)
                    {
                        var nb = arrays[j];
                        if (nb == null || b >= nb.Length) continue;
                        sum += nb[b]; cnt++;
                    }
                    if (cnt == 0) { smoothed[b] = cur[b]; continue; }
                    float avg = sum / cnt;
                    // 補正量がジッタ域を超える＝実変化 → 原値保持
                    smoothed[b] = MathF.Abs(avg - cur[b]) <= MagnJitterThreshold ? avg : cur[b];
                }
                FrameAccess.WriteVtMagn(LlsmBindings.Llsm.GetFrame(chunk, i), smoothed);
            }
        }
    }
}
