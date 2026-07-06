using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Diagnostics;
using UtauEngineNg.Llsm;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// グロウル効果（G フラグ）。Pulse-by-Pulse 合成で声帯の不規則振動を再現する。
    /// サブハーモニクス（F0/3・F0/5）、ガウシアンジッター、声門波形変調を有声フレームに付与。
    /// Layer1 状態で適用するため、有効時は Layer0 変換をスキップする必要がある。
    /// （UtauEngine GrowlEffectState + GrowlEffectCallback の移植）
    /// </summary>
    public sealed class GrowlEffect
    {
        private const string Stage = "Growl";
        private readonly int _strength;
        private readonly ILogger _log;

        // GC 保護（合成完了まで保持）
        private NativeLLSM.llsm_fgfm? _callback;
        private GCHandle _stateHandle;

        public GrowlEffect(int strength, ILogger log)
        {
            _strength = strength;
            _log = log;
        }

        /// <summary>有効なら true（有効時は Layer1 合成が必要）。</summary>
        public bool IsActive => _strength > 0;

        /// <summary>
        /// dstChunk の有声フレームへ PBP 効果を設定し、位相伝搬を実行する。
        /// 呼び出し側は本メソッド戻り後、Layer0 変換をスキップして Layer1 のまま合成すること。
        /// </summary>
        public void Apply(ChunkHandle dstChunk, int dstNfrm)
        {
            if (!IsActive) return;

            var growlState = new GrowlEffectState(_strength);
            _stateHandle = GCHandle.Alloc(growlState);
            IntPtr statePtr = GCHandle.ToIntPtr(_stateHandle);

            _callback = Callback;
            IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(_callback);

            int applied = 0;
            for (int i = 0; i < dstNfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(dstChunk, i);
                if (LlsmBindings.Llsm.GetFrameF0(frame) <= 0) continue; // 有声のみ

                // HM を NULL 化（Layer1 から直接合成）
                NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_HM, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                // PBPSYN 有効化
                IntPtr pbpsynPtr = NativeLLSM.llsm_create_int(1);
                NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_PBPSYN,
                    pbpsynPtr, NativeCallbacks.DeleteInt, NativeCallbacks.CopyInt);

                // PBP 効果オブジェクト
                IntPtr pbpeffPtr = NativeLLSM.llsm_create_pbpeffect(callbackPtr, statePtr);
                NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_PBPEFF,
                    pbpeffPtr, NativeCallbacks.DeletePbpEffect, NativeCallbacks.CopyPbpEffect);
                applied++;
            }

            // 位相伝搬（PBP 後の位相一貫性）
            NativeLLSM.llsm_chunk_phasepropagate(dstChunk.DangerousGetHandle(), 1);
            _log.Info(Stage, $"PBP growl applied to {applied}/{dstNfrm} voiced frames (strength {_strength}%)");
        }

        private static void Callback(ref NativeLLSM.llsm_gfm gfm, ref float delta_t, IntPtr info, IntPtr src_frame)
        {
            var handle = GCHandle.FromIntPtr(info);
            if (handle.Target is not GrowlEffectState state) return;

            state.PeriodCount++;

            float lfo1 = MathF.Sin(state.PeriodCount * 2 * MathF.PI / 47.3f);
            float lfo2 = MathF.Sin(state.PeriodCount * 2 * MathF.PI / 31.7f);
            float lfo = (lfo1 + lfo2 * 0.6f) / 1.6f;

            float subfreq1 = 3.0f + lfo * 0.3f;
            float subfreq2 = 5.0f + lfo * 0.5f;

            state.Oscillator1 += 2 * MathF.PI / subfreq1;
            state.Oscillator2 += 2 * MathF.PI / subfreq2;

            float osc = MathF.Sin(state.Oscillator1) * 0.7f + MathF.Sin(state.Oscillator2) * 0.3f;

            float jitter1 = (float)state.Random.NextDouble() - 0.5f;
            float jitter2 = (float)state.Random.NextDouble() - 0.5f;
            float jitter = (jitter1 + jitter2) * 1.4142f;
            delta_t = gfm.T0 * 0.01f * jitter * state.Strength;

            gfm.Fa *= 1.0f - osc * 0.5f * state.Strength;
            gfm.Rk *= 1.0f + osc * 0.3f * state.Strength;
            gfm.Ee *= 1.0f - osc * 0.5f * state.Strength;
        }

        /// <summary>グロウル LFO／ジッター発振状態。</summary>
        private sealed class GrowlEffectState
        {
            public int PeriodCount;
            public float Oscillator1;
            public float Oscillator2;
            public readonly Random Random = new();
            public readonly float Strength;

            public GrowlEffectState(float strength) => Strength = strength / 100.0f;
        }
    }
}
