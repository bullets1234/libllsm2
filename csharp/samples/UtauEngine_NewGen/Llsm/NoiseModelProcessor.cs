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

        /// <summary>
        /// 診断用: L2R_EENV_LIMIT=&lt;倍率&gt; で変調深度上限を上書きする（0 で eenv 変調を無効化、
        /// 未指定なら <see cref="EenvModulationLimit"/>）。パルス同期ノイズのザラつき切り分け用。
        /// </summary>
        private static readonly float EffectiveEenvLimit = ResolveEenvLimit();

        private static float ResolveEenvLimit()
        {
            var s = Environment.GetEnvironmentVariable("L2R_EENV_LIMIT");
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0f && v <= 100f
                ? v : EenvModulationLimit;
        }

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

                    float limit = Math.Max(edc[ch], 0f) * EffectiveEenvLimit; // 既定: 変調深度250%まで許容
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
                _log.Info(Stage, $"Clamped eenv modulation depth on {clamped}/{nfrm} frames (limit x{EffectiveEenvLimit:F2})");
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
        /// 有声フレームの NM PSD 低域を、チャンク内有声フレームの定常レベル（ビン毎の中央値）
        /// + <see cref="LowFreqCapMarginDb"/> で頭打ちにする。弾き音・渡りなど倍音振幅が急変する
        /// 箇所では調波モデルが追従できず、倍音の引き残しが残差へ漏れて低域 PSD が定常部より
        /// 13〜20 dB 盛り上がる（実測: 「ら」の弾き周辺 60 ms）。これを雑音として再合成すると
        /// 25 ms 程度の低域ノイズ塊になり、乱数の実現値次第で「ブッ」と聞こえる。有声部の 1 kHz
        /// 以下の息成分は倍音にマスクされるため、定常レベル基準の上限は聴感上安全。
        /// 1〜2 kHz は上限を 6→18 dB へ緩めてスペクトルの段差を避ける。頭打ちしたビンでは
        /// PSDRES の正の偏差も落とす（合成時に dB 加算で戻ってしまうため）。
        /// </summary>
        public void CapVoicedLowFreqPsd(ChunkHandle chunk, int nfrm)
        {
            float fnyq = LlsmBindings.Llsm.GetConfFloat(LlsmBindings.Llsm.GetConf(chunk), NativeLLSM.LLSM_CONF_FNYQ);
            var psds = new float[nfrm][];
            int npsd = -1, nVoiced = 0;
            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                if (FrameAccess.GetF0(frame) <= 0) continue;
                if (FrameAccess.TryGetNm(frame) is not { HasPsd: true } nm) continue;
                if (npsd == -1) npsd = nm.NPsd;
                if (nm.NPsd != npsd) continue;
                psds[i] = nm.ReadPsd();
                nVoiced++;
            }
            if (nVoiced < LowFreqCapMinFrames || npsd <= 1) return;

            float binHz = fnyq / (npsd - 1);
            int nbins = Math.Min(npsd, (int)(LowFreqCapEndHz / binHz) + 1);
            var column = new float[nVoiced];
            var cap = new float[nbins];
            for (int j = 0; j < nbins; j++)
            {
                int c = 0;
                for (int i = 0; i < nfrm; i++) if (psds[i] != null) column[c++] = psds[i][j];
                Array.Sort(column, 0, c);
                float f = j * binHz;
                float margin = f <= LowFreqCapFullHz ? LowFreqCapMarginDb
                    : LowFreqCapMarginDb + (f - LowFreqCapFullHz) / (LowFreqCapEndHz - LowFreqCapFullHz) * 12f;
                cap[j] = column[c / 2] + margin;
            }

            int capped = 0;
            for (int i = 0; i < nfrm; i++)
            {
                if (psds[i] == null) continue;
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                var resPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES);
                int resLen = resPtr != IntPtr.Zero ? NativeLLSM.llsm_fparray_length(resPtr) : 0;
                float[]? res = null;
                bool modified = false;
                for (int j = 0; j < nbins; j++)
                {
                    if (psds[i][j] <= cap[j]) continue;
                    psds[i][j] = cap[j];
                    modified = true;
                    if (j < resLen)
                    {
                        if (res == null) { res = new float[resLen]; System.Runtime.InteropServices.Marshal.Copy(resPtr, res, 0, resLen); }
                        if (res[j] > 0) res[j] = 0;
                    }
                }
                if (!modified) continue;
                FrameAccess.TryGetNm(frame)?.WritePsd(psds[i]);
                if (res != null) System.Runtime.InteropServices.Marshal.Copy(res, 0, resPtr, resLen);
                capped++;
            }
            if (capped > 0) _log.Info(Stage, $"Capped low-frequency NM PSD on {capped}/{nVoiced} voiced frames");
        }

        private const float LowFreqCapMarginDb = 6f;
        private const float LowFreqCapFullHz = 1000f;
        private const float LowFreqCapEndHz = 2000f;
        private const int LowFreqCapMinFrames = 10;

        /// <summary>
        /// 有声フレームの NM PSD から原音倍音位置の櫛形の谷を均す。解析残差（原音 − 調波再合成）は
        /// 各倍音 k·f0 の周りが引き抜かれているため、PSD に f0 周期の谷（実測 3〜6 dB）が残る。
        /// 等倍では HM 側の狭帯域成分がその谷を埋めるが、ピッチを動かすと谷は原音ピッチの位置に
        /// 取り残され、雑音床に原音ピッチの櫛、倍音側は谷を埋めない純トーンになる（1.2% のシフトで
        /// 8-16 kHz の山/谷比 +3 dB を実測）。パワー領域で幅 f0 の移動平均を取ると f0 周期の
        /// 櫛は消え、帯域エネルギーは保存される。
        /// </summary>
        public void DecombVoicedPsd(ChunkHandle chunk, int nfrm)
        {
            float fnyq = LlsmBindings.Llsm.GetConfFloat(LlsmBindings.Llsm.GetConf(chunk), NativeLLSM.LLSM_CONF_FNYQ);
            int done = 0;
            for (int i = 0; i < nfrm; i++)
            {
                var frame = LlsmBindings.Llsm.GetFrame(chunk, i);
                float f0 = FrameAccess.GetF0(frame);
                if (f0 <= 0) continue;
                if (FrameAccess.TryGetNm(frame) is not { HasPsd: true } nm) continue;

                float[] psd = nm.ReadPsd();
                int n = psd.Length;
                float binHz = fnyq / (n - 1);
                float halfBins = 0.5f * f0 / binHz;   // 窓の半幅（ビン、実数）
                if (halfBins < 1f) continue;

                // 累積和で実数幅の箱型平均（端は折り返さず有効範囲で正規化）
                var cum = new double[n + 1];
                for (int j = 0; j < n; j++) cum[j + 1] = cum[j] + Math.Pow(10.0, psd[j] / 10.0);
                var outPsd = new float[n];
                for (int j = 0; j < n; j++)
                {
                    double lo = Math.Max(0.0, j + 0.5 - halfBins), hi = Math.Min(n, j + 0.5 + halfBins);
                    double sum = CumAt(cum, hi) - CumAt(cum, lo);
                    outPsd[j] = (float)(10.0 * Math.Log10(Math.Max(sum / (hi - lo), 1e-12)));
                }
                nm.WritePsd(outPsd);
                done++;
            }
            if (done > 0) _log.Info(Stage, $"De-combed NM PSD on {done} voiced frames");
        }

        /// <summary>累積和の実数位置での線形補間。</summary>
        private static double CumAt(double[] cum, double pos)
        {
            int k = (int)pos;
            if (k >= cum.Length - 1) return cum[^1];
            return cum[k] + (cum[k + 1] - cum[k]) * (pos - k);
        }

        /// <summary>
        /// 調波フィット誤差による雑音バーストの抑制。
        /// フォルマントが速く動く区間や声門の再アタックでは、調波フィットが一時的に外れて残差
        /// （雑音モデルの入力）が跳ね、原音には無い雑音バースト（「ブツ」）が合成される
        /// （戯白メリー e+あ: 原音包絡は平坦なまま雑音 PSD が +14dB, 2026-09-24）。
        /// 有声フレームについて、雑音 PSD の広帯域レベルが近傍（±3〜±8 フレーム）の中央値より
        /// <see cref="BurstThresholdDb"/> 以上高く、かつ原音の包絡が近傍中央値から
        /// <see cref="EnvelopeTolDb"/> 以内（＝実際の過渡ではない）のフレームを対象にする。
        /// バーストは Kalman 平滑後の PSD ではなくフレーム毎の残差 PSDRES（低域の倍音位置）と
        /// 包絡 edc / eenv（低域チャンネル）に入るので、PSDRES の広帯域レベルで検出し、PSDRES を
        /// ビン毎、包絡をチャンネル毎に「近傍中央値 + 1dB」へクランプする。
        /// 子音の破裂や息継ぎは包絡が変化するので対象にならない。N256 で無効化。
        /// </summary>
        private const float BurstThresholdDb = 3f;
        private const float EnvelopeTolDb = 2f;
        private const float BurstKeepAboveMedianDb = 1f;

        public void SuppressFitErrorBursts(ChunkHandle chunk, int nfrm, float[] segment, int nhop)
        {
            // バーストは Kalman 平滑後の PSD ではなく各フレームの残差 PSDRES と包絡 edc/eenv に入る
            var resLevel = new float[nfrm]; var envDb = new float[nfrm]; var voiced = new bool[nfrm];
            var resArr = new float[]?[nfrm]; var resPtrs = new IntPtr[nfrm];
            var edcArr = new float[]?[nfrm]; var eenv0 = new float[]?[nfrm];
            for (int i = 0; i < nfrm; i++)
            {
                var fr = LlsmBindings.Llsm.GetFrame(chunk, i);
                voiced[i] = LlsmBindings.Llsm.GetFrameF0(fr) > 0;
                resLevel[i] = float.NaN;
                var rp = NativeLLSM.llsm_container_get(fr.Ptr, NativeLLSM.LLSM_FRAME_PSDRES);
                if (rp == IntPtr.Zero) continue;
                int n = NativeLLSM.llsm_fparray_length(rp);
                if (n <= 0) continue;
                var res = new float[n]; System.Runtime.InteropServices.Marshal.Copy(rp, res, 0, n);
                double lin = 0; for (int j = 0; j < n; j++) lin += Math.Pow(10.0, res[j] / 10.0);
                resLevel[i] = (float)(10.0 * Math.Log10(lin / n + 1e-30));
                resArr[i] = res; resPtrs[i] = rp;
                var nm = FrameAccess.TryGetNm(fr);
                if (nm is { } nv)
                {
                    if (nv.HasEdc) edcArr[i] = nv.ReadEdc();
                    if (nv.HasEenv)
                    {
                        var e0 = new float[nv.NChannel];
                        for (int c = 0; c < nv.NChannel; c++)
                        {
                            var ch = nv.GetEenvChannel(c);
                            e0[c] = ch is { HasAmplitudes: true } cv ? cv.ReadAmplitudes()[0] : 0f;
                        }
                        eenv0[i] = e0;
                    }
                }
                int lo = Math.Max(0, i * nhop - nhop), hi = Math.Min(segment.Length, i * nhop + nhop);
                double e = 0; for (int k = lo; k < hi; k++) e += (double)segment[k] * segment[k];
                envDb[i] = hi > lo ? (float)(10.0 * Math.Log10(e / (hi - lo) + 1e-12)) : -120f;
            }

            int suppressed = 0; float maxCut = 0;
            var med = new System.Collections.Generic.List<float>(); var envMed = new System.Collections.Generic.List<float>();
            var nbr = new System.Collections.Generic.List<int>(); var tmp = new System.Collections.Generic.List<float>();
            bool dbg = Environment.GetEnvironmentVariable("L2R_BURSTDBG") == "1";
            float keepLin = MathF.Pow(10f, BurstKeepAboveMedianDb / 10f);
            for (int i = 0; i < nfrm; i++)
            {
                if (!voiced[i] || resArr[i] == null) continue;
                med.Clear(); envMed.Clear(); nbr.Clear();
                for (int d = 3; d <= 8; d++)
                {
                    foreach (int k in new[] { i - d, i + d })
                    {
                        if (k < 0 || k >= nfrm || !voiced[k] || resArr[k] == null) continue;
                        med.Add(resLevel[k]); envMed.Add(envDb[k]); nbr.Add(k);
                    }
                }
                if (med.Count < 4) continue;
                med.Sort(); envMed.Sort();
                float mRes = med[med.Count / 2], mEnv = envMed[envMed.Count / 2];
                float excess = resLevel[i] - mRes;
                if (dbg) _log.Debug(Stage, $"burst? f{i}: psdres {resLevel[i]:F1} med {mRes:F1} excess {excess:+0.0;-0.0} | env {envDb[i]:F1} med {mEnv:F1}");
                if (excess < BurstThresholdDb) continue;
                if (MathF.Abs(envDb[i] - mEnv) > EnvelopeTolDb) continue; // 実際の過渡（子音・息継ぎ）は残す

                // PSDRES: ビン毎に近傍中央値 + keep へクランプ（バーストは低域の倍音位置に集中する）
                var cur = resArr[i]!; float cutMax = 0;
                for (int j = 0; j < cur.Length; j++)
                {
                    tmp.Clear();
                    foreach (int k in nbr) { var r = resArr[k]!; if (j < r.Length) tmp.Add(r[j]); }
                    tmp.Sort();
                    float lim = tmp[tmp.Count / 2] + BurstKeepAboveMedianDb;
                    if (cur[j] > lim) { cutMax = MathF.Max(cutMax, cur[j] - lim); cur[j] = lim; }
                }
                System.Runtime.InteropServices.Marshal.Copy(cur, 0, resPtrs[i], cur.Length);

                // 包絡（edc / eenv, パワー）: チャンネル毎に近傍中央値 × keep へクランプ
                var nm = FrameAccess.TryGetNm(LlsmBindings.Llsm.GetFrame(chunk, i));
                if (nm is { } nmv)
                {
                    if (nmv.HasEdc && edcArr[i] != null)
                    {
                        var edc = edcArr[i]!;
                        for (int c = 0; c < edc.Length; c++)
                        {
                            tmp.Clear(); foreach (int k in nbr) if (edcArr[k] is { } ek && c < ek.Length) tmp.Add(ek[c]);
                            if (tmp.Count < 4) continue; tmp.Sort();
                            float lim = tmp[tmp.Count / 2] * keepLin;
                            if (edc[c] > lim) edc[c] = lim;
                        }
                        nmv.WriteEdc(edc);
                    }
                    if (nmv.HasEenv && eenv0[i] != null)
                    {
                        var e0 = eenv0[i]!;
                        for (int c = 0; c < nmv.NChannel; c++)
                        {
                            tmp.Clear(); foreach (int k in nbr) if (eenv0[k] is { } ek && c < ek.Length) tmp.Add(ek[c]);
                            if (tmp.Count < 4) continue; tmp.Sort();
                            float lim = tmp[tmp.Count / 2] * keepLin;
                            if (e0[c] <= lim || e0[c] <= 0) continue;
                            float g = lim / e0[c];
                            var ch = nmv.GetEenvChannel(c);
                            if (ch is not { HasAmplitudes: true } chv) continue;
                            float[] amp = chv.ReadAmplitudes();
                            for (int k = 0; k < amp.Length; k++) amp[k] *= g;
                            chv.WriteAmplitudes(amp);
                        }
                    }
                }
                suppressed++; maxCut = MathF.Max(maxCut, cutMax);
            }
            if (suppressed > 0)
                _log.Info(Stage, $"Suppressed fit-error noise bursts on {suppressed} voiced frames (max -{maxCut:F1}dB)");
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
