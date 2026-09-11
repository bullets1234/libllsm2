using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Audio;
using UtauEngineNg.Diagnostics;
using UtauEngineNg.Effects;
using UtauEngineNg.Llsm;
using UtauEngineNg.Pitch;

namespace UtauEngineNg.Synthesis
{
    /// <summary>合成結果（出力波形 + 分解成分）。</summary>
    public sealed class SynthesisResult
    {
        public required float[] Output { get; init; }
        public float[]? Sinusoid { get; init; }
        public float[]? Noise { get; init; }
    }

    /// <summary>
    /// 標準タイムストレッチ合成。子音部は velocity、伸縮部は均一展開で各出力フレームを
    /// 隣接ソースから補間（フリーズ／反復を回避）し、ピッチシフト・各種エフェクトを適用して
    /// Layer0（または Growl 時 Layer1）で合成する。
    /// （UtauEngine SynthesizeWithConsonantAndStretch の再構成。検証済み DSP を保持）
    /// </summary>
    public sealed class StandardSynthesizer
    {
        private const string Stage = "Synthesis";
        private const int Nfft = 16384;       // 2x オーバーサンプリング解析対応
        private const float VtmagnFloorDb = -80f;

        // ピッチダウン時の VSPHSE 高域拡張の A/B 用トグル（L2R_VSEXT=0 で無効化）。
        // 拡張位相が金属的リングの原因か切り分けるための診断スイッチ
        private static readonly bool VsphseExtensionEnabled =
            Environment.GetEnvironmentVariable("L2R_VSEXT") != "0";

        private readonly DiagnosticsContext _diag;

        public StandardSynthesizer(DiagnosticsContext diag) => _diag = diag;

