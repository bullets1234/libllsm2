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

                // ネイティブ推定器 (layer1.c llsm_analyze_rd) と同じ前処理:
                // 8kHz までの倍音に制限し、逆リップ放射フィルタ (+6dB/oct 除去) を適用。
                // これを省くとティルトが乗ったまま比較され、Rd が張り上げ側へ系統的に
                // バイアスする。
                var conf = LlsmBindings.Llsm.GetConf(chunk);
                float lipRadius = LlsmBindings.Llsm.GetConfFloat(conf, NativeLLSM.LLSM_CONF_LIPRADIUS);

                int voicedCount = 0;
                for (int i = 0; i < nfrm; i++)
                {
                    var framePtr = LlsmBindings.Llsm.GetFrame(chunk, i);
                    float f0 = LlsmBindings.Llsm.GetFrameF0(framePtr);
                    if (f0 < 50 || f0 > 800) continue;

                    IntPtr hmPtr = LlsmBindings.Llsm.GetFrameHM(framePtr);
                    if (hmPtr == IntPtr.Zero) continue;

                    int nhar = LlsmBindings.Llsm.GetHMNHar(hmPtr);
                    nhar = Math.Min(nhar, (int)MathF.Round(8000f / f0));
                    if (nhar <= 0) continue;

                    float[] ampl = LlsmBindings.Llsm.GetHMAmpl(hmPtr, nhar);
                    // 振幅 0 はネイティブ側 log(0) で NaN 距離になるためフロアを敷く
                    for (int h = 0; h < ampl.Length; h++)
                        if (!float.IsFinite(ampl[h]) || ampl[h] < 1e-8f) ampl[h] = 1e-8f;
                    if (lipRadius > 0)
                        NativeLLSM.llsm_lipfilter(lipRadius, f0, nhar, ampl, null!, 1);

                    float estimatedRd = NativeLLSM.llsm_spectral_glottal_fitting(ampl, nhar, glottalModel);
                    if (!float.IsFinite(estimatedRd)) continue;
                    rdPerFrame[i] = Math.Clamp(estimatedRd, 0.3f, 2.7f); // モデルグリッド範囲
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
                if (validRd.Count == 0)
                {
                    _log.Warn(Stage, "All Rd estimates invalid, skipping");
                    return;
                }
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
    /// 無声音減衰（U フラグ）。無声フレームのノイズ PSD を指定 dB だけ減衰させる。
    /// 子音・息のノイズを抑えてクリアにする。
    /// 注: 旧実装が委譲していた AttenuateUnvoiced は VTMAGN を編集していたが、
    /// 合成側は無声フレームで VTMAGN を一切読まない（layer1.c は f0==0 で早期リターン、
    /// 無声音のレベルは NM が全て）ため完全な no-op だった。NM の PSD（dB パワー）を
    /// 直接下げる。時間包絡（edc/eenv）は乗算で別途掛かるため PSD のみで十分。
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

            int applied = 0;
            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                if (LlsmBindings.Llsm.GetFrameF0(frame) > 0) continue; // 無声のみ

                var nm = UtauEngineNg.Llsm.FrameAccess.TryGetNm(frame);
                if (nm is not { HasPsd: true } nmv) continue;

                float[] psd = nmv.ReadPsd();
                for (int j = 0; j < psd.Length; j++) psd[j] -= _attenuationDb;
                nmv.WritePsd(psd);
                applied++;
            }
            _log.Info(Stage, $"U{_attenuationDb}: NM PSD attenuated -{_attenuationDb}dB on {applied} unvoiced frames");
        }
    }

    /// <summary>
    /// 声門閉鎖係数（K フラグ）。有声フレームの Rd（声門波形パラメータ）をスケールして
    /// 声質を調整する。K0=息漏れ声(Rd大)、K50=標準(identity)、K100=硬い声(Rd小)。
    /// 2026-09-16 調整: 旧来は K0 で高域 -13dB・低域 +2dB・雑音不変で「こもる」だけになり、
    /// K100 では Rd が下限に張り付いていた。Rd 倍率を 2.0〜0.5 に穏やかにし、エネルギー補償を
    /// 半分にして低域の持ち上がりを抑え、息漏れ側では気息雑音を増やす（K0 +6dB、K100 -6dB）。
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

        // Rd 変更によるスペクトルティルト変化で倍音全体のエネルギーが落ちる/上がる
        // （tolayer0 は H1 基準正規化のため）。補償は半分だけ掛ける（全量だと低域が持ち上がり
        // こもる）。暴走防止クランプ付き。
        private const float MaxCompensationDb = 6.0f;
        private const float CompensationFraction = 0.5f;
        // 息漏れ側で増やす気息雑音（PSD, dB）: K0 で +NoiseCoupleDb、K100 で -NoiseCoupleDb
        private const float NoiseCoupleDb = 6.0f;

        public void Apply(ChunkHandle chunk, int nfrm, int fs)
        {
            if (!IsActive) return;

            float rdScale = _closure <= 50
                ? 2.0f - (_closure / 50.0f) * 1.0f          // K0..50 : 2.0 -> 1.0
                : 1.0f - ((_closure - 50) / 50.0f) * 0.5f;  // K50..100: 1.0 -> 0.5
            float noiseDb = -NoiseCoupleDb * (_closure - 50) / 50.0f; // K0 +6dB .. K100 -6dB

            var conf = LlsmBindings.Llsm.GetConf(chunk);
            int nspec = LlsmBindings.Llsm.GetConfInt(conf, NativeLLSM.LLSM_CONF_NSPEC);

            int clamped = 0;
            int compensated = 0;
            float gainSum = 0;
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

                // 変更前 Rd での倍音エネルギーを実測（tolayer0 で HM を再生成）
                NativeLLSM.llsm_frame_tolayer0(frame.Ptr, conf.Ptr);
                double e0 = HarmonicEnergy(frame);

                Marshal.StructureToPtr(boundedRd, rdPtr, false);

                // 変更後 Rd での倍音エネルギー
                NativeLLSM.llsm_frame_tolayer0(frame.Ptr, conf.Ptr);
                double e1 = HarmonicEnergy(frame);

                // エネルギー総和を保存するゲインを VTMAGN に付与（ノイズ成分 NM は不変）。
                // 実声では息漏れ声でも声量はほぼ保たれるため、無補償だと倍音だけが
                // 落ちて N/S 比が悪化し「シャー」というノイズ感が出る。
                if (e0 > 0 && e1 > 0)
                {
                    float gainDb = Math.Clamp(
                        (float)(10.0 * Math.Log10(e0 / e1)) * CompensationFraction,
                        -MaxCompensationDb, MaxCompensationDb);
                    var vtmagn = LlsmBindings.Llsm.GetFrameVtMagn(frame, nspec);
                    for (int j = 0; j < nspec; j++)
                        vtmagn[j] = Math.Max(vtmagn[j] + gainDb, -80.0f);
                    LlsmBindings.Llsm.SetFrameVtMagn(frame, vtmagn);
                    gainSum += gainDb;
                    compensated++;
                }

                // 気息雑音の連動（有声フレームの NM PSD）
                if (MathF.Abs(noiseDb) > 0.01f)
                {
                    var nm = UtauEngineNg.Llsm.FrameAccess.TryGetNm(frame);
                    if (nm is { HasPsd: true } nmv)
                    {
                        float[] psd = nmv.ReadPsd();
                        for (int j = 0; j < psd.Length; j++) psd[j] += noiseDb;
                        nmv.WritePsd(psd);
                    }
                }
            }
            float gainAvg = compensated > 0 ? gainSum / compensated : 0;
            _log.Info(Stage, $"K{_closure} applied (Rd scale {rdScale:F2}, clamped {clamped}/{nfrm}, energy comp avg {gainAvg:+0.0;-0.0}dB on {compensated} frames, noise {noiseDb:+0.0;-0.0}dB)");
        }

        private static double HarmonicEnergy(ContainerRef frame)
        {
            IntPtr hm = LlsmBindings.Llsm.GetFrameHM(frame);
            if (hm == IntPtr.Zero) return 0;
            int nhar = LlsmBindings.Llsm.GetHMNHar(hm);
            if (nhar <= 0) return 0;
            float[] ampl = LlsmBindings.Llsm.GetHMAmpl(hm, nhar);
            double e = 0;
            for (int k = 0; k < nhar; k++) e += (double)ampl[k] * ampl[k];
            return e;
        }
    }
}
