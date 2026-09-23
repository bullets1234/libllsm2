using System;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// 位相導出 F0（Wavehax 的「調波事前分布の瞬時周波数を正しくする」補正）。
    ///
    /// llsm_chunk_phasepropagate は各フレームの F0 を積分した位相を全倍音から引き（逆伝播）、
    /// 合成時に新しい F0 で積分し直す。積分に使う F0 が実測パルス周期と一致していれば、
    /// 引いた残り（VSPHSE）は時間的にほぼ一定になり、フレーム補間しても位相が崩れない。
    /// 解析 F0（PYIN + フレーム中心での瞬時周波数 refine）には数 Hz の誤差があり、その積分誤差が
    /// VSPHSE にランダムウォークとして残るため、補間で混ざって合成パルスの時刻が約 50µs RMS
    /// 揺れていた（等倍で SNR 11dB、位相導出 F0 で補間を迂回すると 19.6dB。2026-09-24 計測）。
    ///
    /// 本クラスは隣接フレームの H1 実測位相差から「フレーム間の平均 F0」を導出し、フレームの F0 を
    /// それで置き換える。2π の曖昧さは解析 F0 で解く。F0 の変化は数 Hz なので HM の倍音周波数の
    /// 意味は変わらない。有声区間の先頭フレームは元の値を保つ。
    /// </summary>
    public static class PhaseConsistentF0
    {
        private const string Stage = "PhaseF0";
        /// <summary>解析 F0 からの許容偏差（比）。これを超える導出値は誤アンラップとみなし棄却。</summary>
        private const float MaxDeviationRatio = 0.08f;

        public static void Apply(ChunkHandle chunk, int nfrm, float thopSec, ILogger log)
        {
            var f0 = new float[nfrm];
            var phi1 = new float[nfrm];
            var has = new bool[nfrm];
            for (int i = 0; i < nfrm; i++)
            {
                var fr = LlsmBindings.Llsm.GetFrame(chunk, i);
                f0[i] = LlsmBindings.Llsm.GetFrameF0(fr);
                if (f0[i] <= 0) continue;
                var hm = FrameAccess.TryGetHm(fr);
                if (hm is not { HasPhases: true } hmv || hmv.NHar < 1) continue;
                phi1[i] = hmv.ReadPhases()[0];
                has[i] = true;
            }

            int replaced = 0, rejected = 0; double sumAbs = 0;
            for (int i = 1; i < nfrm; i++)
            {
                if (!has[i] || !has[i - 1]) continue;
                double expected = 2.0 * Math.PI * f0[i] * thopSec;           // 解析 F0 による予想位相進み
                double d = phi1[i] - phi1[i - 1];
                d -= 2.0 * Math.PI * Math.Round((d - expected) / (2.0 * Math.PI)); // expected の近傍へアンラップ
                float derived = (float)(d / (2.0 * Math.PI * thopSec));
                if (derived <= 0 || MathF.Abs(derived - f0[i]) > f0[i] * MaxDeviationRatio) { rejected++; continue; }
                sumAbs += Math.Abs(derived - f0[i]);
                LlsmBindings.Llsm.SetFrameF0(LlsmBindings.Llsm.GetFrame(chunk, i), derived);
                replaced++;
            }
            log.Info(Stage, $"Phase-consistent F0: replaced {replaced}/{nfrm} frames (mean |delta| {(replaced > 0 ? sumAbs / replaced : 0):F2} Hz), rejected {rejected}");
        }
    }
}