        public SynthesisResult Synthesize(ChunkHandle srcChunk, int fs, SynthesisParams p)
        {
            var log = _diag.Log;
            float srcF0 = p.SrcF0, targetF0 = p.TargetF0;
            float pitchShiftRatio = targetF0 / srcF0;

            var unwrappedPb = PitchBendGrid.Unwrap(p.PitchBend);
            int srcNfrm = LlsmBindings.Llsm.GetNumFrames(srcChunk);

            // L フラグ: 声門パラメータ自動推定（Layer1 変換前、HM が存在する状態）
            new GlottalEstimateEffect(p.UseGlottalAutoEstimate, log).Apply(srcChunk, srcNfrm, fs);

            // 倍音トレース診断（L2R_HMTRACE）: 分析済みHM（Layer1変換前）
            if (HarmonicTracer.Enabled) HarmonicTracer.TraceHm(srcChunk, srcNfrm, "pre-layer1", log);

            // Layer1 へ変換 + スペクトルダウンサンプル + 逆位相伝播
            using (_diag.Profiler.Measure("to_layer1"))
            {
                // 残差包絡補正: Layer1包絡フィットで失われる高域倍音振幅（実測2k-16kHzで
                // -3〜-7dB）を残差包絡としてVTMAGNへ還元するため、変換前のHM振幅を保存する。
                // L2R_RESIDUAL=0 で無効化（A/B用）。
                bool residualCorrectionEnabled =
                    Environment.GetEnvironmentVariable("L2R_RESIDUAL") != "0" && !p.DisableResidualCorrection;
                float[][]? residualSnapshots = null;
                float[]? residualF0s = null;
                if (residualCorrectionEnabled)
                    residualSnapshots = ResidualEnvelopeCorrector.Snapshot(srcChunk, srcNfrm, out residualF0s);

                LlsmBindings.Llsm.ChunkToLayer1(srcChunk, Nfft);
                SpectrumDownsampler.Apply(srcChunk, fs, log);

                // 倍音トレース診断: Layer1変換+ダウンサンプル直後（逆位相伝播前）
                if (HarmonicTracer.Enabled)
                {
                    HarmonicTracer.TraceHm(srcChunk, srcNfrm, "post-layer1-hm", log);
                    HarmonicTracer.TraceVtmagn(srcChunk, srcNfrm, "post-layer1", log, fs);
                }

                LlsmBindings.Llsm.ChunkPhasePropagate(srcChunk, -1);

                // 分析ジッタ除去（倍音±フレームレートの AM/PM サイドバンド＝ジリジリ音対策）。
                // tolayer1 の vs_phse = phse - vt_phse(最小位相) はノイズフロア帯の乱れを
                // 全域に拡散させるため、時間方向の円環/移動平均で除去する。
                // 補正量が閾値を超えるビン（＝実際の音声変化）は素通し。L2R_SMOOTH=0 で無効化（A/B用）。
                if (Environment.GetEnvironmentVariable("L2R_SMOOTH") != "0" && !p.DisableVsphseSmoother)
                {
                    VsphseSmoother.Apply(srcChunk, srcNfrm);
                    VsphseSmoother.SmoothVtmagn(srcChunk, srcNfrm);

                    // 倍音トレース診断: VsphseSmoother 適用後
                    if (HarmonicTracer.Enabled) HarmonicTracer.TraceVtmagn(srcChunk, srcNfrm, "post-smoother", log, fs);
                }

                // 残差包絡補正: 上記スムージング等まで完了した最終状態のVTMAGNへ加算する。
                if (residualCorrectionEnabled && residualSnapshots != null && residualF0s != null)
                {
                    ResidualEnvelopeCorrector.Apply(srcChunk, srcNfrm, residualSnapshots, residualF0s, log);

                    // 倍音トレース診断: ResidualEnvelopeCorrector.Apply 後
                    if (HarmonicTracer.Enabled) HarmonicTracer.TraceVtmagn(srcChunk, srcNfrm, "post-residual", log, fs);
                }
            }

            // ストレッチ計算（末尾不安定フレームを保護）
            int stretchableFrames = srcNfrm - p.ConsonantFrames;
            int tailMargin = Math.Min(3, stretchableFrames / 4);
            int effectiveStretchableFrames = Math.Max(1, stretchableFrames - tailMargin);
            int dstConsonantFrames = (int)Math.Round(p.ConsonantFrames * p.ConsonantStretch);
            int dstStretchedFrames = (int)Math.Round(stretchableFrames * p.StretchRatio);
            int dstNfrm = dstConsonantFrames + dstStretchedFrames;

            float[] interpolatedPb = PitchBendTimeline.Interpolate(unwrappedPb, dstNfrm, p.Tempo, p.ThopSeconds);

            // dst チャンク生成
            var conf = LlsmBindings.Llsm.GetConf(srcChunk);
            var confCopy = LlsmBindings.Llsm.CopyContainer(conf);
            var nfrmPtr = NativeLLSM.llsm_container_get(confCopy.Ptr, NativeLLSM.LLSM_CONF_NFRM);
            Marshal.WriteInt32(nfrmPtr, dstNfrm);
            using var dstChunk = LlsmBindings.Llsm.CreateChunk(confCopy, 0);
            // llsm_create_chunk は conf をディープコピーする（container.c）ため、
            // confCopy はここで解放する（旧実装は合成毎にコンテナ一式をリーク）
            NativeLLSM.llsm_delete_container(confCopy.Ptr);

            // トランジェント検出（子音保護用）
            var srcFrameList = new List<ContainerRef>(srcNfrm);
            for (int i = 0; i < srcNfrm; i++) srcFrameList.Add(LlsmBindings.Llsm.GetFrame(srcChunk, i));
            bool[] isTransient = TransientDetector.Detect(srcFrameList);

            float[] dstF0 = new float[dstNfrm];
            log.Info(Stage, $"Pitch shift {srcF0:F1}->{targetF0:F1}Hz (ratio={pitchShiftRatio:F4}, comp=bend-relative only), {dstNfrm} frames");

            var breathiness = new BreathinessEffect(p.Breathiness);
            var gender = new GenderEffect(p.GenderFactor);
            var formantFollow = new FormantFollowEffect(p.FormantFollow);
            var voiceQuality = new VoiceQualityCurveEffect(p.VoiceQuality, srcF0, p.ThopSeconds, dstNfrm, fs);

            // NM テクスチャ実時間転写（冷凍ノイズ対策）。L2R_NMTEX=0 / N16 で無効化（A/B 用）
            bool noiseTextureEnabled = Environment.GetEnvironmentVariable("L2R_NMTEX") != "0" && !p.DisableNoiseTexture;
            var noiseTexture = new NoiseTextureTransfer(srcChunk, srcNfrm, noiseTextureEnabled, log);
            float consonantLocalStretch = p.ConsonantFrames > 0 ? (float)dstConsonantFrames / p.ConsonantFrames : 1f;
            float vowelLocalStretch = (float)dstStretchedFrames / effectiveStretchableFrames;

            using (_diag.Profiler.Measure("frame_interp_loop"))
            {
                for (int i = 0; i < dstNfrm; i++)
                {
                    float srcPosFloat = i < dstConsonantFrames
                        ? (float)((double)i / dstConsonantFrames * p.ConsonantFrames)
                        : (float)((double)p.ConsonantFrames + (double)(i - dstConsonantFrames) / dstStretchedFrames * effectiveStretchableFrames);

                    int srcIdx1 = (int)Math.Floor(srcPosFloat);
                    int srcIdx2 = srcIdx1 + 1;
                    float ratio = srcPosFloat - srcIdx1;
                    srcIdx1 = Math.Clamp(srcIdx1, 0, srcNfrm - 1);
                    srcIdx2 = Math.Clamp(srcIdx2, 0, srcNfrm - 1);

                    bool isConsonant = i < dstConsonantFrames;
                    bool isSrcTransient1 = srcIdx1 < isTransient.Length && isTransient[srcIdx1];
                    bool isSrcTransient2 = srcIdx2 < isTransient.Length && isTransient[srcIdx2];
                    bool isTransientRegion = (isSrcTransient1 || isSrcTransient2) && isConsonant;

                    IntPtr newFramePtr = InterpolateFrameAt(srcChunk, srcIdx1, srcIdx2, ratio, srcNfrm, i, isTransientRegion);

                    // 息テクスチャ（PSDRES / edc 残差）を実時間カーソルから転写。
                    // トランジェント区間は原音フレームをそのまま使うため対象外。
                    if (!isTransientRegion)
                        noiseTexture.Apply(newFramePtr, srcPosFloat, srcIdx1, srcIdx2, ratio,
                            isConsonant ? consonantLocalStretch : vowelLocalStretch);

                    var newFrameRef = new ContainerRef(newFramePtr);
                    float originalF0 = LlsmBindings.Llsm.GetFrameF0(newFrameRef);

                    // 息成分（フレーム前段）
                    breathiness.Apply(new FrameEffectContext { Frame = newFrameRef, OutFrameIdx = i, PitchRatio = pitchShiftRatio, Fs = fs });

                    if (originalF0 > 0)
                    {
                        float dynamicMod = p.UseModPlus
                            ? PitchBendTimeline.DynamicModulation(i, dstNfrm, p.Modulation, p.ThopSeconds, p.OverlapMs)
                            : p.Modulation;

                        float logDeviation = MathF.Log(originalF0 / srcF0);
                        float maxLogDev = 6.0f * MathF.Log(2.0f) / 12.0f;
                        logDeviation = Math.Clamp(logDeviation, -maxLogDev, maxLogDev);
                        float adjustedSourceF0 = srcF0 * MathF.Exp(logDeviation * (dynamicMod / 100.0f));
                        float newF0 = adjustedSourceF0 * pitchShiftRatio;

                        if (interpolatedPb.Length > 0 && i < interpolatedPb.Length)
                            newF0 *= (float)Math.Pow(2, interpolatedPb[i] / 1200.0);

                        // マイクロプロソディ: 原音由来のジッター/シマーを「出力時間軸に
                        // 等速」で再適用する（ストレッチしても揺れの速度が変わらない）。
                        // モジュレーションと独立に mod=0 でも声の微細な生気を保つ。
                        float shimmerDb = 0;
                        if (p.JitterDepth > 0)
                        {
                            float depth = p.JitterDepth / 100f;
                            float jitterCents = p.Micro.PitchAt(i) * depth;
                            if (jitterCents != 0)
                                newF0 *= MathF.Pow(2f, jitterCents / 1200f);
                            shimmerDb = p.Micro.ShimmerAt(i) * depth;
                        }

                        // 振幅補正: フレーム毎の実効ピッチ比（ベンド込み）で -20log10(ratio)。
                        // この補償は実測で合成レベルをほぼピッチ中立にする（2 オクターブで
                        // 差 ~1dB）。除去すると母音が -3dB/oct 下がり、基準レベル正規化が
                        // ノート全体を持ち上げる際にピッチ非依存の子音バーストのピークが
                        // 天井へ達し、リミッタ作動でレベル一貫性が崩れる（実測で確認済み）。
                        float frameRatio = Math.Clamp(newF0 / originalF0, 0.25f, 4.0f);
                        float vtmagnCompensation = -20.0f * MathF.Log10(frameRatio) + shimmerDb;
                        var vtmagnPtr = NativeLLSM.llsm_container_get(newFramePtr, NativeLLSM.LLSM_FRAME_VTMAGN);
                        if (vtmagnPtr != IntPtr.Zero)
                        {
                            int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                            float[] vtmagn = new float[nspec];
                            Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                            for (int j = 0; j < nspec; j++)
                                vtmagn[j] = Math.Max(vtmagn[j] + vtmagnCompensation, VtmagnFloorDb);
                            Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
                        }

                        NativeCallbacks.AttachF0(newFramePtr, newF0);

                        // ピッチダウン時は Layer0 変換の倍音数が len(vsphse) で頭打ちになるため
                        // 必要本数まで拡張する（不要なら関数側で no-op。
                        // N1 フラグ / L2R_VSEXT=0 で A/B 可）
                        if (VsphseExtensionEnabled && !p.DisableVsphseExtension)
                            FrameInterpolator.ExtendVsphseForPitchShift(newFramePtr, newF0, fs);

                        dstF0[i] = newF0;
                    }
                    else dstF0[i] = 0;

                    // ジェンダー / フォルマント追従
                    var fctx = new FrameEffectContext { Frame = newFrameRef, OutFrameIdx = i, PitchRatio = pitchShiftRatio, Fs = fs };
                    gender.Apply(fctx);
                    formantFollow.Apply(fctx);

                    // 動的声質カーブ（Q フラグ・試験実装、Layer0 変換前に Rd/明るさ/息を変調）
                    voiceQuality.ApplyFrame(newFramePtr, i, dstF0[i]);

                    LlsmBindings.Llsm.SetFrame(dstChunk, i, newFramePtr);
                }
            }

            voiceQuality.LogSummary(log);
            noiseTexture.LogSummary();

            // 倍音トレース診断: フレーム補間ループ完了後
            if (HarmonicTracer.Enabled) HarmonicTracer.TraceVtmagn(dstChunk, dstNfrm, "post-interp", log, fs);

            // V/UV 境界の eenv フェード（ポップノイズ防止）
            new NoiseModelProcessor(log).FadeEenvAtVuvBoundaries(dstChunk, dstF0, dstNfrm);

            // チャンクレベルエフェクト
            new SpectralTiltEffect(p.SpectralTilt).Apply(dstChunk, dstNfrm, fs);
            new GlottalClosureEffect(p.GlottalClosure, log).Apply(dstChunk, dstNfrm, fs);
            new UnvoicedAttenuationEffect(p.UnvoicedAttenuation, log).Apply(dstChunk, dstNfrm, fs);

            // GrowlEffect はネイティブコールバックの寿命を握るため、合成完了
            //（Render 戻り）まで生存させてから Dispose する
            using var growl = new GrowlEffect(p.GrowlStrength, log);
            growl.Apply(dstChunk, dstNfrm);
            bool useLayer1Synthesis = growl.IsActive;

            // Layer0 変換（Growl 時はスキップ）
            if (!useLayer1Synthesis)
            {
                using (_diag.Profiler.Measure("to_layer0"))
                {
                    LlsmBindings.Llsm.ChunkToLayer0(dstChunk);
                    NativeLLSM.llsm_chunk_phasesync_rps(dstChunk.DangerousGetHandle(), 1);
                    LlsmBindings.Llsm.ChunkPhasePropagate(dstChunk, +1);
                }

                // 倍音トレース診断: Layer0変換+位相同期+位相伝播直後
                if (HarmonicTracer.Enabled) HarmonicTracer.TraceHm(dstChunk, dstNfrm, "post-layer0", log);
                if (HarmonicTracer.Enabled) HarmonicTracer.TracePhaseCoherence(dstChunk, dstNfrm, "post-layer0", log, p.ThopSeconds);
            }
            else log.Info(Stage, "Keeping Layer1 for PBP synthesis (Growl active)");

            var result = Render(dstChunk, fs, p.UseOversampling, useLayer1Synthesis, log);
            // JIT の生存解析による srcChunk の早期 finalize（=ネイティブ解放）防止
            GC.KeepAlive(srcChunk);
            return result;
        }

