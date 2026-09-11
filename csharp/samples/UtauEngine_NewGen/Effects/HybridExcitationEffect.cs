using System;
using LlsmBindings;
using UtauEngineNg.Diagnostics;
using UtauEngineNg.Llsm;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// 高域ハイブリッド励振（Y フラグ、試験実装）。
    ///
    /// 生声の高域（約7kHz以上）は純粋な倍音ではなく「ピッチに同期して脈動する
    /// 色付きノイズ」に近い。LLSM の高域倍音推定は本来ノイズであるものを正弦波と
    /// してフィットするため、再合成で金属的な光沢感（安定しすぎた純正弦波）が出る。
    ///
    /// 本エフェクトはクロスオーバー帯域以上の倍音エネルギーの一部を NM（ノイズ
    /// モデル）へ再配分する:
    ///   1. 倍音振幅を √(1-a) 倍に減衰（a = 帯域重み × 強度、上限 0.95 で倍音床を残す）
    ///   2. 移動したパワーを PSD の同帯域へ「原音実測の形のまま」密度加算
    ///      （フラットなブーストにしない — 旧 BandEnergyCalibrator がホワイトノイズ化
    ///      した教訓）
    ///   3. 高域チャンネルの eenv 変調を深め、ノイズを声帯周期に同期させる
    ///      （定常ヒスでなく「息の脈動」にする — ここが白色化回避の決定打）
    ///
    /// HM が存在する解析直後（Layer1 変換前）に適用すること。変更は tolayer1 の
    /// 包絡フィット・ResidualEnvelopeCorrector のスナップショットへ自然に伝搬する。
    /// </summary>
    public sealed class HybridExcitationEffect : IChunkEffect
    {
        private const string Stage = "HybridExc";

        // クロスオーバー: 5k→9kHz のコサインランプ（ブリックウォール回避）
        private const float RampLoHz = 5000f;
        private const float RampHiHz = 9000f;
        // 再配分率上限（完全置換にせず倍音床 ≈ -13dB を残す）
        private const float MaxReallocFraction = 0.95f;
        // layer0.c:412 の PSD 規約 10·log10(psd·44100/fs) に対応する物理スケール
        //（BandEnergyCalibrator / HnrEffect と同一の較正値）
        private const double PsdPhysScale = 2.0 / 44100.0;
        // eenv 変調ブースト上限（強度100・重み1で +60%）
        private const float MaxEenvBoost = 0.6f;

        private readonly int _strength;
        private readonly ILogger _log;

        public HybridExcitationEffect(int strength, ILogger log)
        {
            _strength = strength;
            _log = log;
        }

        public bool IsActive => _strength > 0;

        public void Apply(ChunkHandle chunk, int nfrm, int fs)
        {
            if (!IsActive) return;

            var conf = LlsmBindings.Llsm.GetConf(chunk);
            float fnyq = LlsmBindings.Llsm.GetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ);
            if (fnyq <= 0) fnyq = fs / 2f;
            // 2x 解析チャンク上で動くため、可聴上限（元 fs の Nyquist）までのみ処理。
            // それ以上は SpectrumDownsampler が破棄する帯域
            float audibleNyq = fs / 2f;
            float s = _strength / 100f;

            int applied = 0;
            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                float f0 = LlsmBindings.Llsm.GetFrameF0(frame);
                if (f0 <= 0) continue;

                var hm = FrameAccess.TryGetHm(frame);
                if (hm is not { HasAmplitudes: true } hmv) continue;
                var nm = FrameAccess.TryGetNm(frame);
                if (nm is not { HasPsd: true } nmv) continue;

                float[] ampl = hmv.ReadAmplitudes();
                int npsd = nmv.NPsd;
                if (npsd < 2) continue;
                double binHz = fnyq / (npsd - 1);
                var addDensity = new double[npsd];
                bool touched = false;

                for (int k = 0; k < ampl.Length; k++)
                {
                    float fk = (k + 1) * f0;
                    if (fk > audibleNyq) break;
                    float w = CrossfadeWeight(fk);
                    if (w <= 0) continue;
                    float a = Math.Min(MaxReallocFraction, w * s);

                    double power = (double)ampl[k] * ampl[k] / 2.0;
                    double moved = power * a;
                    ampl[k] *= MathF.Sqrt(1f - a);

                    // 密度 ΔP/f0 を [fk-f0/2, fk+f0/2] のビンへ一様加算
                    //（単一ビン加算だと PSD に櫛状の凹凸が出る）
                    int lo = Math.Max(0, (int)((fk - f0 / 2) / binHz));
                    int hi = Math.Min(npsd - 1, (int)((fk + f0 / 2) / binHz));
                    double density = moved / f0;
                    for (int j = lo; j <= hi; j++) addDensity[j] += density;
                    touched = true;
                }
                if (!touched) continue;

                float[] psd = nmv.ReadPsd();
                for (int j = 0; j < npsd; j++)
                {
                    if (addDensity[j] <= 0) continue;
                    double lin = Math.Pow(10.0, psd[j] / 10.0) * PsdPhysScale + addDensity[j];
                    psd[j] = (float)(10.0 * Math.Log10(lin / PsdPhysScale + 1e-30));
                }
                hmv.WriteAmplitudes(ampl);
                nmv.WritePsd(psd);

                // 高域チャンネルの eenv 変調を深める（ピッチ同期の脈動化）
                if (nmv.HasEenv)
                {
                    for (int ch = 0; ch < nmv.NChannel; ch++)
                    {
                        float wc = CrossfadeWeight(ChannelCenterHz(ch));
                        if (wc <= 0) continue;
                        var eenv = nmv.GetEenvChannel(ch);
                        if (eenv is not { HasAmplitudes: true } ev) continue;
                        float[] ea = ev.ReadAmplitudes();
                        float boost = 1f + MaxEenvBoost * s * wc;
                        for (int h = 0; h < ea.Length; h++) ea[h] *= boost;
                        ev.WriteAmplitudes(ea);
                    }
                }
                applied++;
            }

            _log.Info(Stage, $"Y{_strength}: reallocated HF harmonic energy to pitch-synchronous noise " +
                             $"on {applied}/{nfrm} voiced frames (ramp {RampLoHz:F0}-{RampHiHz:F0}Hz)");
        }

        /// <summary>クロスオーバー重み: RampLo 以下 0、RampHi 以上 1 のコサインランプ。</summary>
        private static float CrossfadeWeight(float freqHz)
        {
            if (freqHz <= RampLoHz) return 0f;
            if (freqHz >= RampHiHz) return 1f;
            float t = (freqHz - RampLoHz) / (RampHiHz - RampLoHz);
            return 0.5f - 0.5f * MathF.Cos(t * MathF.PI);
        }

        /// <summary>NM チャンネル境界 {2k,4k,8k,12k} 前提の中心周波数近似。</summary>
        private static float ChannelCenterHz(int ch) => ch switch
        {
            0 => 1000f,
            1 => 3000f,
            2 => 6000f,
            3 => 10000f,
            _ => 16000f,
        };
    }
}
