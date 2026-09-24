using System;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg.Synthesis
{
    /// <summary>
    /// 出力後処理：基準レベル正規化 → ボリューム適用 → ピークリミット。
    ///
    /// 合成チェーン（ピッチ比エネルギー補償等）のゲインはノート毎に異なるため、出力の
    /// 実効音量（無音除外 RMS）を基準レベルへ正規化してノート間のバランスを揃える。
    /// 基準レベルは既定で「原音区間の実効音量」（＝音源の録音レベルをそのまま保つ。
    /// 旧版のピーク処理のみの挙動と同じ音量感）。環境変数 L2R_TARGET_DB=-16 等を指定した
    /// 場合のみ絶対値（dBFS）を基準にし、音源間のレベルも揃える。
    /// ゲインは ±12dB でクランプするため、囁き・息・語尾などの極端に静かな素材が
    /// 通常ノートと同じ音量まで爆音化することはない（+12dB 止まり）。
    /// 最後に UTAU ミキサーのオーバーラップ加算でクリップしないよう
    /// -3dB を超えるピークのみ抑える。
    /// </summary>
    public static class PostProcessor
    {
        private const string Stage = "Volume";
        private const float MaxPeak = 0.70f;        // -3dB
        /// <summary>
        /// ピーク下限（-12dBFS）。作業前の版が持っていた「小さいノートはここまで持ち上げる」規則。
        /// 録音レベルの小さい音源はサンプル間のレベルが揃っていないことが多く、原音レベル追従だけ
        /// だとその不揃いがそのまま出る（2026-09-16 報告）。原音追従の後にこの下限を掛けることで、
        /// 通常レベルの音源は原音の音量感を保ちつつ、小さい音源は作業前と同じ扱いになる。
        /// </summary>
        private const float MinPeak = 0.25f;        // -12dB（旧規則。現在は実効レベル下限に置き換え、参考値として保持）
        /// <summary>
        /// 実効レベル下限（有声部 RMS, dBFS）。小さい音源のノートはここまで持ち上げる。
        /// 旧規則のピーク下限（0.25）はノートのピーク値で持ち上げ量が決まるため、子音のピークが立つ
        /// ノートほど持ち上がらず、隣接ノート間で最大 5dB の段差になった（2026-09-24 実測）。
        /// 実効レベルで揃えれば同じ音源のノートは同じ音量感に収束する。-22dBFS は旧規則が母音で
        /// 実現していた水準に相当。
        /// </summary>
        private const float MinLevelDb = -22f;
        private const float MaxFloorGainDb = 20f;
        /// <summary>L2R_TARGET_DB 未指定時のフォールバック（原音レベルが取れない場合のみ使用）。</summary>
        private const float DefaultTargetRmsDb = -16f;
        private const float MaxNormGainDb = 12f;    // 正規化ゲインの安全クランプ ±12dB
        private const int RmsFrameLen = 441;        // 10ms @44.1k 相当（絶対時間でなくてよい）

        /// <summary>
        /// 絶対基準レベル（実効 RMS, dBFS）。環境変数 L2R_TARGET_DB=-16 等で指定した場合のみ有効。
        /// 未指定（NaN）なら原音区間のレベルに合わせる。
        /// </summary>
        private static readonly float AbsoluteTargetDb = ResolveTargetDb();

        private static float ResolveTargetDb()
        {
            var s = Environment.GetEnvironmentVariable("L2R_TARGET_DB");
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var v)
                   && v > -40f && v < 0f
                ? v : float.NaN;
        }

        /// <summary>原音区間の実効レベル（dBFS）。<see cref="Apply"/> の基準に使う。</summary>
        public static float SourceLevelDb(float[] segment)
        {
            float rms = ActiveRms(segment);
            return rms > 1e-6f ? 20f * MathF.Log10(rms) : float.NaN;
        }

        /// <summary>
        /// 有声フレーム（<paramref name="frameF0"/>[i] &gt; 0、フレーム i の中心 = i·nhop）だけで
        /// 測った実効レベル（dBFS）。無音・子音・録音ノイズ床の割合に左右されないので、
        /// 伸長やゲインの小さい音源でもノート間で基準が揃う。有声フレームが無ければ
        /// <see cref="SourceLevelDb"/> と同じ全体の実効レベル。
        /// </summary>
        public static float VoicedLevelDb(float[] x, float[]? frameF0, int nhop)
        {
            float rms = frameF0 != null ? VoicedRms(x, frameF0, nhop) : 0f;
            if (rms <= 1e-6f) rms = ActiveRms(x);
            return rms > 1e-6f ? 20f * MathF.Log10(rms) : float.NaN;
        }

        private static float VoicedRms(float[] x, float[] frameF0, int nhop)
        {
            if (x.Length == 0 || nhop <= 0) return 0f;
            var ms = new System.Collections.Generic.List<double>();
            double maxMs = 0;
            for (int i = 0; i < frameF0.Length; i++)
            {
                if (frameF0[i] <= 0) continue;
                int lo = Math.Max(0, i * nhop - nhop / 2), hi = Math.Min(x.Length, i * nhop + nhop / 2);
                if (hi <= lo) continue;
                double e = 0;
                for (int k = lo; k < hi; k++) e += (double)x[k] * x[k];
                e /= hi - lo;
                ms.Add(e);
                if (e > maxMs) maxMs = e;
            }
            if (ms.Count == 0 || maxMs <= 0) return 0f;
            double threshold = maxMs * 0.01; // 有声中でも -20dB 未満（無音混入）は除外
            double sum = 0; int cnt = 0;
            foreach (var e in ms) { if (e >= threshold) { sum += e; cnt++; } }
            return cnt > 0 ? (float)Math.Sqrt(sum / cnt) : 0f;
        }

        /// <summary>
        /// 基準レベル正規化＋ボリューム適用＋ピークリミット（in-place）。
        /// <paramref name="sourceLevelDb"/> は原音区間の実効レベル（NaN なら -16dBFS フォールバック）。
        /// </summary>
        /// <param name="outputFrameF0">出力フレーム毎の F0（有声判定用、null なら全体の実効値）。</param>
        /// <summary>直前の <see cref="Apply"/> の音量処理の内訳（呼び出し記録用）。</summary>
        public static string LastSummary { get; private set; } = "";

        public static void Apply(float[] output, int volume, ILogger log, float sourceLevelDb = float.NaN,
            float[]? outputFrameF0 = null, int nhop = 0)
        {
            if (output.Length == 0) return;
            float normGainDb = 0f, floorGainDb = 0f, limiterGainDb = 0f, outLevelDb = float.NaN;

            // 1. 基準レベルへの正規化（±12dB クランプ）
            bool absolute = !float.IsNaN(AbsoluteTargetDb);
            float targetDb = absolute ? AbsoluteTargetDb
                           : !float.IsNaN(sourceLevelDb) ? sourceLevelDb
                           : DefaultTargetRmsDb;
            float outRms = outputFrameF0 != null && nhop > 0 ? VoicedRms(output, outputFrameF0, nhop) : 0f;
            if (outRms <= 1e-6f) outRms = ActiveRms(output);
            if (outRms > 1e-6f)
            {
                float gainDb = Math.Clamp(
                    targetDb - 20f * MathF.Log10(outRms), -MaxNormGainDb, MaxNormGainDb);
                outLevelDb = 20f * MathF.Log10(outRms);
                if (MathF.Abs(gainDb) > 0.1f)
                {
                    normGainDb = gainDb;
                    float gain = MathF.Pow(10f, gainDb / 20f);
                    for (int i = 0; i < output.Length; i++) output[i] *= gain;
                    log.Debug(Stage, $"Normalize: {20f * MathF.Log10(outRms):F1}dBFS -> " +
                                     $"{(absolute ? "target" : "source")} {targetDb:F1}dBFS (gain {gainDb:+0.0;-0.0}dB)");
                }
            }

            // 2. volume 引数
            if (volume != 100)
            {
                float volScale = volume / 100.0f;
                for (int i = 0; i < output.Length; i++) output[i] *= volScale;
            }

            // 3. 実効レベル下限（小さい音源のノートを同じ音量感に揃える）
            float levelNow = outputFrameF0 != null && nhop > 0 ? VoicedRms(output, outputFrameF0, nhop) : 0f;
            if (levelNow <= 1e-6f) levelNow = ActiveRms(output);
            if (levelNow > 1e-6f)
            {
                float levelDb = 20f * MathF.Log10(levelNow);
                if (levelDb < MinLevelDb)
                {
                    floorGainDb = Math.Min(MinLevelDb - levelDb, MaxFloorGainDb);
                    float floorGain = MathF.Pow(10f, floorGainDb / 20f);
                    for (int i = 0; i < output.Length; i++) output[i] *= floorGain;
                    log.Debug(Stage, $"Level {levelDb:F1}dBFS below floor -> {MinLevelDb:F0}dBFS (gain {floorGainDb:+0.0}dB)");
                }
            }

            // 4. ピークリミッタ
            float peak = 0f;
            for (int i = 0; i < output.Length; i++)
            {
                float a = Math.Abs(output[i]);
                if (a > peak) peak = a;
            }

            float peakBefore = peak;
            if (peak > MaxPeak)
            {
                float limiterGain = MaxPeak / peak;
                limiterGainDb = 20f * MathF.Log10(limiterGain);
                for (int i = 0; i < output.Length; i++) output[i] *= limiterGain;
                log.Debug(Stage, $"Peak {peak:F3} too loud -> {MaxPeak:F3} (gain {limiterGain:F3})");
            }
            else
            {
                log.Debug(Stage, $"Peak {peak:F3} within range, no adjustment");
            }

            LastSummary = $"src {(float.IsNaN(sourceLevelDb) ? float.NaN : sourceLevelDb):F1}dB out {outLevelDb:F1}dB norm {normGainDb:+0.0;-0.0}dB vol {volume} peak {peakBefore:F2} floor {floorGainDb:+0.0;-0.0}dB lim {limiterGainDb:+0.0;-0.0}dB";
        }

        /// <summary>
        /// 無音を除外した実効 RMS。10ms フレームの RMS を取り、最大フレームの
        /// -20dB 以上のフレームのみで平均する（囁き素材でも相対閾値なので有効音が残る）。
        /// タイムストレッチで無音・子音比率が変わっても「鳴っている部分」同士で
        /// 比較できるようにするための処理。
        /// </summary>
        private static float ActiveRms(float[] x)
        {
            if (x.Length == 0) return 0f;
            int nFrames = (x.Length + RmsFrameLen - 1) / RmsFrameLen;
            var frameMs = new double[nFrames];
            double maxMs = 0;
            for (int f = 0; f < nFrames; f++)
            {
                int lo = f * RmsFrameLen;
                int hi = Math.Min(x.Length, lo + RmsFrameLen);
                double e = 0;
                for (int i = lo; i < hi; i++) e += (double)x[i] * x[i];
                frameMs[f] = e / Math.Max(1, hi - lo);
                if (frameMs[f] > maxMs) maxMs = frameMs[f];
            }
            if (maxMs <= 0) return 0f;

            double threshold = maxMs * 0.01; // パワー比 -20dB
            double sum = 0; int cnt = 0;
            for (int f = 0; f < nFrames; f++)
            {
                if (frameMs[f] < threshold) continue;
                sum += frameMs[f]; cnt++;
            }
            return cnt > 0 ? (float)Math.Sqrt(sum / cnt) : 0f;
        }
    }
}
