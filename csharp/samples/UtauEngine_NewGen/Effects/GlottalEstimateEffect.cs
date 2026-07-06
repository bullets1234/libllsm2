using System;
using System.Linq;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// 声門パラメータ自動推定（L フラグ）。各有声フレームの倍音振幅から声門モデル(Rd)を
    /// スペクトルフィッティングで推定し、移動平均で平滑化してフレームへ適用する。
    /// 声質（張り・息漏れ）をソース音声から再現する。
    /// （UtauEngine EstimateAndApplyGlottalParameters の移植）
    /// </summary>
    public sealed class GlottalEstimateEffect : IChunkEffect
    {
        private const string Stage = "Glottal";
        private const int NParam = 241;     // Rd 0.3〜2.7, 0.01刻み
        private const int MaxHar = 100;
        private const int SmoothWindow = 3;

        private readonly bool _enabled;
        private readonly ILogger _log;

        public GlottalEstimateEffect(bool enabled, ILogger log)
        {
            _enabled = enabled;
            _log = log;
        }

        public bool IsActive => _enabled;

        public void Apply(ChunkHandle chunk, int nfrm, int fs)
        {
            if (!IsActive) return;

            float[] rdParams = new float[NParam];
            for (int i = 0; i < NParam; i++) rdParams[i] = 0.3f + i * 0.01f;

            IntPtr glottalModel = IntPtr.Zero;
            try
            {
                glottalModel = NativeLLSM.llsm_create_cached_glottal_model(rdParams, NParam, MaxHar);
                if (glottalModel == IntPtr.Zero)
                {
                    _log.Warn(Stage, "Failed to create glottal model");
                    return;
                }

                float[] rdPerFrame = new float[nfrm];
                for (int i = 0; i < nfrm; i++) rdPerFrame[i] = float.NaN;

                int voicedCount = 0;
                for (int i = 0; i < nfrm; i++)
                {
                    var framePtr = LlsmBindings.Llsm.GetFrame(chunk, i);
                    float f0 = LlsmBindings.Llsm.GetFrameF0(framePtr);
                    if (f0 < 50 || f0 > 800) continue;

                    IntPtr hmPtr = LlsmBindings.Llsm.GetFrameHM(framePtr);
                    if (hmPtr == IntPtr.Zero) continue;

                    int nhar = LlsmBindings.Llsm.GetHMNHar(hmPtr);
                    if (nhar <= 0) continue;

                    float[] ampl = LlsmBindings.Llsm.GetHMAmpl(hmPtr, nhar);
                    float estimatedRd = NativeLLSM.llsm_spectral_glottal_fitting(ampl, nhar, glottalModel);
                    rdPerFrame[i] = Math.Clamp(estimatedRd, 0.3f, 3.0f);
                    voicedCount++;
                }

                if (voicedCount == 0)
                {
                    _log.Info(Stage, "No voiced frames found for estimation");
                    return;
                }

                float[] rdSmoothed = new float[nfrm];
                for (int i = 0; i < nfrm; i++) rdSmoothed[i] = float.NaN;
                for (int i = 0; i < nfrm; i++)
                {
                    if (float.IsNaN(rdPerFrame[i])) continue;
                    float sum = 0; int count = 0;
                    for (int j = Math.Max(0, i - SmoothWindow / 2); j <= Math.Min(nfrm - 1, i + SmoothWindow / 2); j++)
                    {
                        if (!float.IsNaN(rdPerFrame[j])) { sum += rdPerFrame[j]; count++; }
                    }
                    rdSmoothed[i] = Math.Clamp(sum / count, 0.3f, 3.0f);
                }

                var validRd = rdSmoothed.Where(x => !float.IsNaN(x)).ToList();
                float meanRd = validRd.Average();
                float stdRd = validRd.Count > 1
                    ? MathF.Sqrt(validRd.Select(x => (x - meanRd) * (x - meanRd)).Average())
                    : 0;
                _log.Info(Stage, $"Analyzed {voicedCount}/{nfrm} frames, Rd mean={meanRd:F3} std={stdRd:F3} range=[{validRd.Min():F3}, {validRd.Max():F3}]");

                for (int i = 0; i < nfrm; i++)
                {
                    if (float.IsNaN(rdSmoothed[i])) continue;
                    LlsmBindings.Llsm.SetFrameRd(LlsmBindings.Llsm.GetFrame(chunk, i), rdSmoothed[i]);
                }
            }
            finally
            {
                if (glottalModel != IntPtr.Zero)
                    NativeLLSM.llsm_delete_cached_glottal_model(glottalModel);
            }
        }
    }

    /// <summary>
    /// 無声音減衰（U フラグ）。無声フレームのノイズエネルギーを指定 dB だけ減衰させる。
    /// 子音・息のノイズを抑えてクリアにする。バインディングの AttenuateUnvoiced を委譲。
    /// </summary>
    public sealed class UnvoicedAttenuationEffect : IChunkEffect
    {
        private const string Stage = "Unvoiced";
        private readonly int _attenuationDb;
        private readonly ILogger _log;

        public UnvoicedAttenuationEffect(int attenuationDb, ILogger log)
        {
            _attenuationDb = attenuationDb;
            _log = log;
        }

        public bool IsActive => _attenuationDb > 0;

        public void Apply(ChunkHandle chunk, int nfrm, int fs)
        {
            if (!IsActive) return;
            LlsmBindings.Llsm.AttenuateUnvoiced(chunk, uvDb: -_attenuationDb);
            _log.Info(Stage, $"Attenuation -{_attenuationDb}dB applied (Layer1)");
        }
    }

    /// <summary>
    /// 声門閉鎖係数（K フラグ）。有声フレームの Rd（声門波形パラメータ）をスケールして
    /// 声質を調整する。K0=息漏れ声(Rd大)、K50=標準(identity)、K100=硬い声(Rd小)。
    /// （UtauEngine の K フラグ Rd 編集ブロックの移植）
    /// </summary>
    public sealed class GlottalClosureEffect : IChunkEffect
    {
        private const string Stage = "GlottalClosure";
        private readonly int _closure;
        private readonly ILogger _log;

        public GlottalClosureEffect(int closure, ILogger log)
        {
            _closure = closure;
            _log = log;
        }

        public bool IsActive => _closure != 50;

        // LF/Rd 声門モデルが物理的に安定な範囲。これを超えると声門波形が
        // 破綻し、不自然なノイズ（特に K0 付近）の原因になる。声門推定時の
        // クランプ（estimatedRd ∈ [0.3, 3.0]）と同一範囲。
        private const float MinRd = 0.3f;
        private const float MaxRd = 3.0f;

        public void Apply(ChunkHandle chunk, int nfrm, int fs)
        {
            if (!IsActive) return;

            float rdScale = _closure <= 50
                ? 2.5f - (_closure / 50.0f) * 1.5f          // K0..50 : 2.5 -> 1.0
                : 1.0f - ((_closure - 50) / 50.0f) * 0.7f;  // K50..100: 1.0 -> 0.3

            int clamped = 0;
            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                if (LlsmBindings.Llsm.GetFrameF0(frame) <= 0) continue;

                var rdPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_RD);
                if (rdPtr == IntPtr.Zero) continue;

                float currentRd = Marshal.PtrToStructure<float>(rdPtr);
                float newRd = currentRd * rdScale;
                float boundedRd = Math.Clamp(newRd, MinRd, MaxRd);
                if (boundedRd != newRd) clamped++;
                Marshal.StructureToPtr(boundedRd, rdPtr, false);
            }
            _log.Info(Stage, $"K{_closure} applied (Rd scale {rdScale:F2}, clamped {clamped}/{nfrm} frames to [{MinRd:F1},{MaxRd:F1}])");
        }
    }
}
