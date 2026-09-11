using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// 雑音モデル(NM)テクスチャの実時間転写。「冷凍ノイズ」対策。
    ///
    /// libllsm2 のノイズは 2 層で表現される:
    ///   - 傾向: PSD（Kalman で時間平滑した包絡, layer0.c llsm_analyze_noise_psd）, edc, eenv
    ///   - テクスチャ: PSDRES（各フレームの微細な残差。log-χ² 分布で標準偏差 5〜6dB）
    /// 合成時 (llsm_filter_noise) は PSD + PSDRES で白色雑音を整形するため、PSDRES が
    /// 息の「生きた」質感を担う。ところがタイムストレッチでは <see cref="FrameInterpolator"/> が
    /// PSDRES を基準フレームからコピーするだけなので、伸長比 N なら同一の微細スペクトル
    /// パターンが N/2 フレーム固定 → 切替 を繰り返し、静止したヒスとして可聴化する。
    /// edc（帯域エネルギー）の 5〜20Hz の揺らぎも N 倍遅くなり、息が止まって聞こえる。
    ///
    /// 本クラスは NM の時間軸を分離する:
    ///   - 傾向（PSD / edc / eenv 変調深度 の ±75ms 局所平均）は従来通り伸長位置で補間された値を使う
    ///   - テクスチャ（PSDRES そのもの、および PSD(dB)・edc(log)・eenv 深度(log) の局所平均残差）は
    ///     「出力 1 フレームにつき原音 1 フレーム進むカーソル」から取る。カーソルは伸長位置
    ///     ±<see cref="WindowFrames"/> の範囲内をパリンドローム（往復）し、出力フレームと
    ///     有声/無声が一致する原音フレームのみ参照する。
    /// 伸長比 ≤ 1.05 の区間では何もせず、従来と同一出力になる（恒等、バイト一致を確認済み）。
    /// 全処理は決定論的。L2R_NMTEX=0 / N16 フラグで無効化（A/B 用）。
    /// 計測: diag/noise_texture.py（微細構造のフレーム間相関、帯域包絡の変調配分）。
    /// </summary>
    public sealed class NoiseTextureTransfer
    {
        private const string Stage = "NmTexture";

        /// <summary>テクスチャカーソルが伸長位置から離れてよい範囲（フレーム、5ms 単位 → ±100ms）。</summary>
        private const int WindowFrames = 20;
        /// <summary>傾向/残差分離の局所平均半窓（フレーム）。±75ms → 遮断約 6.5Hz（MicroProsody と同一）。±150ms も試したが計測差なし。</summary>
        private const int EdcMeanRadius = 15;
        /// <summary>edc 残差（log パワー）のクランプ。±2.0 ≈ ±8.7dB。</summary>
        private const float EdcResidualClamp = 2.0f;
        private const float EdcFloor = 1e-9f;
        /// <summary>PSD 残差（dB）のクランプ。Kalman 平滑後の PSD が持つ速い変動分。</summary>
        private const float PsdResidualClamp = 6.0f;
        /// <summary>eenv 変調深度 log 残差のクランプ。±0.7 ≈ ×0.5〜×2。</summary>
        private const float EenvResidualClamp = 0.7f;
        /// <summary>
        /// eenv 変調深度の上限（Σ|ampl| ≤ edc × この値）。
        /// <see cref="NoiseModelProcessor"/> の解析時クランプと同一値。転写で深度が
        /// これを超えるとパルス同期が壊れ、非同期のザラつきになるため合成側でも守る。
        /// </summary>
        private const float EenvDepthLimit = 2.5f;
        /// <summary>
        /// これ以下の局所伸長比は恒等扱い（カーソル＝伸長位置）。等倍でも末尾保護
        /// (tailMargin) で比が 1.03 程度になるため、僅かな比でテクスチャがずれないようにする。
        /// </summary>
        private const float IdentityStretchThreshold = 1.05f;

        private readonly ILogger _log;
        private readonly ChunkHandle _src;
        private readonly int _srcNfrm;
        private readonly bool[] _srcVoiced;
        /// <summary>フレーム毎・チャンネル毎の edc 残差（log パワー、局所平均との差）。edc 不在なら null。</summary>
        private readonly float[]?[] _edcResidual;
        /// <summary>フレーム毎・ビン毎の PSD 残差（dB、局所平均との差）。PSD 不在なら null。</summary>
        private readonly float[]?[] _psdResidual;
        /// <summary>フレーム毎・チャンネル毎の eenv 変調深度 log(Σ|ampl|) 残差。eenv 不在なら null。</summary>
        private readonly float[]?[] _eenvResidual;

        private int _cursor = -1;
        private int _dir = +1;

        // 統計（ログ用）
        private int _applied, _psdresTransferred, _psdTransferred, _edcTransferred, _eenvTransferred, _voicingFallback, _maxDrift;

        public bool IsActive { get; }

        public NoiseTextureTransfer(ChunkHandle srcChunk, int srcNfrm, bool enabled, ILogger log)
        {
            _log = log;
            _src = srcChunk;
            _srcNfrm = srcNfrm;
            IsActive = enabled && srcNfrm > 0;
            _srcVoiced = new bool[srcNfrm];
            _edcResidual = new float[]?[srcNfrm];
            _psdResidual = new float[]?[srcNfrm];
            _eenvResidual = new float[]?[srcNfrm];
            if (!IsActive) return;

            // 有声フラグ・log(edc)・PSD(dB) を先に読む
            var logEdc = new float[]?[srcNfrm];
            var psd = new float[]?[srcNfrm];
            var logEenv = new float[]?[srcNfrm];
            for (int i = 0; i < srcNfrm; i++)
            {
                var fr = LlsmBindings.Llsm.GetFrame(srcChunk, i);
                _srcVoiced[i] = LlsmBindings.Llsm.GetFrameF0(fr) > 0;
                var nm = FrameAccess.TryGetNm(fr);
                if (nm is not { } nmv) continue;
                if (nmv.HasEdc)
                {
                    float[] edc = nmv.ReadEdc();
                    var le = new float[edc.Length];
                    for (int c = 0; c < edc.Length; c++) le[c] = MathF.Log(MathF.Max(edc[c], EdcFloor));
                    logEdc[i] = le;
                }
                if (nmv.HasPsd) psd[i] = nmv.ReadPsd();
                if (nmv.HasEenv && _srcVoiced[i])
                {
                    var le = new float[nmv.NChannel];
                    bool any = false;
                    for (int c = 0; c < nmv.NChannel; c++)
                    {
                        float depth = EenvDepth(nmv, c);
                        if (depth > 0) { le[c] = MathF.Log(depth); any = true; }
                        else le[c] = float.NaN;
                    }
                    if (any) logEenv[i] = le;
                }
            }

            // 同一有声性の近傍 ±EdcMeanRadius の局所平均との差を残差とする
            _edcResidual = ComputeResiduals(logEdc, EdcResidualClamp);
            _psdResidual = ComputeResiduals(psd, PsdResidualClamp);
            _eenvResidual = ComputeResiduals(logEenv, EenvResidualClamp);
        }

        /// <summary>チャンネル c の eenv 変調深度 Σ|ampl|（0 なら eenv なし）。</summary>
        private static float EenvDepth(NmView nm, int c)
        {
            var ev = nm.GetEenvChannel(c);
            if (ev is not { HasAmplitudes: true } e) return 0f;
            float[] a = e.ReadAmplitudes();
            float sum = 0;
            for (int h = 0; h < a.Length; h++) sum += MathF.Abs(a[h]);
            return sum;
        }

        /// <summary>
        /// 各フレームの系列（edc / PSD）から、同一有声性の近傍 ±<see cref="EdcMeanRadius"/> の
        /// 局所平均を引いた残差を作る。近傍が 3 未満のフレームは null（転写しない）。
        /// </summary>
        private float[]?[] ComputeResiduals(float[]?[] series, float clamp)
        {
            int n = series.Length;
            var result = new float[]?[n];
            for (int i = 0; i < n; i++)
            {
                var cur = series[i];
                if (cur == null) continue;
                int len = cur.Length;
                var sum = new double[len];
                var cntc = new int[len];
                for (int k = Math.Max(0, i - EdcMeanRadius); k <= Math.Min(n - 1, i + EdcMeanRadius); k++)
                {
                    var sk = series[k];
                    if (sk == null || sk.Length != len || _srcVoiced[k] != _srcVoiced[i]) continue;
                    for (int c = 0; c < len; c++)
                        if (float.IsFinite(sk[c])) { sum[c] += sk[c]; cntc[c]++; }
                }
                var res = new float[len];
                bool any = false;
                for (int c = 0; c < len; c++)
                {
                    if (cntc[c] < 3 || !float.IsFinite(cur[c])) { res[c] = 0f; continue; }
                    res[c] = Math.Clamp(cur[c] - (float)(sum[c] / cntc[c]), -clamp, clamp);
                    any = true;
                }
                if (any) result[i] = res;
            }
            return result;
        }

        /// <summary>
        /// 出力フレーム <paramref name="dstFramePtr"/>（補間済み）へテクスチャを転写する。
        /// </summary>
        /// <param name="srcPos">出力フレームに対応する原音フレーム位置（実数）。</param>
        /// <param name="srcIdx1">補間に使った原音フレーム（下側）。</param>
        /// <param name="srcIdx2">補間に使った原音フレーム（上側）。</param>
        /// <param name="ratio">srcIdx1→srcIdx2 の補間比。</param>
        /// <param name="localStretch">この区間の伸長比（出力フレーム数/原音フレーム数）。≤1 なら恒等。</param>
        public void Apply(IntPtr dstFramePtr, float srcPos, int srcIdx1, int srcIdx2, float ratio, float localStretch)
        {
            if (!IsActive) return;

            bool outVoiced = ReadF0(dstFramePtr) > 0;
            int nearest = Math.Clamp((int)MathF.Round(srcPos), 0, _srcNfrm - 1);

            if (localStretch <= IdentityStretchThreshold)
            {
                // 圧縮・等倍（±5%）: 原音は出力とほぼ同速以上で進むのでテクスチャは補間器の
                // 基準フレーム由来のままでよい。何も触らず従来と同一出力にする（恒等）。
                _cursor = nearest;
                _dir = +1;
                return;
            }

            int tex = AdvanceCursor(srcPos, outVoiced, nearest);
            _applied++;
            _maxDrift = Math.Max(_maxDrift, Math.Abs(tex - nearest));

            var texFrame = LlsmBindings.Llsm.GetFrame(_src, tex);

            // 1. PSDRES: テクスチャフレームのものをそのまま載せ替える
            var psdresPtr = NativeLLSM.llsm_container_get(texFrame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES);
            if (psdresPtr != IntPtr.Zero && NativeLLSM.llsm_fparray_length(psdresPtr) > 0)
            {
                NativeCallbacks.AttachFpArrayCopy(dstFramePtr, NativeLLSM.LLSM_FRAME_PSDRES, psdresPtr);
                _psdresTransferred++;
            }

            var nmOut = FrameAccess.TryGetNm(new ContainerRef(dstFramePtr));
            if (nmOut is not { } nmv) return;
            var res1Edc = IndexOrNull(_edcResidual, srcIdx1);
            var res2Edc = IndexOrNull(_edcResidual, srcIdx2);
            var res1Psd = IndexOrNull(_psdResidual, srcIdx1);
            var res2Psd = IndexOrNull(_psdResidual, srcIdx2);

            // 2. PSD(dB): 補間値から「補間位置の残差」を除き「テクスチャ位置の残差」を載せる
            //    psd_out = psd_interp - lerp(res[idx1], res[idx2]) + res[tex]
            var resTexPsd = _psdResidual[tex];
            if (resTexPsd != null && nmv.HasPsd)
            {
                float[] p = nmv.ReadPsd();
                if (p.Length == resTexPsd.Length)
                {
                    for (int b = 0; b < p.Length; b++)
                        p[b] += resTexPsd[b] - Lerp(res1Psd, res2Psd, b, p.Length, ratio);
                    nmv.WritePsd(p);
                    _psdTransferred++;
                }
            }

            // 3. edc(log パワー): 同様に残差を付け替える
            //    edc_out = exp( log(edc_interp) - lerp(res[idx1], res[idx2]) + res[tex] )
            var resTexEdc = _edcResidual[tex];
            if (resTexEdc != null && nmv.HasEdc)
            {
                float[] edc = nmv.ReadEdc();
                if (edc.Length == resTexEdc.Length)
                {
                    bool changed = false;
                    for (int c = 0; c < edc.Length; c++)
                    {
                        if (edc[c] <= 0) continue;
                        float delta = resTexEdc[c] - Lerp(res1Edc, res2Edc, c, edc.Length, ratio);
                        float v = edc[c] * MathF.Exp(delta);
                        if (float.IsFinite(v)) { edc[c] = v; changed = true; }
                    }
                    if (changed) { nmv.WriteEdc(edc); _edcTransferred++; }
                }
            }

            // 4. eenv 変調深度: チャンネル毎に Σ|ampl| の log 残差を付け替える（振幅を一様スケール）
            var resTexEenv = _eenvResidual[tex];
            if (resTexEenv != null && nmv.HasEenv && outVoiced)
            {
                var res1E = IndexOrNull(_eenvResidual, srcIdx1);
                var res2E = IndexOrNull(_eenvResidual, srcIdx2);
                float[]? edcNow = nmv.HasEdc ? nmv.ReadEdc() : null;
                bool changed = false;
                for (int c = 0; c < Math.Min(nmv.NChannel, resTexEenv.Length); c++)
                {
                    float delta = resTexEenv[c] - Lerp(res1E, res2E, c, resTexEenv.Length, ratio);
                    if (MathF.Abs(delta) < 1e-6f) continue;
                    var ev = nmv.GetEenvChannel(c);
                    if (ev is not { HasAmplitudes: true } e) continue;
                    float scale = MathF.Exp(delta);
                    float[] a = e.ReadAmplitudes();
                    float sum = 0;
                    for (int h = 0; h < a.Length; h++) sum += MathF.Abs(a[h]);
                    // 変調深度の上限（解析側クランプと同じ）を超えない範囲に制限
                    if (edcNow != null && c < edcNow.Length && edcNow[c] > 0 && sum > 0)
                        scale = MathF.Min(scale, EenvDepthLimit * edcNow[c] / sum);
                    if (MathF.Abs(scale - 1f) < 1e-6f) continue;
                    for (int h = 0; h < a.Length; h++) a[h] *= scale;
                    e.WriteAmplitudes(a);
                    changed = true;
                }
                if (changed) _eenvTransferred++;
            }
        }

        private static float[]? IndexOrNull(float[]?[] arr, int idx) =>
            idx >= 0 && idx < arr.Length ? arr[idx] : null;

        private static float Lerp(float[]? a, float[]? b, int i, int len, float ratio)
        {
            float va = a != null && a.Length == len ? a[i] : 0f;
            float vb = b != null && b.Length == len ? b[i] : 0f;
            return va * (1f - ratio) + vb * ratio;
        }

        /// <summary>
        /// カーソルを 1 フレーム進め、伸長位置 ±WindowFrames の窓内で往復させる。
        /// 有声性が出力フレームと一致する原音フレームのみ返す（無ければ nearest）。
        /// </summary>
        private int AdvanceCursor(float srcPos, bool outVoiced, int nearest)
        {
            int lo = Math.Max(0, (int)MathF.Floor(srcPos) - WindowFrames);
            int hi = Math.Min(_srcNfrm - 1, (int)MathF.Ceiling(srcPos) + WindowFrames);

            if (_cursor < 0) { _cursor = nearest; _dir = +1; }
            else _cursor += _dir;

            // 窓端で折り返し（パリンドローム）
            if (_cursor > hi) { _cursor = Math.Max(lo, hi - 1); _dir = -1; }
            else if (_cursor < lo) { _cursor = Math.Min(hi, lo + 1); _dir = +1; }

            if (_srcVoiced[_cursor] == outVoiced) return _cursor;

            // 有声性不一致: 進行方向 → 逆方向 の順に窓内で一致フレームを探す
            for (int step = 1; step <= hi - lo; step++)
            {
                int a = _cursor + _dir * step;
                if (a >= lo && a <= hi && _srcVoiced[a] == outVoiced) { _cursor = a; return a; }
                int b = _cursor - _dir * step;
                if (b >= lo && b <= hi && _srcVoiced[b] == outVoiced) { _cursor = b; _dir = -_dir; return b; }
            }

            // 窓内に一致なし: 恒等にフォールバック（補間器の基準フレームに揃える）
            _voicingFallback++;
            _cursor = nearest;
            return nearest;
        }

        public void LogSummary()
        {
            if (!IsActive) return;
            int Count(float[]?[] a) { int n = 0; foreach (var x in a) if (x != null) n++; return n; }
            _log.Info(Stage, $"NM texture transfer: {_applied} frames, PSDRES {_psdresTransferred}, PSD {_psdTransferred}, edc {_edcTransferred}, eenv {_eenvTransferred}, " +
                             $"max drift {_maxDrift}f, voicing fallback {_voicingFallback}");
            _log.Debug(Stage, $"source residuals: edc {Count(_edcResidual)}, psd {Count(_psdResidual)}, eenv {Count(_eenvResidual)} / {_srcNfrm} frames");
        }

        private static float ReadF0(IntPtr framePtr)
        {
            var p = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_F0);
            return p != IntPtr.Zero ? Marshal.PtrToStructure<float>(p) : 0f;
        }
    }
}