        /// <summary>位置 srcPos のフレームを補間生成する（整数/境界/トランジェント/4点/2点）。</summary>
        private static IntPtr InterpolateFrameAt(ChunkHandle srcChunk, int srcIdx1, int srcIdx2, float ratio, int srcNfrm, int outIdx, bool isTransientRegion)
        {
            if (srcIdx1 == srcIdx2 || ratio < 0.01f)
                return LlsmBindings.Llsm.CopyFrame(LlsmBindings.Llsm.GetFrame(srcChunk, srcIdx1));
            if (ratio > 0.99f)
                return LlsmBindings.Llsm.CopyFrame(LlsmBindings.Llsm.GetFrame(srcChunk, srcIdx2));

            if (isTransientRegion)
            {
                int nearestIdx = ratio < 0.5f ? srcIdx1 : srcIdx2;
                return LlsmBindings.Llsm.CopyFrame(LlsmBindings.Llsm.GetFrame(srcChunk, nearestIdx));
            }

            bool canUseCubic = srcIdx1 > 0 && srcIdx2 < srcNfrm - 1;
            if (canUseCubic)
            {
                return FrameInterpolator.Interpolate4(
                    LlsmBindings.Llsm.GetFrame(srcChunk, srcIdx1 - 1),
                    LlsmBindings.Llsm.GetFrame(srcChunk, srcIdx1),
                    LlsmBindings.Llsm.GetFrame(srcChunk, srcIdx2),
                    LlsmBindings.Llsm.GetFrame(srcChunk, srcIdx2 + 1),
                    ratio, outIdx);
            }
            return FrameInterpolator.Interpolate2(
                LlsmBindings.Llsm.GetFrame(srcChunk, srcIdx1),
                LlsmBindings.Llsm.GetFrame(srcChunk, srcIdx2),
                ratio, outIdx);
        }

