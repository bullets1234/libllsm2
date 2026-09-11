using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Diagnostics;
using UtauEngineNg.Llsm;

namespace UtauEngineNg.Synthesis
{
    /// <summary>
    /// 2x オーバーサンプリングで解析した Layer1 チャンクのスペクトルを、合成サンプルレート
    /// （元の Nyquist）へダウンサンプルする。HM 倍音トリム・VTMAGN トリム・PSDRES/NM.PSD の
    /// 下半分リサンプルを行い、conf の nspec/fnyq を更新する。
    /// （UtauEngine DownsampleChunkSpectrum + ResamplePsdLowerHalf の移植）
    /// </summary>
    public static class SpectrumDownsampler
    {
        private const string Stage = "Downsample";

        public static void Apply(ChunkHandle chunk, int originalFs, ILogger log)
        {
            var conf = LlsmBindings.Llsm.GetConf(chunk);
            int analysisNspec = LlsmBindings.Llsm.GetConfInt(conf, NativeLLSM.LLSM_CONF_NSPEC);
            float analysisFnyq = LlsmBindings.Llsm.GetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ);
            int nfrm = LlsmBindings.Llsm.GetNumFrames(chunk);

            float originalFnyq = originalFs / 2.0f;
            int originalNspec = (analysisNspec - 1) / 2 + 1;

            log.Debug(Stage, $"Spectrum nspec {analysisNspec} -> {originalNspec}, fnyq {analysisFnyq:F0} -> {originalFnyq:F0}");

            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                float f0 = LlsmBindings.Llsm.GetFrameF0(frame);

                // HM 倍音トリム（エイリアシング防止）
                var hmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_HM);
                if (hmPtr != IntPtr.Zero && f0 > 0)
                {
                    int analysisNhar = LlsmBindings.Llsm.GetHMNHar(hmPtr);
                    int originalNhar = (int)(originalFnyq / f0);
                    if (originalNhar < analysisNhar && originalNhar > 0)
                    {
                        var newHm = NativeLLSM.llsm_create_hmframe(originalNhar);
                        var newHmStruct = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(newHm);
                        var oldHmStruct = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);

                        float[] ampl = new float[originalNhar];
                        float[] phse = new float[originalNhar];
                        Marshal.Copy(oldHmStruct.ampl, ampl, 0, originalNhar);
                        Marshal.Copy(oldHmStruct.phse, phse, 0, originalNhar);
                        Marshal.Copy(ampl, 0, newHmStruct.ampl, originalNhar);
                        Marshal.Copy(phse, 0, newHmStruct.phse, originalNhar);

                        NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_HM,
                            newHm, NativeCallbacks.DeleteHm, NativeCallbacks.CopyHm);
                    }
                }

                // VTMAGN トリム（下半分のみ）
                var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr != IntPtr.Zero && f0 > 0)
                {
                    // conf NSPEC でなく実配列長でガード（二重適用や nfft 不一致での
                    // ネイティブヒープ越え読み出しを防止）
                    int actualLen = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                    int copyLen = Math.Min(analysisNspec, actualLen);
                    if (copyLen >= originalNspec)
                    {
                        float[] full = new float[copyLen];
                        Marshal.Copy(vtmagnPtr, full, 0, copyLen);
                        float[] trimmed = new float[originalNspec];
                        Array.Copy(full, trimmed, originalNspec);
                        LlsmBindings.Llsm.SetFrameVtMagn(frame, trimmed);
                    }
                }

                // PSDRES リサンプル（下半分を全体へ）
                var psdresPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES);
                if (psdresPtr != IntPtr.Zero)
                {
                    int psdresLen = NativeLLSM.llsm_fparray_length(psdresPtr);
                    if (psdresLen > 0)
                    {
                        float[] full = new float[psdresLen];
                        Marshal.Copy(psdresPtr, full, 0, psdresLen);
                        float[] resampled = ResamplePsdLowerHalf(full, analysisFnyq, originalFnyq);
                        var newArr = NativeLLSM.llsm_create_fparray(resampled.Length);
                        Marshal.Copy(resampled, 0, newArr, resampled.Length);
                        NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES,
                            newArr, LlsmBindings.Llsm.DeleteFpArrayPtr, LlsmBindings.Llsm.CopyFpArrayPtr);
                    }
                }

                // NM.psd リサンプル
                var nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
                if (nmPtr != IntPtr.Zero)
                {
                    var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                    if (nm.npsd > 0 && nm.psd != IntPtr.Zero)
                    {
                        float[] full = new float[nm.npsd];
                        Marshal.Copy(nm.psd, full, 0, nm.npsd);
                        float[] resampled = ResamplePsdLowerHalf(full, analysisFnyq, originalFnyq);
                        Marshal.Copy(resampled, 0, nm.psd, nm.npsd);
                    }
                }
            }

            LlsmBindings.Llsm.SetConfInt(conf, NativeLLSM.LLSM_CONF_NSPEC, originalNspec);
            LlsmBindings.Llsm.SetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ, originalFnyq);
        }

        /// <summary>PSD（dB）配列の下半分 [0, originalFnyq] を Catmull-Rom で全体へ引き伸ばす。</summary>
        private static float[] ResamplePsdLowerHalf(float[] psd, float analysisFnyq, float originalFnyq)
        {
            int n = psd.Length;
            float[] result = new float[n];
            float ratio = originalFnyq / analysisFnyq;  // 2x で 0.5

            for (int j = 0; j < n; j++)
            {
                float srcIdx = j * ratio;
                int i1 = (int)srcIdx;
                float t = srcIdx - i1;
                int i0 = Math.Max(0, i1 - 1);
                int i2 = Math.Min(n - 1, i1 + 1);
                int i3 = Math.Min(n - 1, i1 + 2);

                float p0 = psd[i0], p1 = psd[i1], p2 = psd[i2], p3 = psd[i3];
                float t2 = t * t, t3 = t2 * t;
                result[j] = 0.5f * (
                    2f * p1 +
                    (-p0 + p2) * t +
                    (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                    (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
            }
            return result;
        }
    }
}
