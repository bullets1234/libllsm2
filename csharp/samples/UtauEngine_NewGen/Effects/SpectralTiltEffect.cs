using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LlsmBindings;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// スペクトル傾斜（T フラグ）。フォルマントピークを保持しながらスペクトル全体の傾きを
    /// 変える。明るさ／こもりの調整。±12 程度。無声フレームはスキップ。
    /// （UtauEngine ApplySpectralTilt の移植）
    /// </summary>
    public sealed class SpectralTiltEffect : IChunkEffect
    {
        private const float VtmagnFloorDb = -80f;
        private readonly int _spectralTilt;

        public SpectralTiltEffect(int spectralTilt) => _spectralTilt = spectralTilt;

        public bool IsActive => _spectralTilt != 0;

        public void Apply(ChunkHandle chunk, int nfrm, int fs)
        {
            if (!IsActive) return;

            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                if (LlsmBindings.Llsm.GetFrameF0(frame) <= 0) continue;

                var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr == IntPtr.Zero) continue;

                int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                if (nspec <= 0) continue;
                float[] vtmagn = new float[nspec];
                Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);

                List<(int index, float magnitude)> formantPeaks = SpectralUtils.DetectFormantPeaks(vtmagn, fs, nspec);

                for (int j = 0; j < nspec; j++)
                {
                    float freqKhz = (float)j / nspec * (fs / 2000.0f);
                    vtmagn[j] += _spectralTilt * MathF.Log2(MathF.Max(freqKhz, 0.1f));
                }

                foreach (var (peakIdx, origMagnitude) in formantPeaks)
                {
                    float correction = (origMagnitude - vtmagn[peakIdx]) * (_spectralTilt < 0 ? 0.5f : 0.7f);
                    int bandwidthBins = Math.Max(3, nspec / 100);
                    float sigma = bandwidthBins / 2.0f;
                    for (int k = -bandwidthBins; k <= bandwidthBins; k++)
                    {
                        int idx = peakIdx + k;
                        if (idx >= 0 && idx < nspec)
                        {
                            float weight = MathF.Exp(-(k * k) / (2 * sigma * sigma));
                            vtmagn[idx] += correction * weight;
                        }
                    }
                }

                for (int j = 0; j < nspec; j++)
                    vtmagn[j] = Math.Max(vtmagn[j], VtmagnFloorDb);
                Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
            }
        }
    }
}
