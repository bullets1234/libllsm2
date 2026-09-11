using System;
using System.Collections.Generic;
using System.Linq;
using LlsmBindings;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Pitch
{
    /// <summary>
    /// F0 系列の安定化処理。
    /// FRQ 由来 F0 への PYIN V/UV マスク、FRQ 外れ値の平滑化、
    /// PYIN 由来 F0 の局所オクターブ補正を提供する。
    /// （UtauEngine の F0 後処理ブロックを移植）
    /// </summary>
    public static class F0Stabilizer
    {
        private const string Stage = "Pitch";

        /// <summary>
        /// FRQ ピッチ + PYIN 有声/無声判定のハイブリッドマスク。
        /// ピッチ値は FRQ を維持しつつ、PYIN が無声と判定した区間を無声化し、
        /// 子音バーストのクリック化を防ぐ。Whisper 音源等で誤マスクが多い場合は自動スキップ。
        /// <paramref name="f0Vuv"/> は事前計算した PYIN トラック
        /// （マイクロプロソディ抽出と共用するため呼び出し側で解析する）。
        /// </summary>
        /// <returns>マスク・安定化を適用したか（false=自動フォールバックでスキップ）。</returns>
        public static bool ApplyPyinVuvMask(
            float[] f0, float[] f0Vuv, float thopSec, ILogger log)
        {
            int nMask = Math.Min(f0.Length, f0Vuv.Length);

            // 無声判定に ±10ms 相当の膨張をかける
            int dilate = Math.Max(1, (int)MathF.Round(0.010f / thopSec));
            var unvoicedMask = new bool[nMask];
            for (int i = 0; i < nMask; i++)
            {
                if (f0Vuv[i] > 0) continue;
                for (int k = Math.Max(0, i - dilate); k <= Math.Min(nMask - 1, i + dilate); k++)
                    unvoicedMask[k] = true;
            }

            // 自動フォールバック: FRQ 有声の >70% を無声化しそうなら中止
            int frqVoiced = 0, wouldMask = 0;
            for (int i = 0; i < nMask; i++)
            {
                if (f0[i] > 0)
                {
                    frqVoiced++;
                    if (unvoicedMask[i]) wouldMask++;
                }
            }
            if (frqVoiced > 0 && (float)wouldMask / frqVoiced > 0.70f)
            {
                log.Warn(Stage, $"PYIN V/UV mask skipped: would unvoice {wouldMask}/{frqVoiced} frames " +
                                "(likely PYIN misdetection; use Z flag to force pure FRQ)");
                return false;
            }

            int maskedCount = 0;
            for (int i = 0; i < nMask; i++)
            {
                if (f0[i] > 0 && unvoicedMask[i]) { f0[i] = 0; maskedCount++; }
            }
            for (int i = nMask; i < f0.Length; i++)
            {
                if (f0[i] > 0) { f0[i] = 0; maskedCount++; }
            }

            // 孤立した極短有声区間（≤20ms 相当）を除去
            int minRunFrames = Math.Max(2, (int)MathF.Round(0.020f / thopSec));
            maskedCount += RemoveShortVoicedRuns(f0, minRunFrames);

            // FRQ ピッチ値の安定化
            int stabilized = StabilizeVoicedRuns(f0);

            if (maskedCount > 0 || stabilized > 0)
                log.Info(Stage, $"V/UV mask from PYIN: unvoiced {maskedCount} frames, stabilized {stabilized} FRQ outliers");

            return true;
        }

        private static int RemoveShortVoicedRuns(float[] f0, int minRunFrames)
        {
            int removed = 0;
            int runStart = -1;
            for (int i = 0; i <= f0.Length; i++)
            {
                bool voiced = i < f0.Length && f0[i] > 0;
                if (voiced && runStart < 0) runStart = i;
                else if (!voiced && runStart >= 0)
                {
                    if (i - runStart <= minRunFrames)
                    {
                        for (int j = runStart; j < i; j++) f0[j] = 0;
                        removed += i - runStart;
                    }
                    runStart = -1;
                }
            }
            return removed;
        }

        /// <summary>
        /// 有声区間ごとに 5タップメディアンで平滑化し、区間中央値から 20% 超外れる
        /// フレームは無声化、3% 超ずれるフレームはメディアン値で置換する。
        /// </summary>
        private static int StabilizeVoicedRuns(float[] f0)
        {
            int stabilized = 0;
            int runStart = -1;
            for (int i = 0; i <= f0.Length; i++)
            {
                bool voiced = i < f0.Length && f0[i] > 0;
                if (voiced && runStart < 0) runStart = i;
                else if (!voiced && runStart >= 0)
                {
                    int runLen = i - runStart;

                    var med = new float[runLen];
                    var medBuf = new List<float>(5);
                    for (int j = 0; j < runLen; j++)
                    {
                        medBuf.Clear();
                        for (int k = Math.Max(0, j - 2); k <= Math.Min(runLen - 1, j + 2); k++)
                            medBuf.Add(f0[runStart + k]);
                        medBuf.Sort();
                        med[j] = medBuf[medBuf.Count / 2];
                    }

                    var sorted = new float[runLen];
                    for (int j = 0; j < runLen; j++) sorted[j] = f0[runStart + j];
                    Array.Sort(sorted);
                    float runMedian = sorted[runLen / 2];

                    for (int j = 0; j < runLen; j++)
                    {
                        float orig = f0[runStart + j];
                        if (MathF.Abs(orig - runMedian) / runMedian > 0.20f)
                        {
                            // 無声化すると母音中に 1 フレームのノイズ穴（V/UV フリッカー→
                            // クリック）が生じるため、局所メディアンで補間する。
                            // 局所メディアン自体も外れている場合は区間中央値へフォールバック。
                            f0[runStart + j] = MathF.Abs(med[j] - runMedian) / runMedian > 0.20f
                                ? runMedian : med[j];
                            stabilized++;
                        }
                        else if (MathF.Abs(orig - med[j]) / med[j] > 0.03f)
                        {
                            f0[runStart + j] = med[j];
                            stabilized++;
                        }
                    }
                    runStart = -1;
                }
            }
            return stabilized;
        }

        /// <summary>
        /// PYIN 由来 F0 の局所オクターブジャンプを補正する（前後±15フレームの局所中央値基準、
        /// V/UV 境界でウィンドウ打ち切り）。補正があれば true。
        /// </summary>
        public static bool CorrectOctaveJumps(float[] f0, ILogger log)
        {
            var voiced = f0.Where(x => x > 0).ToArray();
            if (voiced.Length <= 2) return false;

            const int half = 15;
            int corrected = 0;
            for (int i = 0; i < f0.Length; i++)
            {
                if (f0[i] <= 0) continue;

                var local = new List<float>();
                for (int j = i - 1; j >= Math.Max(0, i - half); j--)
                {
                    if (f0[j] <= 0) break;
                    local.Add(f0[j]);
                }
                for (int j = i + 1; j <= Math.Min(f0.Length - 1, i + half); j++)
                {
                    if (f0[j] <= 0) break;
                    local.Add(f0[j]);
                }

                float refF0;
                if (local.Count >= 3)
                {
                    local.Sort();
                    refF0 = local[local.Count / 2];
                }
                else
                {
                    Array.Sort(voiced);
                    refF0 = voiced[voiced.Length / 2];
                }

                float ratio = f0[i] / refF0;
                if (ratio > 1.6f) { f0[i] /= MathF.Round(ratio); corrected++; }
                else if (ratio < 0.625f && ratio > 0.01f) { f0[i] *= MathF.Round(1.0f / ratio); corrected++; }
            }

            if (corrected > 0)
            {
                log.Info(Stage, $"Corrected {corrected} octave jumps (local window ±{half})");
                return true;
            }
            return false;
        }
    }
}
