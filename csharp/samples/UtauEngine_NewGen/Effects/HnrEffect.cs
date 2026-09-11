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

            // PSD 軸は 0..conf FNYQ の線形軸（layer0.c: linspace(0, fs/2, npsd)）。
            // このエフェクトは 2x 解析チャンクに適用されるため fnyq は conf から取る。
            var conf = LlsmBindings.Llsm.GetConf(chunk);
            float fnyq = LlsmBindings.Llsm.GetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ);
            if (fnyq <= 0) fnyq = fs / 2.0f;
            // layer0.c:412 の PSD 規約 10·log10(psd·44100/fs) に対応する物理スケール
            //（BandEnergyCalibrator と同一の較正値）
            const double PsdPhysScale = 2.0 / 44100.0;

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

                // 倍音総パワー: Σa²/2（正弦波の実効パワー）
                float[] ampl = new float[hm.nhar];
                Marshal.Copy(hm.ampl, ampl, 0, hm.nhar);
                double harmonicPower = 0;
                for (int h = 0; h < hm.nhar; h++) harmonicPower += (double)ampl[h] * ampl[h] / 2.0;
                if (harmonicPower <= 1e-20) continue;

                float[] psd = new float[nm.npsd];
                Marshal.Copy(nm.psd, psd, 0, nm.npsd);

                // ノイズ総パワー: PSD（dBパワー密度）を線形領域で周波数積分する。
                // 旧実装は「倍音の総パワーdB」と「PSD の dB 平均（≒幾何平均密度）」を
                // 直接引き算しており 10〜30dB の系統誤差で adaptiveFactor が常時ほぼ 0
                //（= D フラグがほぼ無効）だった。
                double deltaF = nm.npsd > 1 ? fnyq / (nm.npsd - 1) : fnyq;
                double noisePower = 0;
                for (int j = 0; j < nm.npsd; j++)
                    noisePower += Math.Pow(10.0, psd[j] / 10.0) * PsdPhysScale * deltaF;

                double snrDb = 10.0 * Math.Log10(harmonicPower / Math.Max(noisePower, 1e-20));

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
