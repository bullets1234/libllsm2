using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// HNR エンハンス（D フラグ）。倍音／ノイズ比(SNR)に応じて適応的に NM PSD を低減し、
    /// 倍音優位フレームのざらつきを抑える。低 SNR ほど強く、高域ほど強く低減。
    /// （UtauEngine EnhanceHNR の移植）
    /// </summary>
    public sealed class HnrEffect : IChunkEffect
    {
        private const string Stage = "HNR";
        private readonly int _strength;
        private readonly ILogger _log;

        public HnrEffect(int strength, ILogger log)
        {
            _strength = strength;
            _log = log;
        }

        public bool IsActive => _strength > 0;

        public void Apply(ChunkHandle chunk, int nfrm, int fs)
        {
            if (!IsActive) return;

            float maxReductionDb = 6.0f * (_strength / 100.0f);
            int processedFrames = 0;

            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);

                var hmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_HM);
                if (hmPtr == IntPtr.Zero) continue;
                var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
                if (hm.nhar <= 0) continue;

                var nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
                if (nmPtr == IntPtr.Zero) continue;
                var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                if (nm.npsd <= 0) continue;

                float[] ampl = new float[hm.nhar];
                Marshal.Copy(hm.ampl, ampl, 0, hm.nhar);
                double harmonicPower = 0;
                for (int h = 0; h < hm.nhar; h++) harmonicPower += ampl[h] * ampl[h];
                if (harmonicPower <= 1e-20) continue;
                double harmonicPowerDb = 10.0 * Math.Log10(harmonicPower);

                float[] psd = new float[nm.npsd];
                Marshal.Copy(nm.psd, psd, 0, nm.npsd);
                double noiseMeanDb = 0;
                for (int j = 0; j < nm.npsd; j++) noiseMeanDb += psd[j];
                noiseMeanDb /= nm.npsd;

                double snrDb = harmonicPowerDb - noiseMeanDb;

                double adaptiveFactor;
                if (snrDb <= 10.0) adaptiveFactor = 1.0;
                else if (snrDb >= 30.0) adaptiveFactor = 0.0;
                else adaptiveFactor = 1.0 - (snrDb - 10.0) / 20.0;
                if (adaptiveFactor <= 0.01) continue;

                double reductionDb = maxReductionDb * adaptiveFactor;
                for (int j = 0; j < nm.npsd; j++)
                {
                    double freqRatio = (double)j / Math.Max(1, nm.npsd - 1);
                    double freqWeight = 0.3 + 0.7 * freqRatio;
                    psd[j] -= (float)(reductionDb * freqWeight);
                }

                Marshal.Copy(psd, 0, nm.psd, nm.npsd);
                processedFrames++;
            }

            _log.Info(Stage, $"Enhanced {processedFrames}/{nfrm} frames (max reduction: {maxReductionDb:F1}dB)");
        }
    }
}