        /// <summary>Layer0/Layer1 チャンクを波形へ合成する（O フラグ時は 4x オーバーサンプリング）。</summary>
        private SynthesisResult Render(ChunkHandle dstChunk, int fs, bool useOversampling, bool useLayer1Synthesis, ILogger log)
        {
            bool dump = _diag.Dump.Enabled;
            using (_diag.Profiler.Measure("llsm_synthesize"))
            {
                if (useOversampling)
                {
                    const int oversampleRate = 4;
                    int synthesisFs = fs * oversampleRate;
                    log.Info(Stage, $"Oversampling {oversampleRate}x ({synthesisFs}Hz)");
                    using var sopt = LlsmBindings.Llsm.CreateSynthesisOptions(synthesisFs);
                    SetUseL1(sopt, useLayer1Synthesis);
                    using var output = LlsmBindings.Llsm.Synthesize(sopt, dstChunk);

                    if (dump)
                    {
                        var (y, ySin, yNoise) = LlsmBindings.Llsm.ReadOutputDecomposed(output);
                        return new SynthesisResult
                        {
                            Output = Resampling.Downsample(y, oversampleRate),
                            Sinusoid = Resampling.Downsample(ySin, oversampleRate),
                            Noise = Resampling.Downsample(yNoise, oversampleRate),
                        };
                    }
                    return new SynthesisResult { Output = Resampling.Downsample(LlsmBindings.Llsm.ReadOutput(output), oversampleRate) };
                }
                else
                {
                    log.Info(Stage, $"Direct synthesis at {fs}Hz");
                    using var sopt = LlsmBindings.Llsm.CreateSynthesisOptions(fs);
                    SetUseL1(sopt, useLayer1Synthesis);
                    using var output = LlsmBindings.Llsm.Synthesize(sopt, dstChunk);

                    if (dump)
                    {
                        var (y, ySin, yNoise) = LlsmBindings.Llsm.ReadOutputDecomposed(output);
                        return new SynthesisResult { Output = y, Sinusoid = ySin, Noise = yNoise };
                    }
                    return new SynthesisResult { Output = LlsmBindings.Llsm.ReadOutput(output) };
                }
            }
        }

        private static unsafe void SetUseL1(SOptionsHandle sopt, bool useLayer1Synthesis)
        {
            if (!useLayer1Synthesis) return;
            var soptPtr = (NativeLLSM.llsm_soptions*)sopt.DangerousGetHandle().ToPointer();
            soptPtr->use_l1 = 1;
        }
    }
}
