using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Audio;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// ジェンダーファクター（g フラグ）。VTMAGN を周波数軸リサンプリングしてフォルマントを
    /// シフトする。正=男性的（フォルマント低下）、負=女性的（上昇）。無声フレームは不変。
    /// （UtauEngine ApplyGenderFactor の移植）
    /// </summary>
    public sealed class GenderEffect : IFrameEffect
    {
        private readonly int _genderFactor;

        public GenderEffect(int genderFactor) => _genderFactor = genderFactor;

        public bool IsActive => _genderFactor != 0;

        public void Apply(in FrameEffectContext ctx)
        {
            if (!IsActive) return;
            var frame = ctx.Frame;

            if (LlsmBindings.Llsm.GetFrameF0(frame) <= 0) return;

            var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnPtr == IntPtr.Zero) return;

            int vtmagnSize = NativeLLSM.llsm_fparray_length(vtmagnPtr);
            if (vtmagnSize <= 0) return;

            float shiftRatio = MathF.Pow(2.0f, -_genderFactor / 400.0f);

            float[] vtmagn = new float[vtmagnSize];
            Marshal.Copy(vtmagnPtr, vtmagn, 0, vtmagnSize);
            var shifted = Resampling.ResampleArray(vtmagn, shiftRatio);
            Marshal.Copy(shifted, 0, vtmagnPtr, vtmagnSize);
        }
    }
}
