using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Audio;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// フォルマント追従率（F フラグ）。ピッチ変化に対してフォルマントをどれだけ追従させるかを
    /// 制御する。F0=完全固定（声質保持）、F100=完全追従（処理なし）。VTMAGN を逆方向に
    /// シフトしてピッチによるフォルマント移動を相殺する。
    /// （UtauEngine ApplyAdaptiveFormantToFparray の移植）
    /// </summary>
    public sealed class FormantFollowEffect : IFrameEffect
    {
        private readonly int _formantFollow;

        public FormantFollowEffect(int formantFollow) => _formantFollow = formantFollow;

        public bool IsActive => _formantFollow != 100;

        public void Apply(in FrameEffectContext ctx)
        {
            if (!IsActive) return;
            var frame = ctx.Frame;
            float pitchRatio = ctx.PitchRatio;

            if (Math.Abs(pitchRatio - 1.0f) < 0.05f) return;
            if (LlsmBindings.Llsm.GetFrameF0(frame) <= 0) return;

            float followRatio = _formantFollow / 100.0f;
            float targetFormantRatio = 1.0f + (pitchRatio - 1.0f) * followRatio;
            float formantShiftRatio = pitchRatio / targetFormantRatio;

            var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnPtr == IntPtr.Zero) return;

            int vtmagnSize = NativeLLSM.llsm_fparray_length(vtmagnPtr);
            if (vtmagnSize <= 0 || vtmagnSize > 10000) return;

            float[] magnitudes = new float[vtmagnSize];
            Marshal.Copy(vtmagnPtr, magnitudes, 0, vtmagnSize);
            var shifted = Resampling.ResampleArray(magnitudes, formantShiftRatio);
            Marshal.Copy(shifted, 0, vtmagnPtr, vtmagnSize);
        }
    }
}
