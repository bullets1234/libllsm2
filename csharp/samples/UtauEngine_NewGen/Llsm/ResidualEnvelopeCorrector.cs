using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// Layer1 包絡フィット（調波振幅→VTMAGN(声道スペクトル)+LF声門モデルへの分解→
    /// Layer0 再構成）で失われる高域倍音振幅を、残差包絡（dB, 周波数関数）として測定し
    /// VTMAGN へ加算することで補償する。
    /// 実測（diag/codec_ceiling.ps1）: 2k-16kHz帯で -3〜-7dB の欠損が確認されている。
    /// </summary>
    public static class ResidualEnvelopeCorrector
    {
        private const string Stage = "Residual";
        private const float ResidualClampDb = 12f;
        private const float SilenceFloorDb = -80f;

        /// <summary>
        /// Layer1 変換前の HM（調波モデル）振幅をフレーム毎にスナップショットする。
        /// 無声フレーム（f0&lt;=0）または HM 不在フレームのエントリは null。
        /// </summary>
        /// <param name="chunk">解析済みチャンク（HM が存在する状態、Layer1変換前）</param>
        /// <param name="nfrm">フレーム数</param>
        /// <param name="f0s">各フレームの f0（無声/スキップ時は 0）を受け取る out 配列</param>
        /// <returns>フレーム毎の調波振幅配列（無効フレームは null）</returns>
        public static float[][] Snapshot(ChunkHandle chunk, int nfrm, out float[] f0s)
        {
            var ampls = new float[nfrm][];
            f0s = new float[nfrm];

            for (int i = 0; i < nfrm; i++)
            {
                var fr = LlsmBindings.Llsm.GetFrame(chunk, i);
                float f0 = LlsmBindings.Llsm.GetFrameF0(fr);
                if (f0 <= 0) continue;

                IntPtr hmPtr = NativeLLSM.llsm_container_get(fr.Ptr, NativeLLSM.LLSM_FRAME_HM);
                if (hmPtr == IntPtr.Zero) continue;

                var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
                if (hm.nhar <= 0 || hm.ampl == IntPtr.Zero) continue;

                var ampl = new float[hm.nhar];
                Marshal.Copy(hm.ampl, ampl, 0, hm.nhar);

                ampls[i] = ampl;
                f0s[i] = f0;
            }

            return ampls;
        }

        /// <summary>
        /// Layer1 変換＋ダウンサンプル＋位相伝播＋平滑化が完了した状態のチャンクに対し、
        /// スナップショットとの残差（dB）を各フレームの VTMAGN へ加算する。
        /// </summary>
        /// <param name="chunk">Layer1 変換後（最終状態）のチャンク</param>
        /// <param name="nfrm">フレーム数</param>
        /// <param name="snapshots">Snapshot で取得した元振幅（フレーム毎、null 可）</param>
        /// <param name="f0s">Snapshot で取得した f0</param>
        /// <param name="log">ログ出力先</param>
        public static void Apply(ChunkHandle chunk, int nfrm, float[][] snapshots, float[] f0s, ILogger log)
        {
            var conf = LlsmBindings.Llsm.GetConf(chunk);
            float fnyq = LlsmBindings.Llsm.GetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ);

            int correctedFrames = 0;
            double absResidualSum = 0;
            int absResidualCount = 0;

            var freqs = new List<float>();
            var residuals = new List<float>();

            for (int i = 0; i < nfrm; i++)
            {
                var origAmpl = snapshots[i];
                if (origAmpl == null) continue;
                float f0 = f0s[i];
                if (f0 <= 0) continue;

                var fr = LlsmBindings.Llsm.GetFrame(chunk, i);

                // ディープコピー上で Layer0 復元を行い、元フレームは絶対に変更しない。
                // llsm_frame_tolayer0 は Layer1 パラメータから HM を再計算してコピーへ
                // アタッチするため、コピーはディープコピーである必要がある
                // （llsm_copy_container は UtauEngine/Program.cs で同様に使われている実績あり）。
                IntPtr copyPtr = NativeLLSM.llsm_copy_container(fr.Ptr);
                if (copyPtr == IntPtr.Zero) continue;

                float[] reconAmpl;
                try
                {
                    NativeLLSM.llsm_frame_tolayer0(copyPtr, conf.Ptr);

                    IntPtr hmPtr = NativeLLSM.llsm_container_get(copyPtr, NativeLLSM.LLSM_FRAME_HM);
                    if (hmPtr == IntPtr.Zero) continue;

                    var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
                    if (hm.nhar <= 0 || hm.ampl == IntPtr.Zero) continue;

                    reconAmpl = new float[hm.nhar];
                    Marshal.Copy(hm.ampl, reconAmpl, 0, hm.nhar);
                }
                finally
                {
                    // コピーしたコンテナ（フレーム）を解放。元フレームには一切触れない。
                    NativeLLSM.llsm_delete_container(copyPtr);
                }

                int nharMin = Math.Min(origAmpl.Length, reconAmpl.Length);

                freqs.Clear();
                residuals.Clear();

                for (int h = 0; h < nharMin; h++)
                {
                    float freq = (h + 1) * f0;
                    if (freq >= fnyq) break;

                    float origDb = 20f * MathF.Log10(Math.Max(origAmpl[h], 1e-8f));
                    float reconDb = 20f * MathF.Log10(Math.Max(reconAmpl[h], 1e-8f));

                    float residualDb = origDb < SilenceFloorDb
                        ? 0f
                        : Math.Clamp(origDb - reconDb, -ResidualClampDb, ResidualClampDb);

                    freqs.Add(freq);
                    residuals.Add(residualDb);
                }

                if (freqs.Count < 2) continue;

                float[] vtmagn = FrameAccess.ReadVtMagn(fr);
                int nspec = vtmagn.Length;
                if (nspec < 2) continue;

                float lastFreq = freqs[^1];
                float lastResidual = residuals[^1];

                for (int b = 0; b < nspec; b++)
                {
                    // VTMAGN のビン→周波数変換は SpectrumDownsampler / MgcToLlsm と同じ
                    // freq = b * fnyq / (nspec - 1) の規約に従う。
                    float freq = b * fnyq / (nspec - 1);
                    float residualDb = InterpolateResidual(freqs, residuals, freq, lastFreq, lastResidual);
                    vtmagn[b] += residualDb;

                    absResidualSum += Math.Abs(residualDb);
                    absResidualCount++;
                }

                FrameAccess.WriteVtMagn(fr, vtmagn);
                correctedFrames++;
            }

            if (correctedFrames > 0)
            {
                double meanAbsResidual = absResidualCount > 0 ? absResidualSum / absResidualCount : 0;
                log.Info(Stage, $"Applied residual envelope correction to {correctedFrames}/{nfrm} frames, mean|residual|={meanAbsResidual:F2}dB");
            }
        }

        /// <summary>
        /// 倍音の (freq, residualDb) 点列から、任意周波数における残差(dB)を補間する。
        /// 最初の倍音未満は最初の点の値で保持し、最後の倍音より上は1オクターブ(freq*2)
        /// かけて線形に 0 へフェードし、それ以上は 0 とする。
        /// </summary>
        private static float InterpolateResidual(List<float> freqs, List<float> residuals, float freq, float lastFreq, float lastResidual)
        {
            if (freq <= freqs[0]) return residuals[0];

            if (freq > lastFreq)
            {
                float fadeEnd = lastFreq * 2f;
                if (freq >= fadeEnd) return 0f;
                float t = (freq - lastFreq) / (fadeEnd - lastFreq);
                return lastResidual * (1f - t);
            }

            for (int k = 1; k < freqs.Count; k++)
            {
                if (freq <= freqs[k])
                {
                    float f0v = freqs[k - 1], f1v = freqs[k];
                    float r0 = residuals[k - 1], r1 = residuals[k];
                    float t = (freq - f0v) / (f1v - f0v);
                    return r0 + (r1 - r0) * t;
                }
            }

            return lastResidual;
        }
    }
}
