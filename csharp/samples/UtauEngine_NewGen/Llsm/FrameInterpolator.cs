using System;
using System.Runtime.InteropServices;
using LlsmBindings;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// LLSM Layer1 フレームの補間（タイムストレッチの心臓部）。
    /// 2点線形版と4点 Akima 版を提供し、V/UV 遷移では声道スペクトルを等パワー
    /// コサインフェード、位相は円環補間で滑らかに繋ぐ。NM(雑音モデル)は V/UV に
    /// かかわらず常に補間してノイズ特性の急変を防ぐ。
    /// PSDRES は補間せず、呼び出し側でランダム近傍ブレンドする（周期化防止）。
    /// （UtauEngine の InterpolateFrame / InterpolateFrameCubic 他を再構成）
    /// </summary>
    public static partial class FrameInterpolator
    {
        private const float MinVoicedF0 = 50.0f;
        private const float VtFloorDb = -80.0f;

        // ===================================================================
        //  2点線形補間
        // ===================================================================
        /// <summary>frame0→frame1 を ratio∈[0,1] で補間し、新しいフレームポインタを返す。</summary>
        public static IntPtr Interpolate2(ContainerRef frame0, ContainerRef frame1, float ratio, int outFrameIdx = -1)
        {
            float f0_0 = LlsmBindings.Llsm.GetFrameF0(frame0);
            float f0_1 = LlsmBindings.Llsm.GetFrameF0(frame1);
            bool voiced0 = f0_0 >= MinVoicedF0;
            bool voiced1 = f0_1 >= MinVoicedF0;
            bool bothVoiced = voiced0 && voiced1;

            float f0Interp;
            float rdInterp;
            float[]? vtmagnInterp = null;
            float[]? vsphseInterp = null;
            ContainerRef baseFrame;
            float vtmagnFadeDb = 0;

            if (bothVoiced)
            {
                baseFrame = ratio < 0.5f ? frame0 : frame1;
                f0Interp = MathF.Max(MinVoicedF0, f0_0 * (1 - ratio) + f0_1 * ratio);
                rdInterp = Math.Clamp(ReadRd(frame0) * (1 - ratio) + ReadRd(frame1) * ratio, 0.1f, 2.7f);

                vtmagnInterp = InterpVtmagn2(frame0, frame1, ratio);
                vsphseInterp = InterpVsphse2(frame0, frame1, ratio);
            }
            else if (!voiced0 && voiced1)
            {
                baseFrame = frame1;
                f0Interp = f0_1;
                rdInterp = ReadRd(frame1);
                vtmagnFadeDb = FadeInDb(ratio);
            }
            else if (voiced0 && !voiced1)
            {
                baseFrame = frame0;
                f0Interp = f0_0;
                rdInterp = ReadRd(frame0);
                vtmagnFadeDb = FadeInDb(1.0f - ratio);
            }
            else
            {
                // 両方無声: VTMAGN は dB 線形補間（摩擦音の HF ピーク保持）
                baseFrame = ratio < 0.5f ? frame0 : frame1;
                f0Interp = 0;
                rdInterp = 1.0f;
                vtmagnInterp = InterpVtmagnLinearUv(frame0, frame1, ratio);
            }

            IntPtr outPtr = NativeLLSM.llsm_copy_container(baseFrame.Ptr);
            NativeCallbacks.AttachF0(outPtr, f0Interp);
            NativeCallbacks.AttachRd(outPtr, rdInterp);

            ApplyVtmagn(outPtr, vtmagnInterp, vtmagnFadeDb, !voiced0 && !voiced1);
            if (vsphseInterp != null)
                NativeCallbacks.AttachFpArray(outPtr, NativeLLSM.LLSM_FRAME_VSPHSE, vsphseInterp);

            InterpNmLinear(outPtr, frame0, frame1, ratio, outFrameIdx);
            return outPtr;
        }

        // ===================================================================
        //  4点 Akima 補間（frame1→frame2 を補間、frame0/frame3 は制御点）
        // ===================================================================
        public static IntPtr Interpolate4(
            ContainerRef frame0, ContainerRef frame1, ContainerRef frame2, ContainerRef frame3,
            float ratio, int outFrameIdx = -1)
        {
            float f0_0 = LlsmBindings.Llsm.GetFrameF0(frame0);
            float f0_1 = LlsmBindings.Llsm.GetFrameF0(frame1);
            float f0_2 = LlsmBindings.Llsm.GetFrameF0(frame2);
            float f0_3 = LlsmBindings.Llsm.GetFrameF0(frame3);

            bool allVoiced = f0_0 > 0 && f0_1 > 0 && f0_2 > 0 && f0_3 > 0;
            bool bothVoiced = f0_1 > 0 && f0_2 > 0;

            float f0Interp;
            if (allVoiced)
                f0Interp = Math.Clamp(SpectralInterpolation.AkimaInterp(f0_0, f0_1, f0_2, f0_3, ratio), 50, 800);
            else if (bothVoiced)
                f0Interp = f0_1 * (1 - ratio) + f0_2 * ratio;
            else if (f0_2 > 0) f0Interp = f0_2;
            else if (f0_1 > 0) f0Interp = f0_1;
            else f0Interp = 0;

            float rd0 = ReadRd(frame0), rd1 = ReadRd(frame1), rd2 = ReadRd(frame2), rd3 = ReadRd(frame3);
            float rdInterp = Math.Clamp(SpectralInterpolation.AkimaInterp(rd0, rd1, rd2, rd3, ratio), 0.1f, 2.7f);

            bool voiced1 = f0_1 > 0;
            bool voiced2 = f0_2 > 0;

            float[]? vtmagnInterp = (voiced1 && voiced2)
                ? InterpVtmagn4(frame0, frame1, frame2, frame3, ratio)
                : null;
            float[]? vsphseInterp = InterpVsphse4(frame0, frame1, frame2, frame3, ratio);

            // ベースフレーム選択 + V/UV フェード量
            ContainerRef baseFrame;
            float vtmagnFadeDb = 0;
            if (!voiced1 && voiced2)
            {
                baseFrame = frame2;
                vtmagnFadeDb = FadeInDb(ratio);
            }
            else if (voiced1 && !voiced2)
            {
                baseFrame = frame1;
                vtmagnFadeDb = FadeInDb(1.0f - ratio);
            }
            else
            {
                baseFrame = ratio < 0.5f ? frame1 : frame2;
                if (!voiced1 && !voiced2 && vtmagnInterp == null)
                    vtmagnInterp = InterpVtmagnLinearUv(frame1, frame2, ratio);
            }

            IntPtr outPtr = NativeLLSM.llsm_copy_container(baseFrame.Ptr);
            NativeCallbacks.AttachF0(outPtr, f0Interp);
            NativeCallbacks.AttachRd(outPtr, rdInterp);

            ApplyVtmagn(outPtr, vtmagnInterp, vtmagnFadeDb, !voiced1 && !voiced2);

            if (vsphseInterp != null)
            {
                NativeCallbacks.AttachFpArray(outPtr, NativeLLSM.LLSM_FRAME_VSPHSE, vsphseInterp);
            }
            else
            {
                var src = ratio < 0.5f ? frame1 : frame2;
                var srcPtr = NativeLLSM.llsm_container_get(src.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
                if (srcPtr != IntPtr.Zero)
                    NativeCallbacks.AttachFpArrayCopy(outPtr, NativeLLSM.LLSM_FRAME_VSPHSE, srcPtr);
            }

            InterpNmAkima(outPtr, frame0, frame1, frame2, frame3, ratio, outFrameIdx);
            return outPtr;
        }

        // ===================================================================
        //  共通ヘルパ
        // ===================================================================
        private static float ReadRd(ContainerRef frame)
        {
            var p = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_RD);
            return p != IntPtr.Zero ? Marshal.PtrToStructure<float>(p) : 1.0f;
        }

        /// <summary>等パワーコサインフェードの dB 量（progress=0→最大減衰, 1→0dB、下限-24dB）。</summary>
        private static float FadeInDb(float progress)
        {
            float fadeCos = 0.5f * (1.0f - MathF.Cos(MathF.PI * progress));
            float factor = MathF.Sqrt(MathF.Max(1e-8f, fadeCos));
            return Math.Max(-24.0f, 20.0f * MathF.Log10(factor));
        }

        private static float[]? ReadFpArray(ContainerRef frame, int key)
        {
            var ptr = NativeLLSM.llsm_container_get(frame.Ptr, key);
            if (ptr == IntPtr.Zero) return null;
            int n = NativeLLSM.llsm_fparray_length(ptr);
            if (n <= 0) return null;
            var a = new float[n];
            Marshal.Copy(ptr, a, 0, n);
            return a;
        }

        // --- VTMAGN ---
        private static float[]? InterpVtmagn2(ContainerRef f0, ContainerRef f1, float ratio)
        {
            float[]? v0 = ReadFpArray(f0, NativeLLSM.LLSM_FRAME_VTMAGN);
            float[]? v1 = ReadFpArray(f1, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (v0 == null || v1 == null) return null;

            int maxspec = Math.Max(v0.Length, v1.Length);
            float[] cep = SpectralInterpolation.CepstralInterpolateVtmagn(v0, v1, ratio);
            var result = new float[maxspec];
            Array.Copy(cep, result, cep.Length);
            for (int i = cep.Length; i < maxspec; i++)
                result[i] = (i < v1.Length) ? v1[i] : v0[i];
            return result;
        }

        private static float[]? InterpVtmagn4(ContainerRef f0, ContainerRef f1, ContainerRef f2, ContainerRef f3, float ratio)
        {
            float[]? v0 = ReadFpArray(f0, NativeLLSM.LLSM_FRAME_VTMAGN);
            float[]? v1 = ReadFpArray(f1, NativeLLSM.LLSM_FRAME_VTMAGN);
            float[]? v2 = ReadFpArray(f2, NativeLLSM.LLSM_FRAME_VTMAGN);
            float[]? v3 = ReadFpArray(f3, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (v0 == null || v1 == null || v2 == null || v3 == null) return null;

            int maxspec = Math.Max(Math.Max(v0.Length, v1.Length), Math.Max(v2.Length, v3.Length));
            float[] cep = SpectralInterpolation.CepstralInterpolateVtmagnCubic(v0, v1, v2, v3, ratio);
            var result = new float[maxspec];
            Array.Copy(cep, result, cep.Length);
            for (int i = cep.Length; i < maxspec; i++)
            {
                float val = VtFloorDb;
                if (i < v1.Length) val = v1[i];
                else if (i < v2.Length) val = v2[i];
                else if (i < v3.Length) val = v3[i];
                else if (i < v0.Length) val = v0[i];
                result[i] = val;
            }
            for (int i = 0; i < maxspec; i++)
                result[i] = Math.Max(VtFloorDb, result[i]);
            return result;
        }

        private static float[]? InterpVtmagnLinearUv(ContainerRef f0, ContainerRef f1, float ratio)
        {
            float[]? v0 = ReadFpArray(f0, NativeLLSM.LLSM_FRAME_VTMAGN);
            float[]? v1 = ReadFpArray(f1, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (v0 == null || v1 == null) return null;

            int maxspec = Math.Max(v0.Length, v1.Length);
            var result = new float[maxspec];
            for (int i = 0; i < maxspec; i++)
            {
                float a = (i < v0.Length) ? v0[i] : v1[Math.Min(i, v1.Length - 1)];
                float b = (i < v1.Length) ? v1[i] : v0[Math.Min(i, v0.Length - 1)];
                result[i] = Math.Max(VtFloorDb, a * (1 - ratio) + b * ratio);
            }
            return result;
        }

        /// <summary>補間済み VTMAGN・フェード・フロアクランプを出力フレームへ適用する。</summary>
        private static void ApplyVtmagn(IntPtr outPtr, float[]? vtmagnInterp, float vtmagnFadeDb, bool bothUnvoiced)
        {
            if (vtmagnInterp != null)
            {
                for (int i = 0; i < vtmagnInterp.Length; i++)
                    vtmagnInterp[i] = Math.Max(VtFloorDb, vtmagnInterp[i]);
                NativeCallbacks.AttachFpArray(outPtr, NativeLLSM.LLSM_FRAME_VTMAGN, vtmagnInterp);
            }
            else if (vtmagnFadeDb != 0)
            {
                var src = NativeLLSM.llsm_container_get(outPtr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (src != IntPtr.Zero)
                {
                    int nspec = NativeLLSM.llsm_fparray_length(src);
                    var v = new float[nspec];
                    Marshal.Copy(src, v, 0, nspec);
                    for (int i = 0; i < nspec; i++) v[i] = Math.Max(VtFloorDb, v[i] + vtmagnFadeDb);
                    Marshal.Copy(v, 0, src, nspec);
                }
            }
            else if (bothUnvoiced)
            {
                var src = NativeLLSM.llsm_container_get(outPtr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (src != IntPtr.Zero)
                {
                    int nspec = NativeLLSM.llsm_fparray_length(src);
                    var v = new float[nspec];
                    Marshal.Copy(src, v, 0, nspec);
                    for (int i = 0; i < nspec; i++) v[i] = Math.Max(VtFloorDb, v[i]);
                    Marshal.Copy(v, 0, src, nspec);
                }
            }
        }

        // --- VSPHSE ---
        private static float[]? InterpVsphse2(ContainerRef f0, ContainerRef f1, float ratio)
        {
            float[]? p0 = ReadFpArray(f0, NativeLLSM.LLSM_FRAME_VSPHSE);
            float[]? p1 = ReadFpArray(f1, NativeLLSM.LLSM_FRAME_VSPHSE);
            if (p0 == null || p1 == null) return null;

            int minnhar = Math.Min(p0.Length, p1.Length);
            int maxnhar = Math.Max(p0.Length, p1.Length);
            var result = new float[maxnhar];
            for (int i = 0; i < minnhar; i++)
                result[i] = SpectralInterpolation.CircularInterpolatePhase(p0[i], p1[i], ratio);
            for (int i = minnhar; i < maxnhar; i++)
                result[i] = (i < p1.Length) ? p1[i] : p0[i];
            return result;
        }

        private static float[]? InterpVsphse4(ContainerRef f0, ContainerRef f1, ContainerRef f2, ContainerRef f3, float ratio)
        {
            float[]? p1 = ReadFpArray(f1, NativeLLSM.LLSM_FRAME_VSPHSE);
            float[]? p2 = ReadFpArray(f2, NativeLLSM.LLSM_FRAME_VSPHSE);
            if (p1 == null || p2 == null) return null;

            float[]? p0 = ReadFpArray(f0, NativeLLSM.LLSM_FRAME_VSPHSE);
            float[]? p3 = ReadFpArray(f3, NativeLLSM.LLSM_FRAME_VSPHSE);
            bool usePhaseAkima = p0 != null && p3 != null;

            int minnhar = Math.Min(p1.Length, p2.Length);
            int maxnhar = Math.Max(p1.Length, p2.Length);
            int akimaNhar = usePhaseAkima
                ? Math.Min(Math.Min(p0!.Length, p1.Length), Math.Min(p2.Length, p3!.Length))
                : 0;

            var result = new float[maxnhar];
            for (int i = 0; i < minnhar; i++)
            {
                if (i < akimaNhar)
                {
                    float a0 = p0![i], a1 = p1[i], a2 = p2[i], a3 = p3![i];
                    float d01 = a1 - a0; d01 -= 2f * MathF.PI * MathF.Round(d01 / (2f * MathF.PI));
                    float u1 = a0 + d01;
                    float d12 = a2 - u1; d12 -= 2f * MathF.PI * MathF.Round(d12 / (2f * MathF.PI));
                    float u2 = u1 + d12;
                    float d23 = a3 - u2; d23 -= 2f * MathF.PI * MathF.Round(d23 / (2f * MathF.PI));
                    float u3 = u2 + d23;
                    float r = SpectralInterpolation.AkimaInterp(a0, u1, u2, u3, ratio);
                    if (float.IsNaN(r) || float.IsInfinity(r))
                        result[i] = SpectralInterpolation.CircularInterpolatePhase(p1[i], p2[i], ratio);
                    else
                    {
                        r -= 2f * MathF.PI * MathF.Floor((r + MathF.PI) / (2f * MathF.PI));
                        result[i] = r;
                    }
                }
                else
                {
                    result[i] = SpectralInterpolation.CircularInterpolatePhase(p1[i], p2[i], ratio);
                }
            }
            for (int i = minnhar; i < maxnhar; i++)
                result[i] = (i < p2.Length) ? p2[i] : (i < p1.Length ? p1[i] : 0f);
            return result;
        }
    }
}
