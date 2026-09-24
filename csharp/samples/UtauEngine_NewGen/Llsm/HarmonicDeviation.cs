using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// 倍音番号で索引した振幅偏差（d フラグ）。
    ///
    /// Layer1 変換は倍音振幅を「声道包絡 VTMAGN × 声門モデル(Rd)」でフィットするため、
    /// 倍音ごとの振幅の細かな偏差（原音の倍音構造の個性）は失われる。位相の偏差は VSPHSE として
    /// 倍音番号で保持されているのに、振幅偏差だけが落ちる形になっている。
    /// 本クラスは解析直後の HM 振幅（実測）と、Layer1 パラメータから再生成した HM 振幅（モデル）の
    /// 比を dB で倍音ごとに保持し、合成時に出力フレームの新しい倍音周波数 k·f0' の位置へ当て直す
    /// （Wavehax の「調波事前分布に対する補正」の非ニューラル版）。ピッチシフトが大きいほど
    /// 偏差にはフォルマント由来の成分が混ざるため、シフト量に応じて重みを下げる。
    /// 既存の <see cref="ResidualEnvelopeCorrector"/>（周波数索引）の置き換え。
    /// </summary>
    public static class HarmonicDeviation
    {
        private const string Stage = "HarmDev";
        private const float ClampDb = 15f;
        /// <summary>この cents 以内のシフトは全量（1.0）。</summary>
        private const float FullWeightCents = 200f;
        /// <summary>この cents で重みが <see cref="FarWeight"/> になる（以遠は一定）。</summary>
        private const float FarCents = 1200f;
        private const float FarWeight = 0.3f;

        /// <summary>
        /// Layer1 変換・ダウンサンプル後のチャンクについて、スナップショット振幅（解析直後の HM）と
        /// Layer1 から再生成した HM 振幅の比（dB）をフレーム毎・倍音毎に返す。無声/欠損は null。
        /// </summary>
        public static float[]?[] Measure(ChunkHandle chunk, int nfrm, float[]?[] snapshots, ILogger log)
        {
            var conf = LlsmBindings.Llsm.GetConf(chunk);
            var dev = new float[]?[nfrm];
            int frames = 0; double sumAbs = 0; int cnt = 0;
            for (int i = 0; i < nfrm; i++)
            {
                var meas = snapshots[i];
                if (meas == null) continue;
                var fr = LlsmBindings.Llsm.GetFrame(chunk, i);
                if (LlsmBindings.Llsm.GetFrameF0(fr) <= 0) continue;

                // Layer1 → Layer0 をコピー上で再生成してモデル振幅を得る
                IntPtr copy = LlsmBindings.Llsm.CopyFrame(fr);
                float[]? model = null;
                try
                {
                    NativeLLSM.llsm_frame_tolayer0(copy, conf.Ptr);
                    IntPtr hmPtr = NativeLLSM.llsm_container_get(copy, NativeLLSM.LLSM_FRAME_HM);
                    if (hmPtr != IntPtr.Zero)
                    {
                        var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
                        if (hm.nhar > 0 && hm.ampl != IntPtr.Zero)
                        {
                            model = new float[hm.nhar];
                            Marshal.Copy(hm.ampl, model, 0, hm.nhar);
                        }
                    }
                }
                finally { NativeLLSM.llsm_delete_container(copy); }
                if (model == null) continue;

                int n = Math.Min(meas.Length, model.Length);
                if (n <= 0) continue;
                var d = new float[n];
                for (int k = 0; k < n; k++)
                {
                    float m = Math.Max(meas[k], 1e-8f), p = Math.Max(model[k], 1e-8f);
                    d[k] = Math.Clamp(20f * MathF.Log10(m / p), -ClampDb, ClampDb);
                    sumAbs += Math.Abs(d[k]); cnt++;
                }
                dev[i] = d;
                frames++;
            }
            log.Info(Stage, $"Harmonic deviation measured on {frames}/{nfrm} frames, mean |dev| {(cnt > 0 ? sumAbs / cnt : 0):F2} dB");
            return dev;
        }

        /// <summary>シフト量（cents）に応じた重み。</summary>
        public static float Weight(float cents)
        {
            float a = MathF.Abs(cents);
            if (a <= FullWeightCents) return 1f;
            if (a >= FarCents) return FarWeight;
            float t = (a - FullWeightCents) / (FarCents - FullWeightCents);
            return 1f + (FarWeight - 1f) * t;
        }

        /// <summary>
        /// 出力フレームの VTMAGN（dB, nspec ビン, 0..fnyq）に、倍音番号索引の偏差を
        /// 新しい倍音周波数 k·f0New の位置で区分線形に加算する。
        /// </summary>
        public static void ApplyToVtmagn(float[] vtmagn, float fnyq, float f0New, float[]? dev1, float[]? dev2, float ratio, float weight, float floorDb)
        {
            if (f0New <= 0 || weight <= 0) return;
            var dev = dev1 ?? dev2;
            if (dev == null) return;
            int n = dev1 != null && dev2 != null ? Math.Min(dev1.Length, dev2.Length) : dev.Length;
            if (n <= 0) return;
            float DevAt(int k) // k: 0-based harmonic index
            {
                float a = dev1 != null && k < dev1.Length ? dev1[k] : (dev2 != null && k < dev2.Length ? dev2[k] : 0f);
                float b = dev2 != null && k < dev2.Length ? dev2[k] : a;
                return dev1 != null && dev2 != null ? a * (1f - ratio) + b * ratio : (dev1 != null ? a : b);
            }
            int nspec = vtmagn.Length;
            float binHz = fnyq / (nspec - 1);
            int kMax = n - 1;
            for (int b = 0; b < nspec; b++)
            {
                float f = b * binHz;
                float pos = f / f0New - 1f;   // 倍音インデックス位置（0 = 第 1 倍音）
                float add;
                if (pos <= 0) add = DevAt(0);
                else if (pos >= kMax)
                {
                    // 最終倍音より上は 1 倍音幅で 0 へ減衰
                    float over = pos - kMax;
                    add = over >= 1f ? 0f : DevAt(kMax) * (1f - over);
                }
                else
                {
                    int k = (int)pos; float t = pos - k;
                    add = DevAt(k) * (1f - t) + DevAt(k + 1) * t;
                }
                if (add != 0f) vtmagn[b] = Math.Max(vtmagn[b] + add * weight, floorDb);
            }
        }
    }
}
