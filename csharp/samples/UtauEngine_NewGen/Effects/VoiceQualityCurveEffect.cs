using System;
using System.Runtime.InteropServices;
using LlsmBindings;
using UtauEngineNg.Diagnostics;
using UtauEngineNg.Llsm;

namespace UtauEngineNg.Effects
{
    /// <summary>
    /// 動的声質カーブ（Q フラグ、試験実装）。
    ///
    /// 実歌唱では声質は一定でなく、状況に生理的に連動する:
    ///   - 音高連動: 高音ほど声帯が張る（Rd 低下・明るく）、低音は緩む（Rd 上昇・息っぽく）
    ///   - ノート内カーブ: 立ち上がりは柔らかく息混じり → 定常部で確立 → リリースで再び緩む
    ///
    /// 本エフェクトはフレーム毎に:
    ///   1. 実効ピッチの srcF0 からの乖離（オクターブ）に応じて Rd をスケール
    ///      （Rd は LF 声門モデルの物理パラメータなので、tolayer0 で倍音構造が自然に変わる）
    ///   2. 張り方向では 2k→8kHz の明るさシェルフを軽く付与（緩み方向は暗く）
    ///   3. 低音側・ノート端では NM PSD を持ち上げ息成分を増やす
    ///
    /// フレーム補間ループ内（Layer0 変換前）で適用すること。
    /// L フラグ（Rd 自動推定）併用時は推定値が基準となり相対変調になる。
    /// </summary>
    public sealed class VoiceQualityCurveEffect
    {
        private const string Stage = "VoiceQuality";

        private const float MinRd = 0.3f;
        private const float MaxRd = 2.7f;

        // 音高連動: 強度100・1オクターブ上で Rd × 2^-0.35 ≈ 0.78（張る）、下で ≈ 1.27（緩む）
        private const float RdPerOctave = 0.35f;
        private const float MaxTensionOct = 1.5f;

        // 明るさシェルフ: 強度100・1オクターブ上で 8kHz +1.5dB（2kHz から対数勾配）
        private const float ShelfDbPerOct = 1.5f;
        private const float ShelfLoHz = 2000f;
        private const float ShelfRefHz = 8000f;
        private const float MaxShelfDb = 3f;

        // ノート内カーブ
        private const float OnsetSec = 0.08f;
        private const float ReleaseSec = 0.10f;
        private const float EdgeRdBoost = 0.35f;   // 端で Rd +35%（柔らかく）@100%
        private const float EdgeBreathDb = 3f;     // 端で息 +3dB @100%
        private const float PitchBreathDb = 2.5f;  // 1オクターブ下で息 +2.5dB @100%

        private readonly float _strength;
        private readonly float _srcF0;
        private readonly int _dstNfrm;
        private readonly int _onsetFrames;
        private readonly int _releaseFrames;
        private readonly float _fnyq;
        private int _applied;

        public VoiceQualityCurveEffect(int strength, float srcF0, float thopSec, int dstNfrm, int fs)
        {
            _strength = strength / 100f;
            _srcF0 = srcF0;
            _dstNfrm = dstNfrm;
            _onsetFrames = Math.Max(1, (int)(OnsetSec / thopSec));
            _releaseFrames = Math.Max(1, (int)(ReleaseSec / thopSec));
            _fnyq = fs / 2f;
        }

        public bool IsActive => _strength > 0;

        /// <summary>有声フレームへ声質カーブを適用する（Layer0 変換前）。</summary>
        public void ApplyFrame(IntPtr framePtr, int outFrameIdx, float newF0)
        {
            if (!IsActive || newF0 <= 0 || _srcF0 <= 0) return;
            float s = _strength;

            // --- 位置ファクタ e ∈ [0,1]（オンセット/リリース端で 1、定常部で 0） ---
            float e = 0f;
            if (outFrameIdx < _onsetFrames)
                e = 0.5f + 0.5f * MathF.Cos(MathF.PI * outFrameIdx / _onsetFrames);
            int fromEnd = _dstNfrm - 1 - outFrameIdx;
            if (fromEnd < _releaseFrames)
                e = MathF.Max(e, 0.5f + 0.5f * MathF.Cos(MathF.PI * fromEnd / _releaseFrames));

            // --- 音高テンション t（オクターブ、上=正） ---
            float t = Math.Clamp(MathF.Log2(newF0 / _srcF0), -MaxTensionOct, MaxTensionOct);

            // 1. Rd スケール（張り + 端の緩み）
            var rdPtr = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_RD);
            if (rdPtr != IntPtr.Zero)
            {
                float rd = Marshal.PtrToStructure<float>(rdPtr);
                if (float.IsFinite(rd) && rd > 0)
                {
                    float rdMul = MathF.Pow(2f, -t * RdPerOctave * s) * (1f + e * EdgeRdBoost * s);
                    float newRd = Math.Clamp(rd * rdMul, MinRd, MaxRd);
                    NativeCallbacks.AttachRd(framePtr, newRd);
                }
            }

            // 2. 明るさシェルフ（張りで明るく・緩みで暗く）
            float shelfScale = t * s;
            if (MathF.Abs(shelfScale) > 0.01f)
            {
                var vtmagnPtr = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr != IntPtr.Zero)
                {
                    int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                    if (nspec > 1)
                    {
                        float[] v = new float[nspec];
                        Marshal.Copy(vtmagnPtr, v, 0, nspec);
                        // shelf(8kHz) = shelfScale × ShelfDbPerOct になる対数勾配
                        float refSlope = MathF.Log2(ShelfRefHz / ShelfLoHz);
                        for (int j = 0; j < nspec; j++)
                        {
                            float f = (float)j / (nspec - 1) * _fnyq;
                            if (f <= ShelfLoHz) continue;
                            float shelf = shelfScale * ShelfDbPerOct * MathF.Log2(f / ShelfLoHz) / refSlope;
                            v[j] = MathF.Max(-80f, v[j] + Math.Clamp(shelf, -MaxShelfDb, MaxShelfDb));
                        }
                        Marshal.Copy(v, 0, vtmagnPtr, nspec);
                    }
                }
            }

            // 3. 息成分（低音側 + ノート端で NM PSD を持ち上げ）
            float breathDb = (MathF.Max(0f, -t) * PitchBreathDb + e * EdgeBreathDb) * s;
            if (breathDb > 0.05f)
            {
                var nm = FrameAccess.TryGetNm(new ContainerRef(framePtr));
                if (nm is { HasPsd: true } nmv)
                {
                    float[] psd = nmv.ReadPsd();
                    for (int j = 0; j < psd.Length; j++)
                    {
                        // 高域ほど息の寄与が大きい軽い重み付け
                        float fr = (float)j / Math.Max(1, psd.Length - 1);
                        psd[j] += breathDb * (0.6f + 0.4f * fr);
                    }
                    nmv.WritePsd(psd);
                }
            }

            _applied++;
        }

        public void LogSummary(ILogger log)
        {
            if (IsActive && _applied > 0)
                log.Info(Stage, $"Q{(int)(_strength * 100)}: dynamic voice quality on {_applied} voiced frames " +
                                $"(onset {_onsetFrames}f, release {_releaseFrames}f)");
        }
    }
}
