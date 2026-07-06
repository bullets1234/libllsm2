using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Llsm;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// 息成分（B フラグ）。声成分(VTMAGN)を減衰しノイズ成分(NM)を増幅、
    /// さらにフォルマント形状を息へ転写し、有機的テクスチャ（低周波ゆらぎ・帯域変動・
    /// バースト・eenv サイクルジッター）を加えて自然な気息を再現する。
    /// （UtauEngine ApplyBreathiness + ApplyOrganicBreathTexture の移植）
    /// </summary>
    public sealed class BreathinessEffect : IFrameEffect
    {
        private const float VtmagnFloorDb = -80f;
        private readonly int _breathiness;

        public BreathinessEffect(int breathiness) => _breathiness = breathiness;

        public bool IsActive => _breathiness != 50;

        public void Apply(in FrameEffectContext ctx)
        {
            if (!IsActive) return;
            var frame = ctx.Frame;
            int outFrameIdx = ctx.OutFrameIdx;

            float ratio = (_breathiness - 50) / 50.0f;          // -1..+1
            float voiceAttenuation = ratio < 0 ? ratio * 6.0f : ratio * 30.0f;
            float noiseGain = ratio < 0 ? ratio * 6.0f : ratio * 12.0f;

            var nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
            bool hasNm = nmPtr != IntPtr.Zero;
            NativeLLSM.llsm_nmframe nm = default;
            int npsd = 0;
            if (hasNm)
            {
                nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                if (nm.psd != IntPtr.Zero) npsd = nm.npsd;
            }

            // VTMAGN 減衰 + フォルマント偏差抽出
            var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            float[]? formantDeviation = null;
            if (vtmagnPtr != IntPtr.Zero)
            {
                int vtmagnSize = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                if (vtmagnSize > 0)
                {
                    float[] magnitudes = new float[vtmagnSize];
                    Marshal.Copy(vtmagnPtr, magnitudes, 0, vtmagnSize);

                    if (ratio > 0 && npsd > 1 && vtmagnSize > 1)
                    {
                        formantDeviation = new float[npsd];
                        float vtMean = 0f; int vtCount = 0;
                        for (int j = 0; j < vtmagnSize; j++)
                            if (magnitudes[j] > VtmagnFloorDb + 1f) { vtMean += magnitudes[j]; vtCount++; }
                        vtMean = vtCount > 0 ? vtMean / vtCount : 0f;

                        for (int i = 0; i < npsd; i++)
                        {
                            float fpos = (float)i / (npsd - 1) * (vtmagnSize - 1);
                            int vi = (int)fpos;
                            float vfrac = fpos - vi;
                            int vi2 = Math.Min(vi + 1, vtmagnSize - 1);
                            float vtVal = magnitudes[vi] * (1f - vfrac) + magnitudes[vi2] * vfrac;
                            formantDeviation[i] = Math.Clamp(vtVal - vtMean, -18f, 18f);
                        }
                    }

                    for (int i = 0; i < vtmagnSize; i++)
                        magnitudes[i] = Math.Max(VtmagnFloorDb, magnitudes[i] - voiceAttenuation);

                    Marshal.Copy(magnitudes, 0, vtmagnPtr, vtmagnSize);
                }
            }

            if (!hasNm) return;

            // PSD 増幅 + フォルマント転写 + エアシェルフ + 有機テクスチャ
            if (nm.psd != IntPtr.Zero && nm.npsd > 0)
            {
                float[] psd = new float[nm.npsd];
                Marshal.Copy(nm.psd, psd, 0, nm.npsd);

                for (int i = 0; i < nm.npsd; i++)
                {
                    float freqRatio = (float)i / (nm.npsd - 1);
                    float freqWeight = 1.0f + freqRatio * 0.5f;
                    psd[i] += noiseGain * freqWeight;

                    if (formantDeviation != null)
                    {
                        float couplingWeight = 0.15f + freqRatio * 0.20f;
                        psd[i] += formantDeviation[i] * couplingWeight * ratio;
                    }
                    if (ratio > 0 && freqRatio > 0.75f)
                    {
                        float airShelf = (freqRatio - 0.75f) / 0.25f * 4.0f * ratio;
                        psd[i] += airShelf;
                    }
                    psd[i] = Math.Max(-120f, psd[i]);
                }

                if (outFrameIdx >= 0 && ratio > 0)
                    ApplyOrganicBreathTexture(psd, nm.npsd, outFrameIdx, ratio);

                Marshal.Copy(psd, 0, nm.psd, nm.npsd);
            }

            // EDC 線形スケール
            if (nm.edc != IntPtr.Zero && nm.nchannel > 0)
            {
                float linearScale = MathF.Pow(10, noiseGain / 20.0f);
                float[] edc = new float[nm.nchannel];
                Marshal.Copy(nm.edc, edc, 0, nm.nchannel);
                for (int i = 0; i < nm.nchannel; i++)
                    edc[i] = Math.Max(0f, edc[i] * linearScale);
                Marshal.Copy(edc, 0, nm.edc, nm.nchannel);
            }

            // eenv サイクルジッター
            if (outFrameIdx >= 0 && ratio > 0 && nm.eenv != IntPtr.Zero && nm.nchannel > 0)
            {
                IntPtr[] eenvPtrs = new IntPtr[nm.nchannel];
                Marshal.Copy(nm.eenv, eenvPtrs, 0, nm.nchannel);
                float jitterDepth = 0.15f * ratio;

                for (int ch = 0; ch < nm.nchannel; ch++)
                {
                    if (eenvPtrs[ch] == IntPtr.Zero) continue;
                    var eenv = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvPtrs[ch]);
                    if (eenv.nhar <= 0 || eenv.ampl == IntPtr.Zero) continue;

                    float[] ampl = new float[eenv.nhar];
                    Marshal.Copy(eenv.ampl, ampl, 0, eenv.nhar);

                    float chJitter = DeterministicNoise.Hash(outFrameIdx, 5000 + ch) * 2f;
                    float scale = Math.Clamp(1f + chJitter * jitterDepth, 0.5f, 1.6f);
                    for (int h = 0; h < eenv.nhar; h++) ampl[h] *= scale;

                    Marshal.Copy(ampl, 0, eenv.ampl, eenv.nhar);
                }
            }
        }

        /// <summary>息に「生感」を与える NM PSD 変調（低周波ゆらぎ・4帯域変動・確率的バースト）。</summary>
        private static void ApplyOrganicBreathTexture(float[] psd, int npsd, int outFrameIdx, float strength)
        {
            const int slowKnotInterval = 10;
            int slowKnot0 = outFrameIdx / slowKnotInterval;
            float slowT = (float)(outFrameIdx % slowKnotInterval) / slowKnotInterval;
            float slowBlend = (1f - MathF.Cos(slowT * MathF.PI)) * 0.5f;
            float globalAm = (DeterministicNoise.Hash(slowKnot0, 7777) * (1f - slowBlend)
                            + DeterministicNoise.Hash(slowKnot0 + 1, 7777) * slowBlend) * 2.5f * strength;

            const int fastKnotInterval = 4;
            int fastKnot0 = outFrameIdx / fastKnotInterval;
            float fastT = (float)(outFrameIdx % fastKnotInterval) / fastKnotInterval;
            float fastBlend = (1f - MathF.Cos(fastT * MathF.PI)) * 0.5f;

            const int nBands = 4;
            float[] bandGain = new float[nBands];
            for (int b = 0; b < nBands; b++)
            {
                float n0 = DeterministicNoise.Hash(fastKnot0, 8000 + b);
                float n1 = DeterministicNoise.Hash(fastKnot0 + 1, 8000 + b);
                bandGain[b] = (n0 * (1f - fastBlend) + n1 * fastBlend) * 1.8f * strength;
            }

            float burstGain = 0f;
            const int burstPeriod = 15;
            int burstKnot = outFrameIdx / burstPeriod;
            float burstT = (float)(outFrameIdx % burstPeriod) / burstPeriod;
            float burstEnvelope = MathF.Sin(burstT * MathF.PI);
            float burstBase = DeterministicNoise.Hash(burstKnot, 9999) + 0.5f;
            if (burstBase > 0.6f)
                burstGain = burstEnvelope * ((burstBase - 0.6f) / 0.4f) * 3.5f * strength;

            for (int p = 0; p < npsd; p++)
            {
                float freqRatio = (float)p / (npsd - 1);
                int band = Math.Min((int)(freqRatio * nBands), nBands - 1);
                float highWeight = 0.4f + freqRatio * 1.2f;
                float totalMod = (globalAm + bandGain[band]) * highWeight;
                if (freqRatio > 0.25f)
                    totalMod += burstGain * Math.Min(1f, (freqRatio - 0.25f) * 4f);
                psd[p] = Math.Max(-120f, psd[p] + totalMod);
            }
        }
    }
}
