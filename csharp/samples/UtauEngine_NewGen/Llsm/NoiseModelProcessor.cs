using System;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// 雑音モデル(NM)の後処理。F0 誤推定による PSD リーケージやスパイクを抑え、
    /// V/UV 境界や子音過渡で生じるクリック・ジリつきを低減する。
    /// 検証済みの修正（eenv 変調深度クランプ、境界 PSD 制約）を保持する。
    /// （UtauEngine の NM 後処理関数群を FrameAccess ベースで再構成）
    /// </summary>
    public sealed class NoiseModelProcessor
    {
        private const string Stage = "NoiseModel";
        private readonly ILogger _log;

        public NoiseModelProcessor(ILogger log) => _log = log;

        /// <summary>
        /// eenv（ノイズ包絡）の変調深度を Σ|ampl| ≤ edc × <see cref="EenvModulationLimit"/> にクランプする。
        /// 極端な誤フィット（子音過渡の毎周期クリック）のみ抑制する。
        /// 合成側(layer0.c)は包絡を max(env, 1e-8) で正クランプするため、深度100%超
        /// （包絡が周期的にゼロへ触れるパルス同期の息）は正常な表現。100%で切ると
        /// F0同期変調が壊れ、母音オンセットで非同期ザラザラ（ガラガラ声）になる（実測済）。
        /// </summary>
        private const float EenvModulationLimit = 2.5f;

        public void ClampEenvModulationDepth(ChunkHandle chunk, float[] f0, int nfrm)
        {
            int clamped = 0;
            for (int i = 0; i < nfrm; i++)
            {
                if (f0[i] <= 0) continue; // 無声フレームは eenv 未使用

                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                var nm = FrameAccess.TryGetNm(frame);
                if (nm is not { HasEenv: true, HasEdc: true } nmv) continue;

                float[] edc = nmv.ReadEdc();
                bool frameClamped = false;

                for (int ch = 0; ch < nmv.NChannel; ch++)
                {
                    var eenv = nmv.GetEenvChannel(ch);
                    if (eenv is not { HasAmplitudes: true } ev) continue;

                    float[] ampl = ev.ReadAmplitudes();
                    float sum = 0;
                    for (int h = 0; h < ampl.Length; h++) sum += MathF.Abs(ampl[h]);

                    float limit = Math.Max(edc[ch], 0f) * EenvModulationLimit; // 変調深度250%まで許容
                    if (sum > limit && sum > 0)
                    {
                        float scale = limit / sum;
                        for (int h = 0; h < ampl.Length; h++) ampl[h] *= scale;
                        ev.WriteAmplitudes(ampl);
                        frameClamped = true;
                    }
                }
                if (frameClamped) clamped++;
            }

            if (clamped > 0)
                _log.Info(Stage, $"Clamped eenv modulation depth on {clamped}/{nfrm} frames");
        }

        /// <summary>
        /// 全フレームの PSD に ±1 フレームのメディアンフィルタを適用し、
        /// +3dB 超のスパイク（周波数リーケージ）を平滑化する。
        /// </summary>
        public void ApplyMedianFilterToPsd(ChunkHandle chunk, int nfrm)
        {
            if (nfrm < 3) return;

            var psds = new float[nfrm][];
            int npsd = -1;
            for (int i = 0; i < nfrm; i++)
            {
                var nm = FrameAccess.TryGetNm(LlsmBindings.Llsm.GetFrame(chunk, i));
                if (nm is { HasPsd: true } nmv)
                {
                    if (npsd == -1) npsd = nmv.NPsd;
                    psds[i] = nmv.ReadPsd();
                }
            }
            if (npsd <= 0) return;

            int fixedCount = 0;
            var buf = new float[3];
            for (int i = 1; i < nfrm - 1; i++)
            {
                if (psds[i] is null || psds[i - 1] is null || psds[i + 1] is null) continue;
                if (psds[i].Length != npsd || psds[i - 1].Length != npsd || psds[i + 1].Length != npsd) continue;

                bool modified = false;
                var filtered = new float[npsd];
                for (int j = 0; j < npsd; j++)
                {
                    buf[0] = psds[i - 1][j];
                    buf[1] = psds[i][j];
                    buf[2] = psds[i + 1][j];
                    Array.Sort(buf);
                    float med = buf[1];
                    filtered[j] = med;
                    if (psds[i][j] > med + 3.0f) modified = true; // 上方向スパイクを重点抑制
                }

                if (modified)
                {
                    var nm = FrameAccess.TryGetNm(LlsmBindings.Llsm.GetFrame(chunk, i));
                    nm?.WritePsd(filtered);
                    fixedCount++;
                }
            }

            if (fixedCount > 0)
                _log.Info(Stage, $"Applied median filter to PSD on {fixedCount} frames (suppressed spikes)");
        }

        /// <summary>
        /// V/UV 境界フレームの PSD を安全域へクランプし、F0 不安定時のリーケージを抑える。
        /// 閾値は -30dB(低域)〜-10dB(高域)、超過分は 50% に圧縮。
        /// </summary>
        public void ConstrainBoundaryPsd(ChunkHandle chunk, int nfrm)
        {
            var isVoiced = new bool[nfrm];
            for (int i = 0; i < nfrm; i++)
                isVoiced[i] = FrameAccess.IsVoiced(LlsmBindings.Llsm.GetFrame(chunk, i));

            int constrained = 0;
            for (int i = 1; i < nfrm - 1; i++)
            {
                bool isTransition = isVoiced[i - 1] != isVoiced[i] || isVoiced[i] != isVoiced[i + 1];
                if (!isTransition) continue;

                var nm = FrameAccess.TryGetNm(LlsmBindings.Llsm.GetFrame(chunk, i));
                if (nm is not { HasPsd: true } nmv) continue;

                float[] psd = nmv.ReadPsd();
                bool modified = false;
                for (int j = 0; j < psd.Length; j++)
                {
                    float freqRatio = (float)j / (psd.Length - 1);
                    float threshold = -30.0f + freqRatio * 20.0f;
                    if (psd[j] > threshold)
                    {
                        psd[j] = threshold + (psd[j] - threshold) * 0.5f;
                        modified = true;
                    }
                }

                if (modified)
                {
                    nmv.WritePsd(psd);
                    constrained++;
                }
            }

            if (constrained > 0)
                _log.Info(Stage, $"Constrained NM PSD on {constrained} V/UV boundary frames");
        }

        /// <summary>
        /// V/UV 境界（±1 フレーム）の有声側 eenv 振幅を最小 50% まで漸減し、
        /// 有声→無声の急変によるポップノイズを抑える。
        /// </summary>
        public void FadeEenvAtVuvBoundaries(ChunkHandle chunk, float[] dstF0, int dstNfrm)
        {
            const int fadeRadius = 1;
            var distToBoundary = new int[dstNfrm];
            Array.Fill(distToBoundary, fadeRadius + 1);

            for (int i = 1; i < dstNfrm; i++)
            {
                if ((dstF0[i - 1] > 0) == (dstF0[i] > 0)) continue;
                for (int k = 0; k <= fadeRadius; k++)
                {
                    if (i - 1 - k >= 0) distToBoundary[i - 1 - k] = Math.Min(distToBoundary[i - 1 - k], k);
                    if (i + k < dstNfrm) distToBoundary[i + k] = Math.Min(distToBoundary[i + k], k);
                }
            }

            int faded = 0;
            for (int i = 0; i < dstNfrm; i++)
            {
                if (dstF0[i] <= 0) continue;
                if (distToBoundary[i] > fadeRadius) continue;

                // 境界(dist=0)で 50%、radius の外で 100% へ漸増する実ランプ
                float fadeScale = 0.5f + 0.5f * distToBoundary[i] / (fadeRadius + 1f);
                var nm = FrameAccess.TryGetNm(LlsmBindings.Llsm.GetFrame(chunk, i));
                if (nm is not { HasEenv: true } nmv) continue;

                for (int ch = 0; ch < nmv.NChannel; ch++)
                {
                    var eenv = nmv.GetEenvChannel(ch);
                    if (eenv is not { HasAmplitudes: true } ev) continue;
                    float[] ampl = ev.ReadAmplitudes();
                    for (int h = 0; h < ampl.Length; h++) ampl[h] *= fadeScale;
                    ev.WriteAmplitudes(ampl);
                }
                faded++;
            }

            if (faded > 0)
                _log.Info(Stage, $"Faded eenv on {faded} V/UV boundary frames");
        }
    }
}
