using System;
using System.Runtime.InteropServices;
using LlsmBindings;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// <see cref="FrameInterpolator"/> の雑音モデル(NM)補間と PSDRES／VSPHSE 補助処理。
    /// PSD・edc・eenv を線形／Akima 補間し、PSDRES は周期化防止のためランダム近傍ブレンドする。
    /// </summary>
    public static partial class FrameInterpolator
    {
        // ===================================================================
        //  NM 線形補間（2点）
        // ===================================================================
        private static void InterpNmLinear(IntPtr outPtr, ContainerRef frame0, ContainerRef frame1, float ratio, int outFrameIdx)
        {
            var nm0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_NM);
            var nm1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_NM);

            IntPtr nmInterpPtr = IntPtr.Zero;

            if (nm0Ptr != IntPtr.Zero && nm1Ptr != IntPtr.Zero)
            {
                var nm0 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm0Ptr);
                var nm1 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm1Ptr);

                if (nm0.npsd == nm1.npsd && nm0.nchannel == nm1.nchannel)
                {
                    nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm0Ptr);
                    var nmInterp = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmInterpPtr);

                    // PSD 線形補間
                    float[] psd0 = Copy(nm0.psd, nm0.npsd);
                    float[] psd1 = Copy(nm1.psd, nm1.npsd);
                    var psd = new float[nm0.npsd];
                    for (int p = 0; p < nm0.npsd; p++)
                        psd[p] = psd0[p] * (1 - ratio) + psd1[p] * ratio;
                    if (outFrameIdx >= 0) ApplySmoothNmPsdVariation(psd, nm0.npsd, outFrameIdx);
                    Marshal.Copy(psd, 0, nmInterp.psd, nm0.npsd);

                    // EDC 線形補間
                    if (nm0.edc != IntPtr.Zero && nm1.edc != IntPtr.Zero && nm0.nchannel > 0)
                    {
                        float[] edc0 = Copy(nm0.edc, nm0.nchannel);
                        float[] edc1 = Copy(nm1.edc, nm1.nchannel);
                        var edc = new float[nm0.nchannel];
                        for (int c = 0; c < nm0.nchannel; c++)
                            edc[c] = edc0[c] * (1 - ratio) + edc1[c] * ratio;
                        Marshal.Copy(edc, 0, nmInterp.edc, nm0.nchannel);
                    }

                    // eenv 線形補間（振幅=線形, 位相=円環）
                    if (nm0.eenv != IntPtr.Zero && nm1.eenv != IntPtr.Zero && nm0.nchannel > 0)
                        InterpEenv(nmInterp, nm0, nm1, ratio, akima: false, default, default);
                }
            }
            else if (nm0Ptr != IntPtr.Zero) nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm0Ptr);
            else if (nm1Ptr != IntPtr.Zero) nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm1Ptr);

            if (nmInterpPtr != IntPtr.Zero) NativeCallbacks.AttachNm(outPtr, nmInterpPtr);
        }

        // ===================================================================
        //  NM Akima 補間（4点、frame1↔frame2 がメイン）
        // ===================================================================
        private static void InterpNmAkima(IntPtr outPtr,
            ContainerRef frame0, ContainerRef frame1, ContainerRef frame2, ContainerRef frame3,
            float ratio, int outFrameIdx)
        {
            var nm0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_NM);
            var nm1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_NM);
            var nm2Ptr = NativeLLSM.llsm_container_get(frame2.Ptr, NativeLLSM.LLSM_FRAME_NM);
            var nm3Ptr = NativeLLSM.llsm_container_get(frame3.Ptr, NativeLLSM.LLSM_FRAME_NM);

            IntPtr nmInterpPtr = IntPtr.Zero;

            if (nm1Ptr != IntPtr.Zero && nm2Ptr != IntPtr.Zero)
            {
                var nm1 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm1Ptr);
                var nm2 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm2Ptr);

                // frame0/frame3 が揃い形状一致なら Akima
                NativeLLSM.llsm_nmframe nm0 = default, nm3 = default;
                bool useAkima = false;
                if (nm0Ptr != IntPtr.Zero && nm3Ptr != IntPtr.Zero)
                {
                    var nm0v = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm0Ptr);
                    var nm3v = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm3Ptr);
                    if (nm0v.npsd == nm1.npsd && nm3v.npsd == nm1.npsd &&
                        nm0v.nchannel == nm1.nchannel && nm3v.nchannel == nm1.nchannel)
                    {
                        nm0 = nm0v; nm3 = nm3v; useAkima = true;
                    }
                }

                if (nm1.npsd == nm2.npsd && nm1.nchannel == nm2.nchannel)
                {
                    nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm1Ptr);
                    var nmInterp = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmInterpPtr);

                    // PSD
                    float[] psd1 = Copy(nm1.psd, nm1.npsd);
                    float[] psd2 = Copy(nm2.psd, nm2.npsd);
                    float[]? psd0 = useAkima ? Copy(nm0.psd, nm1.npsd) : null;
                    float[]? psd3 = useAkima ? Copy(nm3.psd, nm1.npsd) : null;
                    var psd = new float[nm1.npsd];
                    for (int p = 0; p < nm1.npsd; p++)
                    {
                        if (useAkima)
                        {
                            float v = SpectralInterpolation.AkimaInterp(psd0![p], psd1[p], psd2[p], psd3![p], ratio);
                            psd[p] = (float.IsNaN(v) || float.IsInfinity(v)) ? psd1[p] * (1 - ratio) + psd2[p] * ratio : v;
                        }
                        else psd[p] = psd1[p] * (1 - ratio) + psd2[p] * ratio;
                    }
                    if (outFrameIdx >= 0) ApplySmoothNmPsdVariation(psd, nm1.npsd, outFrameIdx);
                    Marshal.Copy(psd, 0, nmInterp.psd, nm1.npsd);

                    // EDC
                    if (nm1.edc != IntPtr.Zero && nm2.edc != IntPtr.Zero && nm1.nchannel > 0)
                    {
                        float[] edc1 = Copy(nm1.edc, nm1.nchannel);
                        float[] edc2 = Copy(nm2.edc, nm2.nchannel);
                        bool edcAkima = useAkima && nm0.edc != IntPtr.Zero && nm3.edc != IntPtr.Zero;
                        float[]? edc0 = edcAkima ? Copy(nm0.edc, nm1.nchannel) : null;
                        float[]? edc3 = edcAkima ? Copy(nm3.edc, nm1.nchannel) : null;
                        var edc = new float[nm1.nchannel];
                        for (int c = 0; c < nm1.nchannel; c++)
                        {
                            if (edc0 != null)
                            {
                                float v = SpectralInterpolation.AkimaInterp(edc0[c], edc1[c], edc2[c], edc3![c], ratio);
                                edc[c] = (float.IsNaN(v) || float.IsInfinity(v)) ? edc1[c] * (1 - ratio) + edc2[c] * ratio : v;
                            }
                            else edc[c] = edc1[c] * (1 - ratio) + edc2[c] * ratio;
                        }
                        Marshal.Copy(edc, 0, nmInterp.edc, nm1.nchannel);
                    }

                    // eenv
                    if (nm1.eenv != IntPtr.Zero && nm2.eenv != IntPtr.Zero && nm1.nchannel > 0)
                    {
                        bool eenvAkima = useAkima && nm0.eenv != IntPtr.Zero && nm3.eenv != IntPtr.Zero;
                        InterpEenv(nmInterp, nm1, nm2, ratio, eenvAkima, nm0, nm3);
                    }
                }
            }
            else if (nm1Ptr != IntPtr.Zero) nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm1Ptr);
            else if (nm2Ptr != IntPtr.Zero) nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm2Ptr);

            if (nmInterpPtr != IntPtr.Zero) NativeCallbacks.AttachNm(outPtr, nmInterpPtr);
        }

        /// <summary>
        /// eenv（チャンネル毎の調波包絡）を補間する。
        /// 振幅は線形 or 4点 Akima、位相は常に円環補間。nhar 不一致時は hmframe を再生成。
        /// nmA/nmB が主、akima 有効時のみ nmPrev/nmNext を制御点に使う。
        /// </summary>
        private static void InterpEenv(
            NativeLLSM.llsm_nmframe nmInterp, NativeLLSM.llsm_nmframe nmA, NativeLLSM.llsm_nmframe nmB,
            float ratio, bool akima, NativeLLSM.llsm_nmframe nmPrev, NativeLLSM.llsm_nmframe nmNext)
        {
            IntPtr[] eenvA = ReadPtrs(nmA.eenv, nmA.nchannel);
            IntPtr[] eenvB = ReadPtrs(nmB.eenv, nmB.nchannel);
            IntPtr[] eenvInterp = ReadPtrs(nmInterp.eenv, nmInterp.nchannel);

            IntPtr[]? eenvPrev = akima ? ReadPtrs(nmPrev.eenv, nmA.nchannel) : null;
            IntPtr[]? eenvNext = akima ? ReadPtrs(nmNext.eenv, nmA.nchannel) : null;

            for (int ch = 0; ch < nmA.nchannel; ch++)
            {
                if (eenvA[ch] == IntPtr.Zero || eenvB[ch] == IntPtr.Zero) continue;

                var ea = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvA[ch]);
                var eb = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvB[ch]);
                var ei = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvInterp[ch]);

                bool chAkima = false;
                NativeLLSM.llsm_hmframe ep = default, en = default;
                if (akima && eenvPrev![ch] != IntPtr.Zero && eenvNext![ch] != IntPtr.Zero)
                {
                    ep = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvPrev[ch]);
                    en = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvNext[ch]);
                    chAkima = true;
                }

                int minnhar = Math.Min(ea.nhar, eb.nhar);
                int maxnhar = Math.Max(ea.nhar, eb.nhar);
                int akimaNhar = chAkima ? Math.Min(Math.Min(ep.nhar, ea.nhar), Math.Min(eb.nhar, en.nhar)) : 0;
                if (maxnhar <= 0) continue;

                float[] amplA = Copy(ea.ampl, ea.nhar);
                float[] amplB = Copy(eb.ampl, eb.nhar);
                float[] phseA = Copy(ea.phse, ea.nhar);
                float[] phseB = Copy(eb.phse, eb.nhar);
                float[]? amplP = chAkima ? Copy(ep.ampl, ep.nhar) : null;
                float[]? amplN = chAkima ? Copy(en.ampl, en.nhar) : null;

                var ampl = new float[maxnhar];
                var phse = new float[maxnhar];
                for (int i = 0; i < minnhar; i++)
                {
                    if (i < akimaNhar)
                    {
                        float v = SpectralInterpolation.AkimaInterp(amplP![i], amplA[i], amplB[i], amplN![i], ratio);
                        ampl[i] = (float.IsNaN(v) || float.IsInfinity(v)) ? amplA[i] * (1 - ratio) + amplB[i] * ratio : v;
                    }
                    else ampl[i] = amplA[i] * (1 - ratio) + amplB[i] * ratio;
                    phse[i] = SpectralInterpolation.CircularInterpolatePhase(phseA[i], phseB[i], ratio);
                }
                for (int i = minnhar; i < maxnhar; i++)
                {
                    ampl[i] = (i < ea.nhar) ? amplA[i] : amplB[i];
                    phse[i] = (i < ea.nhar) ? phseA[i] : phseB[i];
                }

                if (ei.nhar != maxnhar)
                {
                    var newEenv = NativeLLSM.llsm_create_hmframe(maxnhar);
                    var ns = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(newEenv);
                    Marshal.Copy(ampl, 0, ns.ampl, maxnhar);
                    Marshal.Copy(phse, 0, ns.phse, maxnhar);
                    NativeLLSM.llsm_delete_hmframe(eenvInterp[ch]);
                    eenvInterp[ch] = newEenv;
                }
                else
                {
                    Marshal.Copy(ampl, 0, ei.ampl, maxnhar);
                    Marshal.Copy(phse, 0, ei.phse, maxnhar);
                }
            }

            Marshal.Copy(eenvInterp, 0, nmInterp.eenv, nmInterp.nchannel);
        }

        // ===================================================================
        //  PSDRES ランダム近傍ブレンド
        // ===================================================================
        /// <summary>
        /// PSDRES（残差ノイズスペクトル）を voicing 一致する近傍フレームと加重ブレンドする。
        /// 同一フレームの長時間反復で生じる「一定間隔のザラつき」を抑える。
        /// </summary>
        public static void AttachRandomNearbyPsdres(
            IntPtr dstFramePtr, ChunkHandle srcChunk, int baseIdx, int srcNfrm, float blendRatio = 0.35f)
        {
            if (blendRatio <= 0) return;

            var basePsdresPtr = NativeLLSM.llsm_container_get(dstFramePtr, NativeLLSM.LLSM_FRAME_PSDRES);
            if (basePsdresPtr == IntPtr.Zero) return;
            int psdLen = NativeLLSM.llsm_fparray_length(basePsdresPtr);
            if (psdLen <= 0) return;

            float[] basePsdres = new float[psdLen];
            Marshal.Copy(basePsdresPtr, basePsdres, 0, psdLen);

            float baseF0 = ReadF0(dstFramePtr);
            bool baseVoiced = baseF0 > 0;

            int offset = (Random.Shared.Next(2) == 0) ? -1 : 1;
            int residx = Math.Clamp(baseIdx + offset, 0, srcNfrm - 1);
            if (residx != baseIdx)
            {
                bool candVoiced = LlsmBindings.Llsm.GetFrameF0(LlsmBindings.Llsm.GetFrame(srcChunk, residx)) > 0;
                if (candVoiced != baseVoiced)
                {
                    int altIdx = Math.Clamp(baseIdx - offset, 0, srcNfrm - 1);
                    if (altIdx != baseIdx)
                    {
                        bool altVoiced = LlsmBindings.Llsm.GetFrameF0(LlsmBindings.Llsm.GetFrame(srcChunk, altIdx)) > 0;
                        if (altVoiced == baseVoiced) residx = altIdx;
                        else return;
                    }
                    else return;
                }
            }
            if (residx == baseIdx) return;

            var resFrame = LlsmBindings.Llsm.GetFrame(srcChunk, residx);
            var resPsdresPtr = NativeLLSM.llsm_container_get(resFrame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES);
            if (resPsdresPtr == IntPtr.Zero) return;

            int resLen = NativeLLSM.llsm_fparray_length(resPsdresPtr);
            int copyLen = Math.Min(psdLen, resLen);
            float[] resPsdres = new float[resLen];
            Marshal.Copy(resPsdresPtr, resPsdres, 0, resLen);
            for (int j = 0; j < copyLen; j++)
                basePsdres[j] = basePsdres[j] * (1.0f - blendRatio) + resPsdres[j] * blendRatio;
            Marshal.Copy(basePsdres, 0, basePsdresPtr, copyLen);
        }

        // ===================================================================
        //  VSPHSE ピッチシフト拡張
        // ===================================================================
        /// <summary>
        /// ピッチ上昇で Layer0 変換時に倍音が切り捨てられるのを防ぐため、VSPHSE を
        /// 必要倍音数まで拡張し、高次位相を線形外挿で補う。
        /// </summary>
        public static void ExtendVsphseForPitchShift(IntPtr framePtr, float originalF0, float newF0, int fs)
        {
            var vsphsePtr = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_VSPHSE);
            if (vsphsePtr == IntPtr.Zero) return;
            int currentNhar = NativeLLSM.llsm_fparray_length(vsphsePtr);
            if (currentNhar < 4) return;

            float fnyq = fs / 2.0f;
            int targetNhar = (int)(fnyq / newF0);
            int neededNhar = (int)(fnyq / originalF0);
            int extendedNhar = Math.Min(targetNhar, Math.Max(currentNhar, neededNhar));
            if (extendedNhar <= currentNhar) return;

            float[] vsphse = new float[currentNhar];
            Marshal.Copy(vsphsePtr, vsphse, 0, currentNhar);

            var extended = new float[extendedNhar];
            Array.Copy(vsphse, extended, currentNhar);

            float d1 = vsphse[currentNhar - 1] - vsphse[currentNhar - 2];
            d1 -= 2f * MathF.PI * MathF.Round(d1 / (2f * MathF.PI));
            float d2 = vsphse[currentNhar - 2] - vsphse[currentNhar - 3];
            d2 -= 2f * MathF.PI * MathF.Round(d2 / (2f * MathF.PI));
            float avgDiff = (d1 + d2) * 0.5f;

            for (int k = currentNhar; k < extendedNhar; k++)
            {
                float e = vsphse[currentNhar - 1] + avgDiff * (k - currentNhar + 1);
                e -= 2f * MathF.PI * MathF.Floor((e + MathF.PI) / (2f * MathF.PI));
                extended[k] = e;
            }

            NativeCallbacks.AttachFpArray(framePtr, NativeLLSM.LLSM_FRAME_VSPHSE, extended);
        }

        // ===================================================================
        //  スムーズ NM PSD バリュー・ノイズ
        // ===================================================================
        /// <summary>NM PSD に時間連続な ±0.5dB のバリューノイズを加える（同一フレーム反復のFFT周期化緩和）。</summary>
        public static void ApplySmoothNmPsdVariation(float[] psd, int npsd, int outFrameIdx)
        {
            const int knotInterval = 8;
            const float amplitude = 0.5f;

            int knotIdx = outFrameIdx / knotInterval;
            float t = (float)(outFrameIdx % knotInterval) / knotInterval;
            float blend = (1f - MathF.Cos(t * MathF.PI)) * 0.5f;

            for (int p = 0; p < npsd; p++)
            {
                float n0 = HashNoise(knotIdx, p);
                float n1 = HashNoise(knotIdx + 1, p);
                psd[p] += (n0 * (1f - blend) + n1 * blend) * amplitude;
            }
        }

        private static float HashNoise(int frameIdx, int binIdx) => DeterministicNoise.Hash(frameIdx, binIdx);

        // --- 小ヘルパ ---
        private static float[] Copy(IntPtr src, int n)
        {
            var a = new float[n];
            if (src != IntPtr.Zero && n > 0) Marshal.Copy(src, a, 0, n);
            return a;
        }

        private static IntPtr[] ReadPtrs(IntPtr src, int n)
        {
            var a = new IntPtr[n];
            if (src != IntPtr.Zero && n > 0) Marshal.Copy(src, a, 0, n);
            return a;
        }

        private static float ReadF0(IntPtr framePtr)
        {
            var p = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_F0);
            return p != IntPtr.Zero ? Marshal.PtrToStructure<float>(p) : 0f;
        }
    }
}
