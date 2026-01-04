using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using LlsmBindings;

namespace UtauEngine
{

/// <summary>
/// LLSM ベースの UTAU エンジン (resampler 互換)
/// </summary>
class Program
{
    const float Thop = 0.005f;     // 5ms (LLSM デフォルト、安定した設定)
    
    // ネイティブ関数デリゲート
    delegate void DeleteFpDelegate(IntPtr ptr);
    static readonly DeleteFpDelegate _deleteFp = NativeLLSM.llsm_delete_fp;
    static readonly DeleteFpDelegate _deleteFpArray = NativeLLSM.llsm_delete_fparray;
    static readonly DeleteFpDelegate _deleteHm = NativeLLSM.llsm_delete_hmframe;
    static readonly DeleteFpDelegate _deleteNm = NativeLLSM.llsm_delete_nmframe;
    
    delegate IntPtr CopyFpArrayDelegate(IntPtr ptr);
    static readonly CopyFpArrayDelegate _copyFpArrayFunc = NativeLLSM.llsm_copy_fparray;
    static readonly CopyFpArrayDelegate _copyNm = NativeLLSM.llsm_copy_nmframe;
    
    static void Main(string[] args)
    {
        // Shift-JIS エンコーディングを有効化（UTAU は Shift-JIS を使用）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sjis = Encoding.GetEncoding("shift_jis");
        
        // コンソール出力を Shift-JIS に設定（デバッグ用）
        try { Console.OutputEncoding = sjis; } catch { }
        try { Console.InputEncoding = sjis; } catch { }
        
        // resampler 互換モード: 引数があれば resampler として動作
        if (args.Length >= 2)
        {
            RunAsResampler(args);
            return;
        }
        
        // デモモード
        RunDemo();
    }
    
    /// <summary>
    /// UTAU resampler 互換モード
    /// 引数: <入力wav> <出力wav> <ピッチ> <子音速度> <フラグ> 
    ///       <オフセット> <長さ要求> <固定範囲> <末尾ブランク> 
    ///       <ボリューム> <モジュレーション> [ピッチベンド...]
    /// </summary>
    static void RunAsResampler(string[] args)
    {
        // デバッグ: 引数をログファイルに出力（Shift-JIS 問題の診断用）
        string logPath = "";
        try
        {
            logPath = Path.Combine(Path.GetTempPath(), "utauengine_log.txt");
            using var logFile = new StreamWriter(logPath, true, Encoding.GetEncoding("shift_jis"));
            logFile.WriteLine($"[{DateTime.Now}] Args count: {args.Length}");
            for (int i = 0; i < args.Length; i++)
            {
                // ピッチベンドは長いので最初の100文字だけ
                string val = args[i];
                if (val.Length > 100) val = val.Substring(0, 100) + "...";
                logFile.WriteLine($"  [{i}] {val}");
            }
            
            // ピッチベンドをデコードして確認
            if (args.Length > 12)
            {
                var pb = ParsePitchBend(args, 11);
                logFile.WriteLine($"  [PB] Count: {pb.Count}");
                if (pb.Count > 0)
                {
                    logFile.WriteLine($"  [PB] Range: {pb.Min()} to {pb.Max()}");
                    logFile.WriteLine($"  [PB] First 10: [{string.Join(",", pb.Take(10))}]");
                    logFile.WriteLine($"  [PB] Last 10: [{string.Join(",", pb.Skip(Math.Max(0, pb.Count - 10)))}]");
                }
            }
            
            // targetF0 を記録
            if (args.Length > 2)
            {
                float tF0 = NoteNameToHz(args[2]);
                logFile.WriteLine($"  [Target] Note={args[2]}, F0={tF0:F1}Hz");
            }
            logFile.Flush();
        }
        catch { }
        
        // 最低限の引数チェック
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UtauEngine <input.wav> <output.wav> [pitch] [velocity] [flags] [offset] [length] [consonant] [cutoff] [volume] [modulation] [pitchbend...]");
            Environment.Exit(1);
        }
        
        string inputWav = args[0];
        string outputWav = args[1];
        string pitchStr = args.Length > 2 ? args[2] : "C4";
        int velocity = args.Length > 3 ? int.TryParse(args[3], out var v) ? v : 100 : 100;
        string flags = args.Length > 4 ? args[4] : "";
        float offset = args.Length > 5 ? float.TryParse(args[5], out var o) ? o : 0 : 0;
        float lengthReq = args.Length > 6 ? float.TryParse(args[6], out var l) ? l : 0 : 0;
        float consonant = args.Length > 7 ? float.TryParse(args[7], out var c) ? c : 0 : 0;
        float cutoff = args.Length > 8 ? float.TryParse(args[8], out var cut) ? cut : 0 : 0;
        int volume = args.Length > 9 ? int.TryParse(args[9], out var vol) ? vol : 100 : 100;
        int modulation = args.Length > 10 ? int.TryParse(args[10], out var mod) ? mod : 0 : 0;
        
        // ピッチベンド (Base64) - 12番目以降の引数
        List<int> pitchBend = new List<int>();
        int tempo = 120;  // デフォルトテンポ
        if (args.Length > 11)
        {
            (tempo, pitchBend) = ParsePitchBendWithTempo(args, 11);
        }
        
        // ファイル存在チェック
        if (!File.Exists(inputWav))
        {
            Console.Error.WriteLine($"Input file not found: {inputWav}");
            Environment.Exit(1);
        }
        
        // ピッチ文字列をHz に変換 (例: "C4" -> 261.63)
        float targetF0 = NoteNameToHz(pitchStr);
        
        // 処理実行
        try
        {
            Resample(inputWav, outputWav, targetF0, velocity, flags, 
                     offset, lengthReq, consonant, cutoff, volume, modulation, pitchBend, tempo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.Exit(1);
        }
    }
    
    /// <summary>
    /// resampler メイン処理
    /// UTAU の引数:
    ///   offset: 左ブランク（原音の開始位置）
    ///   consonant: 子音部/固定範囲（この部分は伸縮しない）
    ///   cutoff: 負=末尾からのカット、正=開始からの長さ
    ///   lengthReq: 出力の要求長さ（ms）
    ///   tempo: テンポ（BPM）- ピッチベンドのタイミング計算に使用
    /// </summary>
    static void Resample(string inputWav, string outputWav, float targetF0, 
                         int velocity, string flags, float offset, float lengthReq,
                         float consonant, float cutoff, int volume, int modulation,
                         List<int> pitchBend, int tempo = 120)
    {
        // WAV 読み込み
        var (samples, fs) = Wav.ReadMono(inputWav);
        
        // .frq ファイルから F0 情報を取得（あれば）
        FrqData? frqData = null;
        string frqPath = inputWav + ".frq";
        if (!File.Exists(frqPath))
        {
            frqPath = Path.ChangeExtension(inputWav, null) + "_wav.frq";
        }
        if (File.Exists(frqPath))
        {
            try { frqData = ReadFrqFile(frqPath); } catch { }
        }
        
        // オフセットとカットオフを適用して原音の使用範囲を決定
        // UTAU resampler の仕様:
        //   offset: 使用開始位置 (ms)
        //   cutoff: 負の場合 = offset から |cutoff| ms が使用範囲（右ブランクまでの長さ）
        //           正の場合 = ファイル末尾からの余白（ms）
        int offsetSamples = (int)(offset / 1000.0 * fs);
        int endSamples;
        if (cutoff < 0)
        {
            // 負の値: offset から |cutoff| ms が使用範囲の長さ
            endSamples = offsetSamples + (int)(Math.Abs(cutoff) / 1000.0 * fs);
        }
        else if (cutoff > 0)
        {
            // 正の値: ファイル末尾からの余白
            endSamples = samples.Length - (int)(cutoff / 1000.0 * fs);
        }
        else
        {
            endSamples = samples.Length;
        }
        
        // 子音部の終わり位置を計算
        int consonantEndSamples = offsetSamples + (int)(consonant / 1000.0 * fs);
        
        // デバッグ: 切り出し範囲を表示
        float totalDurationMs = samples.Length / (float)fs * 1000;
        Console.WriteLine($"  [Range] Total WAV: {totalDurationMs:F1}ms ({samples.Length} samples)");
        Console.WriteLine($"  [Range] Offset (start): {offset:F1}ms");
        Console.WriteLine($"  [Range] Consonant end: {consonant:F1}ms from offset = {offset + consonant:F1}ms");
        Console.WriteLine($"  [Range] End: {endSamples / (float)fs * 1000:F1}ms (cutoff={cutoff:F1})");
        Console.WriteLine($"  [Range] Usable segment: {(endSamples - offsetSamples) / (float)fs * 1000:F1}ms");
        
        // 範囲を切り出し
        int startSample = Math.Max(0, offsetSamples);
        int totalLength = Math.Max(0, Math.Min(endSamples - startSample, samples.Length - startSample));
        
        if (totalLength <= 0)
        {
            // 無音を出力
            Wav.WriteMono16(outputWav, new float[1], fs);
            return;
        }
        
        // 子音部（固定範囲）と伸縮部分を分ける
        int consonantSamples = (int)(consonant / 1000.0 * fs);
        consonantSamples = Math.Min(consonantSamples, totalLength);
        int stretchableSamples = totalLength - consonantSamples;
        
        // 全体を切り出し
        float[] segment = new float[totalLength];
        Array.Copy(samples, startSample, segment, 0, totalLength);
        
        // Pフラグ: ピッチ検出バイパス（.frqを無視してPYIN/LLSM自動推定を強制）
        bool bypassFrq = flags.Contains("P", StringComparison.OrdinalIgnoreCase);
        
        // F0 を取得（.frq があれば使用、なければ PYIN で初期推定）
        // Pフラグが有効な場合は.frqを無視してPYIN/LLSM自動推定を使用
        int nhop = (int)(Thop * fs);
        float[] f0;
        float srcF0;
        
        if (frqData != null && frqData.F0Values.Length > 0 && !bypassFrq)
        {
            // .frq から F0 を取得してリサンプル
            int frqStartFrame = offsetSamples / frqData.SamplesPerFrame;
            int frqFrameCount = totalLength / frqData.SamplesPerFrame + 1;
            int llsmFrameCount = totalLength / nhop + 1;
            
            f0 = new float[llsmFrameCount];
            for (int i = 0; i < llsmFrameCount; i++)
            {
                // frq のフレームインデックスを計算
                float frqIdx = frqStartFrame + (float)i * nhop / frqData.SamplesPerFrame;
                int idx = (int)frqIdx;
                if (idx >= 0 && idx < frqData.F0Values.Length)
                {
                    f0[i] = (float)frqData.F0Values[idx];
                }
            }
            srcF0 = (float)frqData.AverageF0;  // 仮の値、後で LLSM から取得（Eフラグがなければ）
            Console.WriteLine($"  [Pitch] Initial F0 from FRQ: {srcF0:F1}Hz");
        }
        else
        {
            // PYIN で F0 初期推定
            f0 = Pyin.Analyze(segment, fs, nhop, 60, 800);
            var voicedF0 = f0.Where(x => x > 0).ToArray();
            srcF0 = voicedF0.Length > 0 ? voicedF0.Average() : targetF0;
            if (bypassFrq && frqData != null)
            {
                Console.WriteLine($"  [Pitch] Initial F0 from PYIN (P flag, bypassing FRQ): {srcF0:F1}Hz");
            }
            else
            {
                Console.WriteLine($"  [Pitch] Initial F0 from PYIN: {srcF0:F1}Hz");
            }
        }
        
        // LLSM 解析（test-layer0-anasynth.cの最適化設定を適用）
        using var aopt = Llsm.CreateAnalysisOptions();
        
        // 分析オプション最適化（高品質設定）
        unsafe
        {
            var aoptPtr = (NativeLLSM.llsm_aoptions*)aopt.DangerousGetHandle().ToPointer();
            aoptPtr->npsd = 128;          // PSDサイズ（128=標準、256だとノイズ過剰）
            aoptPtr->maxnhar = 800;       // 最大倍音数（8kHzまでカバー、F0=100Hz時）
            aoptPtr->maxnhar_e = 5;       // エンベロープ最大倍音数
            aoptPtr->hm_method = 1;       // LLSM_AOPTION_HMCZT（CZT法、高品質）
        }
        
        using var chunk = Llsm.Analyze(aopt, segment, fs, f0, f0.Length);
        
        // LLSM解析で実際に使用されたThopを取得
        var conf = Llsm.GetConf(chunk);
        float actualThop = Llsm.GetThopSeconds(conf);
        
        int nfrm = Llsm.GetNumFrames(chunk);
        
        // Eフラグ: FRQからのF0を使用（デフォルトはLLSMの推定を使用）
        bool useFrqF0 = flags.Contains("E", StringComparison.OrdinalIgnoreCase);
        
        // Bフラグ: 息成分の強度（UTAU仕様：デフォルト50 = 標準、0 = 息なし、100 = ささやき）
        int breathiness = 50;
        var bMatch = System.Text.RegularExpressions.Regex.Match(flags, @"B(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bMatch.Success)
        {
            breathiness = int.Parse(bMatch.Groups[1].Value);
        }
        
        // gフラグ: ジェンダーファクター（デフォルト0 = 変化なし、+値 = 男性的、-値 = 女性的）
        int genderFactor = 0;
        var gMatch = System.Text.RegularExpressions.Regex.Match(flags, @"g([+-]?\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (gMatch.Success)
        {
            genderFactor = int.Parse(gMatch.Groups[1].Value);
        }
        
        // Fフラグ: フォルマント追従率（デフォルト100 = 完全追従、0 = 固定、100 = 完全追従）
        int formantFollow = 100;  // デフォルト100%（処理スキップ）
        var fMatch = System.Text.RegularExpressions.Regex.Match(flags, @"F(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fMatch.Success)
        {
            formantFollow = int.Parse(fMatch.Groups[1].Value);
            formantFollow = Math.Clamp(formantFollow, 0, 100);  // 0-100%に制限
        }
        
        // Nフラグ: Neural Vocoder (HiFi-GAN) 音質向上（デフォルト0 = オフ、100 = 最大）
        // 注意: N1-N99は将来の拡張用予約（現在は0か100のみ）
        int neuralVocoderStrength = 0;  // デフォルトオフ
        var nMatch = System.Text.RegularExpressions.Regex.Match(flags, @"N(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (nMatch.Success)
        {
            neuralVocoderStrength = int.Parse(nMatch.Groups[1].Value);
            neuralVocoderStrength = Math.Clamp(neuralVocoderStrength, 0, 100);  // 0-100%に制限
        }
        
        // Rフラグ: Residual noise copy（指定なし=補間PSDRES使用、指定あり=ランダムコピー有効）
        // 注意: R指定は長時間ストレッチで末尾がノイジーになる可能性があります
        bool useResidualCopy = flags.Contains("R", StringComparison.OrdinalIgnoreCase);
        
        // M+フラグ: Modulation+（ノート境界でmodulationをフェード、クロスフェード時のピッチずれを防止）
        bool useModPlus = flags.Contains("M+", StringComparison.Ordinal) || flags.Contains("M1", StringComparison.OrdinalIgnoreCase);
        if (useModPlus)
        {
            Console.WriteLine($"  [Modulation] M+ enabled: boundary fade for modulation={modulation}");
        }
        
        // Oフラグ: オーバーサンプリング（指定なし=無効、指定あり=4倍有効）
        bool useOversampling = flags.Contains("O", StringComparison.OrdinalIgnoreCase);
        
        if (useFrqF0 && frqData != null)
        {
            // FRQのF0をそのまま使用
            Console.WriteLine($"  [Pitch] Using FRQ F0 (E flag): {srcF0:F1}Hz");
        }
        else
        {
            // LLSM の解析結果から srcF0 を再計算（より正確）
            // 中央値を使用して外れ値の影響を軽減
            var llsmF0Values = new List<float>();
            for (int i = 0; i < nfrm; i++)
            {
                var frame = Llsm.GetFrame(chunk, i);
                float frameF0 = Llsm.GetFrameF0(frame);
                if (frameF0 > 0) llsmF0Values.Add(frameF0);
            }
            if (llsmF0Values.Count > 0)
            {
                // 中央値を計算
                llsmF0Values.Sort();
                float median;
                int count = llsmF0Values.Count;
                if (count % 2 == 0)
                {
                    median = (llsmF0Values[count / 2 - 1] + llsmF0Values[count / 2]) / 2f;
                }
                else
                {
                    median = llsmF0Values[count / 2];
                }
                
                // 中央値の ±50% 以内の値だけを使って平均を計算（外れ値除去）
                float minF0 = median * 0.5f;
                float maxF0 = median * 1.5f;
                var filteredF0 = llsmF0Values.Where(x => x >= minF0 && x <= maxF0).ToArray();
                
                if (filteredF0.Length > 0)
                {
                    srcF0 = filteredF0.Average();
                    Console.WriteLine($"  [Pitch] srcF0 from LLSM: {srcF0:F1}Hz (median={median:F1}, used {filteredF0.Length}/{llsmF0Values.Count})");
                }
                else
                {
                    srcF0 = median;
                    Console.WriteLine($"  [Pitch] srcF0 from LLSM (median): {srcF0:F1}Hz");
                }
            }
        }
        
        // 子音速度（velocity）から子音ストレッチ比率を計算
        // velocity = 100: 標準速度（1.0x）
        // velocity < 100: 子音が長くなる（例：50 = 2.0x）
        // velocity > 100: 子音が短くなる（例：200 = 0.5x）
        float consonantStretch = velocity > 0 ? 100.0f / velocity : 1.0f;
        Console.WriteLine($"  [Velocity] velocity={velocity}, consonantStretch={consonantStretch:F3}x");
        
        // 子音部と伸縮部のフレーム数
        // 原音設定の「先行発声」からの初期推定
        int consonantFrames = (int)(consonant / 1000.0 / actualThop);
        consonantFrames = Math.Min(consonantFrames, nfrm);
        
        // Vフラグ指定時のみF0ベース自動修正を有効化
        bool useF0Boundary = flags.Contains("V", StringComparison.OrdinalIgnoreCase);

        // F0ベースで無声→有声境界を推定（より正確な子音境界検出）
        int detectedConsonantFrames = DetectConsonantBoundary(chunk, nfrm);
        if (useF0Boundary && detectedConsonantFrames > 0 && detectedConsonantFrames < nfrm)
        {
            // 検出された境界と原音設定の境界を比較
            // 差が大きい場合は検出値を使用（連続音の場合に有効）
            int diffFrames = Math.Abs(consonantFrames - detectedConsonantFrames);
            float diffMs = diffFrames * actualThop * 1000f;
            Console.WriteLine($"  [Consonant] OtoSetting: {consonantFrames} frames, Detected: {detectedConsonantFrames} frames (diff: {diffMs:F1}ms)");
            // 差が50ms以上ある場合は検出値を優先
            if (diffMs > 50)
            {
                consonantFrames = detectedConsonantFrames;
                Console.WriteLine($"  [Consonant] Using detected boundary: {consonantFrames} frames");
            }
        } else if (useF0Boundary) {
            Console.WriteLine($"  [Consonant] F0 boundary not detected or not used (detected={detectedConsonantFrames})");
        }
        
        int stretchableFrames = nfrm - consonantFrames;
        
        // 出力の要求長さに基づいてストレッチ率を計算
        // 子音部は velocity でストレッチ、残りの部分だけを伸縮
        float stretchRatio = 1.0f;
        if (lengthReq > 0 && stretchableFrames > 0)
        {
            // 子音部はvelocityで伸縮後の長さ
            float consonantMs = consonantFrames * actualThop * 1000f * consonantStretch;
            float stretchableMs = stretchableFrames * actualThop * 1000f;
            float targetStretchableMs = lengthReq - consonantMs;
            
            if (targetStretchableMs > 0)
            {
                stretchRatio = targetStretchableMs / stretchableMs;
            }
            
            Console.WriteLine($"  [Stretch] ConsonantFrames: {consonantFrames} ({consonantMs:F1}ms, with velocity)");
            Console.WriteLine($"  [Stretch] StretchableFrames: {stretchableFrames} ({stretchableMs:F1}ms)");
            Console.WriteLine($"  [Stretch] Target: {lengthReq}ms, TargetStretchable: {targetStretchableMs:F1}ms");
            Console.WriteLine($"  [Stretch] Ratio: {stretchRatio:F3}");
        }
        
        // ピッチ比率（実測値を基準に計算）
        float pitchRatio = targetF0 / srcF0;
        
        // デバッグ: ピッチ情報を出力
        // Console.WriteLine($"  [Debug] srcF0={srcF0:F1}Hz, targetF0={targetF0:F1}Hz, pitchRatio={pitchRatio:F3}");
        
        // M+ フェード長をcutoffから計算（オーバーラップ領域）
        float overlapMs = 0;
        if (useModPlus && cutoff < 0)
        {
            // cutoffの絶対値から子音部を除いた部分がオーバーラップ領域の推定値
            overlapMs = Math.Max(0, Math.Abs(cutoff) - consonant);
            if (overlapMs > 0)
            {
                Console.WriteLine($"  [M+] Estimated overlap from cutoff: {overlapMs:F1}ms");
            }
        }
        
        // 合成（子音部velocity + 伸縮部）
        float[] output;
        if (Math.Abs(stretchRatio - 1.0f) < 0.01f && Math.Abs(consonantStretch - 1.0f) < 0.01f && pitchBend.Count == 0)
        {
            // ストレッチなし、ピッチベンドなし → 単純なピッチシフト
            output = SynthesizeWithPitch(chunk, fs, srcF0, targetF0, formantFollow, modulation, useModPlus, actualThop, overlapMs);
        }
        else
        {
            // 子音部velocity + 伸縮部ストレッチ
            output = SynthesizeWithConsonantAndStretch(chunk, fs, srcF0, targetF0, 
                                                        consonantFrames, consonantStretch, stretchRatio, pitchBend, tempo, breathiness, genderFactor, formantFollow, actualThop, useOversampling, useResidualCopy, modulation, useModPlus, overlapMs);
        }
        
        // ボリューム適用
        if (volume != 100)
        {
            float volScale = volume / 100.0f;
            for (int i = 0; i < output.Length; i++)
            {
                output[i] *= volScale;
            }
        }
        
        // ピークレベルを測定して音量を適切な範囲に調整
        float peak = 0f;
        for (int i = 0; i < output.Length; i++)
        {
            float absVal = Math.Abs(output[i]);
            if (absVal > peak) peak = absVal;
        }
        
        // 音量範囲調整：適切な範囲（-9dB～-0.5dB）に収める
        const float minPeak = 0.35f;  // -9dB: これより小さい音声は引き上げる
        const float maxPeak = 0.95f;  // -0.5dB: これより大きい音声は抑える
        
        if (peak > maxPeak)
        {
            // 大きすぎる：抑える
            float normalizeGain = maxPeak / peak;
            Console.WriteLine($"  [Volume Adjust] Peak={peak:F3} ({20*Math.Log10(peak):+0.1;-0.1}dB) is too loud → {maxPeak:F3} ({20*Math.Log10(maxPeak):+0.1;-0.1}dB), gain={normalizeGain:F3}x");
            for (int i = 0; i < output.Length; i++)
            {
                output[i] *= normalizeGain;
            }
        }
        else if (peak < minPeak && peak > 0.0f)
        {
            // 小さすぎる：引き上げる
            float normalizeGain = minPeak / peak;
            Console.WriteLine($"  [Volume Adjust] Peak={peak:F3} ({20*Math.Log10(peak):+0.1;-0.1}dB) is too quiet → {minPeak:F3} ({20*Math.Log10(minPeak):+0.1;-0.1}dB), gain={normalizeGain:F3}x");
            for (int i = 0; i < output.Length; i++)
            {
                output[i] *= normalizeGain;
            }
        }
        else
        {
            Console.WriteLine($"  [Volume Adjust] Peak={peak:F3} ({20*Math.Log10(peak):+0.1;-0.1}dB) is within acceptable range, no adjustment");
        }
        
        // Neural Vocoder適用（Nフラグ）- DISABLED (STFT品質問題のため一時無効化)
        if (false && neuralVocoderStrength > 0)
        {
            try
            {
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "hifigan.onnx");
                if (File.Exists(modelPath))
                {
                    Console.WriteLine($"  [NeuralVocoder] Applying enhancement (N={neuralVocoderStrength})...");
                    using var vocoder = new NeuralVocoder(modelPath);
                    vocoder.LoadModel();
                    var enhanced = vocoder.Enhance(output, fs);
                    
                    // ブレンド（強度に応じて原音とミックス）
                    // 注意: 簡易STFT実装のため、効果を抑えめに
                    float blendRatio = neuralVocoderStrength / 1000.0f; // 1/10に減衰
                    int minLen = Math.Min(output.Length, enhanced.Length);
                    for (int i = 0; i < minLen; i++)
                    {
                        output[i] = output[i] * (1 - blendRatio) + enhanced[i] * blendRatio;
                    }
                    Console.WriteLine($"  [NeuralVocoder] Enhancement complete (blend={blendRatio:F3})");
                }
                else
                {
                    Console.WriteLine($"  [NeuralVocoder] Model not found at {modelPath}, skipping");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [NeuralVocoder] Error: {ex.Message}, skipping enhancement");
            }
        }
        
        // 出力
        Wav.WriteMono16(outputWav, output, fs);
    }
    
    /// <summary>
    /// 子音部velocity + 伸縮部ストレッチして合成
    /// </summary>
    static float[] SynthesizeWithConsonantAndStretch(ChunkHandle srcChunk, int fs, float srcF0, float targetF0,
                                                      int consonantFrames, float consonantStretch, float stretchRatio, List<int> pitchBend, int tempo = 120, int breathiness = 50, int genderFactor = 0, int formantFollow = 100, float actualThop = 0.005f, bool useOversampling = false, bool useResidualCopy = false, int modulation = 100, bool useModPlus = false, float overlapMs = 0)
    {
        float basePitchRatio = targetF0 / srcF0;
        
        // ピッチベンドのラップアラウンドを補正
        var unwrappedPb = UnwrapPitchBend(pitchBend);
        
        // 重要: 逆位相伝播はLayer0状態で実行する必要がある（Layer1には位相情報がない）
        // 逆位相伝播（編集前に位相依存性を除去）
        Llsm.ChunkPhasePropagate(srcChunk, -1);
        
        // Layer1 に変換（NFFT=8192で高域まで正確に表現）
        Llsm.ChunkToLayer1(srcChunk, 8192);
        
        int srcNfrm = Llsm.GetNumFrames(srcChunk);
        int stretchableFrames = srcNfrm - consonantFrames;
        
        // 子音部はvelocityでストレッチ、伸縮部は lengthReq でストレッチ
        int dstConsonantFrames = (int)(consonantFrames * consonantStretch);
        int dstStretchedFrames = (int)(stretchableFrames * stretchRatio);
        int dstNfrm = dstConsonantFrames + dstStretchedFrames;
        
        // ピッチベンドを出力フレーム数に合わせて補間
        // テンポからUTAUのピッチベンド時間軸を計算（96分音符単位）
        float[] interpolatedPb = new float[dstNfrm];
        if (unwrappedPb.Count > 0)
        {
            float outputDurationMs = dstNfrm * actualThop * 1000f;
            
            // UTAUのピッチベンドタイミング（96分音符単位 = 60/96/bpm 秒ごと）
            float utauPbIntervalMs = 60.0f / 96.0f / tempo * 1000f;
            int utauPbLength = (int)(outputDurationMs / utauPbIntervalMs) + 1;
            
            // UTAUピッチ配列を必要な長さに調整（0パディングまたは切り詰め）
            float[] paddedPb = new float[utauPbLength];
            for (int j = 0; j < utauPbLength; j++)
            {
                if (j < unwrappedPb.Count)
                    paddedPb[j] = unwrappedPb[j];
                else
                    paddedPb[j] = 0;  // 0パディング
            }
            
            // UTAUタイミング配列を生成
            float[] utauT = new float[utauPbLength];
            for (int j = 0; j < utauPbLength; j++)
            {
                utauT[j] = j * utauPbIntervalMs;
            }
            
            // 出力タイミング配列を生成（actualThop秒間隔）
            float[] outputT = new float[dstNfrm];
            for (int j = 0; j < dstNfrm; j++)
            {
                outputT[j] = j * actualThop * 1000f;
            }
            
            // 線形補間
            interpolatedPb = InterpolatePitchBend(utauT, outputT, paddedPb);
            
            Console.WriteLine($"  [PB] Input points: {unwrappedPb.Count}, UTAU interval: {utauPbIntervalMs:F2}ms");
            Console.WriteLine($"  [PB] Padded to: {utauPbLength}, Output frames: {dstNfrm}");
            Console.WriteLine($"  [PB] Interpolated range: {interpolatedPb.Min():F1} to {interpolatedPb.Max():F1} cents");
        }
        
        // 新しい chunk を作成
        var conf = Llsm.GetConf(srcChunk);
        var confCopy = Llsm.CopyContainer(conf);
        var nfrmPtr = NativeLLSM.llsm_container_get(confCopy.Ptr, NativeLLSM.LLSM_CONF_NFRM);
        Marshal.WriteInt32(nfrmPtr, dstNfrm);
        
        var dstChunk = Llsm.CreateChunk(confCopy, 0);
        
        // Frame interpolation debug logs disabled
        // Console.WriteLine($"[DEBUG] Starting frame interpolation loop: dstNfrm={dstNfrm}, srcNfrm={srcNfrm}");
        // Console.WriteLine($"[DEBUG] consonantFrames={consonantFrames}, dstConsonantFrames={dstConsonantFrames}");
        // Console.WriteLine($"[DEBUG] stretchableFrames={stretchableFrames}, dstStretchedFrames={dstStretchedFrames}");
        
        // 高精度ピッチシフト: targetF0 / srcF0の比率を全フレームに適用
        // 各フレームのF0変動（ビブラート等）を保持しつつ全体をシフト
        float pitchShiftRatio = targetF0 / srcF0;
        Console.WriteLine($"[TimeStretch] Pitch shift ratio: {pitchShiftRatio:F4} ({srcF0:F1}Hz -> {targetF0:F1}Hz)");
        
        // dstF0配列を準備（位相同期処理用）
        float[] dstF0 = new float[dstNfrm];
        
        for (int i = 0; i < dstNfrm; i++)
        {
            // ストレッチ位置を計算
            float srcPosFloat;
            if (i < dstConsonantFrames)
            {
                // 子音部: velocity でストレッチ
                float consonantPos = (float)i / dstConsonantFrames;
                srcPosFloat = consonantPos * consonantFrames;
            }
            else
            {
                // 伸縮部: ストレッチ
                float stretchedPos = (float)(i - dstConsonantFrames) / dstStretchedFrames;
                srcPosFloat = consonantFrames + stretchedPos * stretchableFrames;
            }
            
            // フレーム補間のためのインデックス計算
            int srcIdx1 = (int)Math.Floor(srcPosFloat);
            int srcIdx2 = srcIdx1 + 1;
            float ratio = srcPosFloat - srcIdx1;  // 0.0 ~ 1.0
            
            // 境界チェック
            srcIdx1 = Math.Max(0, Math.Min(srcIdx1, srcNfrm - 1));
            srcIdx2 = Math.Max(0, Math.Min(srcIdx2, srcNfrm - 1));
            
            // 子音部の判定（補間とピッチシフトで使用）
            bool isConsonant = (i < dstConsonantFrames);
            
            // フレーム補間
            IntPtr newFramePtr;
            if (srcIdx1 == srcIdx2 || ratio < 0.01f)
            {
                // 整数位置またはratioがほぼ0: そのままコピー
                // Console.WriteLine($"[Frame {i}] Copy srcIdx1={srcIdx1} (ratio={ratio:F3})");
                var srcFrame = Llsm.GetFrame(srcChunk, srcIdx1);
                newFramePtr = Llsm.CopyFrame(srcFrame);
            }
            else if (ratio > 0.99f)
            {
                // ratioがほぼ1: 次のフレームをコピー
                // Console.WriteLine($"[Frame {i}] Copy srcIdx2={srcIdx2} (ratio={ratio:F3})");
                var srcFrame = Llsm.GetFrame(srcChunk, srcIdx2);
                newFramePtr = Llsm.CopyFrame(srcFrame);
            }
            else
            {
                // 全フレームでキュービック補間を使用（高品質・位相連続性重視）
                // 子音/母音の境界での補間方式の違いをなくし、滑らかな位相遷移を保証
                
                // キュービック補間が可能かチェック（前後1フレームずつ必要）
                bool canUseCubic = (srcIdx1 > 0) && (srcIdx2 < srcNfrm - 1);
                
                if (canUseCubic)
                {
                    // 4点Catmull-Rom補間（高品質なスペクトル補間）
                    // 位相連続性を改善するため、補間前にRPSを適用
                    // 現在Layer1状態なのでlayer1_based=1を指定
                    var srcFrame0 = Llsm.GetFrame(srcChunk, srcIdx1 - 1);
                    var srcFrame1 = Llsm.GetFrame(srcChunk, srcIdx1);
                    var srcFrame2 = Llsm.GetFrame(srcChunk, srcIdx2);
                    var srcFrame3 = Llsm.GetFrame(srcChunk, srcIdx2 + 1);
                    
                    // 元のフレームを保護するためコピーしてからRPSを適用
                    var frame0Copy = Llsm.CopyFrame(srcFrame0);
                    var frame1Copy = Llsm.CopyFrame(srcFrame1);
                    var frame2Copy = Llsm.CopyFrame(srcFrame2);
                    var frame3Copy = Llsm.CopyFrame(srcFrame3);
                    NativeLLSM.llsm_frame_phasesync_rps(frame0Copy, 1);  // Layer1用
                    NativeLLSM.llsm_frame_phasesync_rps(frame1Copy, 1);  // Layer1用
                    NativeLLSM.llsm_frame_phasesync_rps(frame2Copy, 1);  // Layer1用
                    NativeLLSM.llsm_frame_phasesync_rps(frame3Copy, 1);  // Layer1用
                    
                    var frame0Ref = new ContainerRef(frame0Copy);
                    var frame1Ref = new ContainerRef(frame1Copy);
                    var frame2Ref = new ContainerRef(frame2Copy);
                    var frame3Ref = new ContainerRef(frame3Copy);
                    newFramePtr = InterpolateFrameCubic(frame0Ref, frame1Ref, frame2Ref, frame3Ref, ratio);
                    
                    // コピーしたフレームを解放
                    NativeLLSM.llsm_delete_container(frame0Copy);
                    NativeLLSM.llsm_delete_container(frame1Copy);
                    NativeLLSM.llsm_delete_container(frame2Copy);
                    NativeLLSM.llsm_delete_container(frame3Copy);
                }
                else
                {
                    // 境界付近では線形補間にフォールバック（前後フレームが不足）
                    // 位相連続性を改善するため、補間前にRPSを適用
                    var srcFrame1 = Llsm.GetFrame(srcChunk, srcIdx1);
                    var srcFrame2 = Llsm.GetFrame(srcChunk, srcIdx2);
                    
                    var frame1Copy = Llsm.CopyFrame(srcFrame1);
                    var frame2Copy = Llsm.CopyFrame(srcFrame2);
                    NativeLLSM.llsm_frame_phasesync_rps(frame1Copy, 1);  // Layer1用
                    NativeLLSM.llsm_frame_phasesync_rps(frame2Copy, 1);  // Layer1用
                    
                    var frame1Ref = new ContainerRef(frame1Copy);
                    var frame2Ref = new ContainerRef(frame2Copy);
                    newFramePtr = InterpolateFrame(frame1Ref, frame2Ref, ratio);
                    
                    NativeLLSM.llsm_delete_container(frame1Copy);
                    NativeLLSM.llsm_delete_container(frame2Copy);
                }
            }
            
            // ピッチを適用
            // 高精度ピッチシフト: フレームごとにtargetF0 / frameF0でピッチ比率を計算
            // 子音部ではF0が低い場合ノイズが出やすいので、品質チェックを行う
            // newF0 = targetF0 * 2^(pitchBend/1200)
            
            // 補間されたフレームからF0を取得
            var newFrameRef = new ContainerRef(newFramePtr);
            float originalF0 = Llsm.GetFrameF0(newFrameRef);  // 元のF0を保存
            float newF0 = 0;
            
            // 子音部低品質の判定（gフラグ・Fフラグでも使用）
            bool isConsonantLowQuality = isConsonant && (originalF0 > 0 && originalF0 < 150.0f);
            
            // PSDRES（残差ノイズ）を近傍ランダムフレームからコピー（ノイズの自然な変動を保つ）
            // Rフラグで制御: 指定なし=補間PSDRES使用（デフォルト）、R指定=ランダムコピー（demo-stretch.c互換）
            // 注意: R指定は長時間ストレッチで末尾がノイジーになる可能性があります
            if (useResidualCopy)
            {
                Random rnd = new Random(i); // フレーム番号をシードにして再現性を保つ
                int residualOffset = rnd.Next(5) - 2; // -2 ~ +2
                int residualIdx = srcIdx1 + residualOffset;
                residualIdx = Math.Max(0, Math.Min(residualIdx, srcNfrm - 1));
                
                var residualFrame = Llsm.GetFrame(srcChunk, residualIdx);
                var psdresPtr = NativeLLSM.llsm_container_get(residualFrame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES);
                if (psdresPtr != IntPtr.Zero)
                {
                    var psdresCopy = NativeLLSM.llsm_copy_fparray(psdresPtr);
                    NativeLLSM.llsm_container_attach_(newFramePtr, NativeLLSM.LLSM_FRAME_PSDRES,
                        psdresCopy, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                        Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
                }
            }
            
            // 息成分（breathiness）を適用
            // 子音部にも適用することで、全体的に息っぽくなる
            // PSDRESランダムコピー後に実行して、Bフラグの効果を正確に反映
            if (breathiness != 50)
            {
                ApplyBreathiness(newFrameRef, breathiness);
            }
            
            // ノイズ成分（PSDRES + NM）をピッチシフトから保護するため、Breathiness適用後にバックアップ
            // ノイズは本質的にピッチを持たないため、ピッチシフトすると不自然な変化が生じる
            IntPtr originalPsdres = IntPtr.Zero;
            IntPtr originalNm = IntPtr.Zero;
            
            var currentPsdresPtr = NativeLLSM.llsm_container_get(newFramePtr, NativeLLSM.LLSM_FRAME_PSDRES);
            if (currentPsdresPtr != IntPtr.Zero)
            {
                originalPsdres = NativeLLSM.llsm_copy_fparray(currentPsdresPtr);
            }
            
            // NM（Noise Model）全体もバックアップ（子音の自然さを保つ）
            var currentNmPtr = NativeLLSM.llsm_container_get(newFramePtr, NativeLLSM.LLSM_FRAME_NM);
            if (currentNmPtr != IntPtr.Zero)
            {
                originalNm = NativeLLSM.llsm_copy_nmframe(currentNmPtr);
            }
            
            if (originalF0 > 0)
            {
                // isConsonant, isConsonantLowQuality は既に上で定義済み
                
                if (isConsonantLowQuality)
                {
                    // 子音部低品質フレーム: F0とVTMAGNをそのまま使用（ピッチシフト・フォルマント編集なし）
                    // 自然な子音を保つため、スペクトル編集を最小限に
                    newF0 = originalF0;
                    // Console.WriteLine($"    [Frame {i}] Consonant low quality F0={originalF0:F1}Hz, skipping modifications");
                }
                else
                {
                    // 正常品質フレーム: ピッチシフト適用
                    // M+フラグ: ノート境界でmodulationをフェード
                    float dynamicMod = useModPlus 
                        ? GetDynamicModulation(i, dstNfrm, modulation, actualThop, overlapMs)
                        : modulation;
                    
                    // modulationを適用して原音のピッチ揺らぎを調整
                    float deviation = originalF0 - srcF0;  // 平均からのずれ（揺らぎ成分）
                    float modRatio = dynamicMod / 100.0f;
                    float adjustedSourceF0 = srcF0 + deviation * modRatio;  // 揺らぎをスケール
                    
                    float pitchRatio = targetF0 / srcF0;
                    newF0 = adjustedSourceF0 * pitchRatio;  // 揺らぎを保持してシフト
                    
                    // ピッチベンドを適用（補間済み配列から取得）
                    float pbCents = 0;
                    if (interpolatedPb.Length > 0 && i < interpolatedPb.Length)
                    {
                        pbCents = interpolatedPb[i];
                        newF0 *= (float)Math.Pow(2, pbCents / 1200.0);
                    }
                    
                    // ピッチシフトによる振幅補正（test-layer1-anasynth.cと同じ）
                    // 子音部は振幅補正をスキップ（オーバーシュートを防ぐ）
                    if (!isConsonant)
                    {
                        float amplitudeCompensation = -20.0f * MathF.Log10(pitchRatio);
                        var vtmagnPtr = NativeLLSM.llsm_container_get(newFramePtr, NativeLLSM.LLSM_FRAME_VTMAGN);
                        if (vtmagnPtr != IntPtr.Zero)
                        {
                            int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                            float[] vtmagn = new float[nspec];
                            Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                            for (int j = 0; j < nspec; j++)
                            {
                                vtmagn[j] += amplitudeCompensation;
                            }
                            Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
                        }
                    }
                }
                
                // 新しいF0をフレームに設定
                var newF0Ptr = NativeLLSM.llsm_create_fp(newF0);
                NativeLLSM.llsm_container_attach_(newFramePtr, NativeLLSM.LLSM_FRAME_F0,
                    newF0Ptr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
                
                // dstF0配列に保存（位相同期処理用）
                dstF0[i] = newF0;
            }
            else
            {
                // F0がない場合は0を設定
                dstF0[i] = 0;
            }
            
            // ジェンダーファクター（gフラグ）: Layer1での処理
            // 子音部低品質フレームではスキップして自然さを保持
            if (genderFactor != 0 && !isConsonantLowQuality)
            {
                ApplyGenderFactor(newFrameRef, genderFactor);
            }
            
            // 適応的フォルマント処理（Fフラグ）: Layer1での処理
            // pitchShiftRatioのみを使用（ピッチベンドの影響を除外）
            // 子音部低品質フレームではスキップ
            if (formantFollow != 100 && !isConsonantLowQuality)
            {
                ApplyAdaptiveFormantToFparray(newFrameRef, pitchShiftRatio, formantFollow);
            }
            
            // ノイズ成分（PSDRES + NM）を復元: ピッチシフト/フォルマント処理の影響を受けないようにする
            // ピッチシフトは調和成分のみに適用し、ノイズ成分は元の周波数特性を維持
            if (originalPsdres != IntPtr.Zero)
            {
                NativeLLSM.llsm_container_attach_(newFramePtr, NativeLLSM.LLSM_FRAME_PSDRES,
                    originalPsdres, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                    Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
            }
            
            // NM（Noise Model）も復元して子音の自然な特性を保持
            if (originalNm != IntPtr.Zero)
            {
                NativeLLSM.llsm_container_attach_(newFramePtr, NativeLLSM.LLSM_FRAME_NM,
                    originalNm, Marshal.GetFunctionPointerForDelegate(_deleteNm), 
                    Marshal.GetFunctionPointerForDelegate(_copyNm));
            }
            
            Llsm.SetFrame(dstChunk, i, newFramePtr);
        }
        
        // gフラグとFフラグは既に各フレーム処理時に適用済み（Layer1で処理完了）
        
        // 子音と母音の境界をスムージング（プツっとした音を防ぐ）- DISABLED
        // 位相同期（RPS）で対応するため、スペクトルフェードは無効化
        if (false && dstConsonantFrames > 0 && dstConsonantFrames < dstNfrm)
        {
            int fadeFrames = Math.Min(10, Math.Min(dstConsonantFrames, dstNfrm - dstConsonantFrames));
            Console.WriteLine($"[Synthesis] Smoothing consonant-vowel boundary: {fadeFrames} frames crossfade at frame {dstConsonantFrames}");
            
            for (int i = 0; i < fadeFrames; i++)
            {
                int frameIdx = dstConsonantFrames - fadeFrames + i;
                if (frameIdx < 0 || frameIdx >= dstNfrm) continue;
                
                // コサイン窓によるスムーズなフェード（0.0～1.0）
                float fadeRatio = 0.5f * (1.0f - MathF.Cos(MathF.PI * i / fadeFrames));
                
                var frame = Llsm.GetFrame(dstChunk, frameIdx);
                
                // 調和成分（VTMAGN）を全帯域でフェード
                var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr != IntPtr.Zero)
                {
                    int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                    float[] vtmagn = new float[nspec];
                    Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                    
                    // 周波数に応じてフェード量を調整（高域ほど強く）
                    for (int j = 0; j < nspec; j++)
                    {
                        float freqWeight = (float)j / nspec;  // 0.0～1.0
                        float fadeDb = -6.0f * (1.0f - fadeRatio) * (0.3f + 0.7f * freqWeight);
                        vtmagn[j] += fadeDb;
                    }
                    
                    Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
                }
                
                // ノイズ成分（NM PSD）もフェード
                var nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
                if (nmPtr != IntPtr.Zero)
                {
                    var psdPtr = NativeLLSM.llsm_container_get(nmPtr, 2);  // NM.psd
                    if (psdPtr != IntPtr.Zero)
                    {
                        int npsd = NativeLLSM.llsm_fparray_length(psdPtr);
                        float[] psd = new float[npsd];
                        Marshal.Copy(psdPtr, psd, 0, npsd);
                        
                        float fadeDb = -6.0f * (1.0f - fadeRatio);
                        for (int j = 0; j < npsd; j++)
                        {
                            psd[j] += fadeDb;
                        }
                        
                        Marshal.Copy(psd, 0, psdPtr, npsd);
                    }
                }
            }
        }
        
        // Layer0 に変換
        Console.WriteLine($"[Synthesis] Converting {dstNfrm} frames to Layer0...");
        Llsm.ChunkToLayer0(dstChunk);
        
        // 位相同期処理（RPS: Repeated Phase Sync）
        Console.WriteLine($"[Synthesis] Phase sync RPS...");
        unsafe
        {
            // F0配列を準備
            fixed (float* f0Ptr = dstF0)
            {
                NativeLLSM.llsm_chunk_phasesync_rps(dstChunk.DangerousGetHandle(), (IntPtr)f0Ptr, dstNfrm);
            }
        }
        
        // 順方向位相伝播
        Console.WriteLine($"[Synthesis] Phase propagate...");
        Llsm.ChunkPhasePropagate(dstChunk, +1);
        
        // オーバーサンプリング（Oフラグ有効時のみ）
        float[] oversampledOutput;
        if (useOversampling)
        {
            // 4倍オーバーサンプリングで合成（ノイズ削減のため高サンプリングレート使用）
            int oversampleRate = 4;
            int synthesisFs = fs * oversampleRate;
            Console.WriteLine($"[Synthesis] Oversampling enabled: {oversampleRate}x ({synthesisFs}Hz)");
            using var sopt = Llsm.CreateSynthesisOptions(synthesisFs);
            using var output = Llsm.Synthesize(sopt, dstChunk);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Synthesis] llsm_synthesize succeeded");
            Console.ResetColor();
            
            oversampledOutput = Llsm.ReadOutput(output);
            
            // ダウンサンプリング
            Console.WriteLine($"[Synthesis] Downsampling from {synthesisFs}Hz to {fs}Hz...");
            return Downsample(oversampledOutput, oversampleRate);
        }
        else
        {
            // 直接44.1kHzで合成（オーバーサンプリングなし）
            Console.WriteLine($"[Synthesis] Direct synthesis at {fs}Hz (no oversampling)");
            using var sopt = Llsm.CreateSynthesisOptions(fs);
            using var output = Llsm.Synthesize(sopt, dstChunk);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Synthesis] llsm_synthesize succeeded");
            Console.ResetColor();
            
            return Llsm.ReadOutput(output);
        }
    }
    
    /// <summary>
    /// 高品質ダウンサンプリング（FIRローパスフィルタ + デシメーション）
    /// 線形位相FIRフィルタを使用し、群遅延を補正して位相ずれを防止
    /// </summary>
    static float[] Downsample(float[] input, int factor)
    {
        if (factor <= 1) return input;
        
        int outputLength = input.Length / factor;
        float[] output = new float[outputLength];
        
        // FIRローパスフィルタ（windowed-sinc、ハミング窓）
        // 線形位相特性により位相歪みなし、群遅延は正確に補正可能
        int filterLength = factor * 16 + 1;  // 4倍なら65タップ（対称FIR）
        float[] firCoeffs = CreateLowpassFIR(filterLength, 0.95f / factor);
        
        int groupDelay = filterLength / 2;  // 線形位相FIRの群遅延（サンプル数）
        
        // フィルタリング + デシメーション + 群遅延補正
        // centerIdxに群遅延を加算することで位相ずれを補正
        for (int i = 0; i < outputLength; i++)
        {
            int centerIdx = i * factor;  // ダウンサンプリング位置
            float sum = 0;
            float weightSum = 0;  // 境界での正規化用
            
            for (int j = 0; j < filterLength; j++)
            {
                int idx = centerIdx - groupDelay + j;  // 群遅延補正済み
                
                // 境界処理：ミラーリング（より自然な境界処理）
                int mirroredIdx = idx;
                if (mirroredIdx < 0)
                    mirroredIdx = -mirroredIdx;  // 左端をミラー
                else if (mirroredIdx >= input.Length)
                    mirroredIdx = 2 * input.Length - mirroredIdx - 2;  // 右端をミラー
                
                mirroredIdx = Math.Clamp(mirroredIdx, 0, input.Length - 1);
                
                sum += input[mirroredIdx] * firCoeffs[j];
                weightSum += firCoeffs[j];
            }
            
            // 境界付近での正規化（係数の和が1でない場合の補正）
            output[i] = weightSum > 0 ? sum / weightSum : sum;
        }
        
        return output;
    }
    
    /// <summary>
    /// Windowed-sinc FIRローパスフィルタ係数を生成（ハミング窓）
    /// </summary>
    static float[] CreateLowpassFIR(int length, float cutoffRatio)
    {
        float[] coeffs = new float[length];
        int center = length / 2;
        float sum = 0;
        
        for (int i = 0; i < length; i++)
        {
            int n = i - center;
            
            // Sinc関数
            float sinc;
            if (n == 0)
                sinc = 1.0f;
            else
            {
                float x = MathF.PI * cutoffRatio * n;
                sinc = MathF.Sin(x) / x;
            }
            
            // ハミング窓
            float hamming = 0.54f - 0.46f * MathF.Cos(2.0f * MathF.PI * i / (length - 1));
            
            coeffs[i] = sinc * hamming * cutoffRatio;
            sum += coeffs[i];
        }
        
        // 正規化（ゲイン = 1）
        for (int i = 0; i < length; i++)
        {
            coeffs[i] /= sum;
        }
        
        return coeffs;
    }
    
    /// <summary>
    /// 4つのフレームをCatmull-Romスプライン補間
    /// </summary>
    static IntPtr InterpolateFrameCubic(ContainerRef frame0, ContainerRef frame1, ContainerRef frame2, ContainerRef frame3, float ratio)
    {
        // frame1とframe2の間を補間、frame0とframe3は制御点として使用
        float f0_0 = Llsm.GetFrameF0(frame0);
        float f0_1 = Llsm.GetFrameF0(frame1);
        float f0_2 = Llsm.GetFrameF0(frame2);
        float f0_3 = Llsm.GetFrameF0(frame3);
        
        // F0をキュービック補間
        float f0_interp;
        bool allVoiced = (f0_0 > 0 && f0_1 > 0 && f0_2 > 0 && f0_3 > 0);
        bool bothVoiced = (f0_1 > 0 && f0_2 > 0);
        
        if (allVoiced)
        {
            f0_interp = CubicInterp(f0_0, f0_1, f0_2, f0_3, ratio);
            f0_interp = Math.Max(50, Math.Min(800, f0_interp));
        }
        else if (bothVoiced)
        {
            f0_interp = f0_1 * (1 - ratio) + f0_2 * ratio;
        }
        else if (f0_2 > 0)
        {
            f0_interp = f0_2;
        }
        else if (f0_1 > 0)
        {
            f0_interp = f0_1;
        }
        else
        {
            f0_interp = 0;
        }
        
        // RDを取得・補間
        var rd0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_RD);
        var rd1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_RD);
        var rd2Ptr = NativeLLSM.llsm_container_get(frame2.Ptr, NativeLLSM.LLSM_FRAME_RD);
        var rd3Ptr = NativeLLSM.llsm_container_get(frame3.Ptr, NativeLLSM.LLSM_FRAME_RD);
        
        float rd0 = rd0Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd0Ptr) : 1.0f;
        float rd1 = rd1Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd1Ptr) : 1.0f;
        float rd2 = rd2Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd2Ptr) : 1.0f;
        float rd3 = rd3Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd3Ptr) : 1.0f;
        float rd_interp = CubicInterp(rd0, rd1, rd2, rd3, ratio);
        rd_interp = Math.Max(0.1f, Math.Min(2.7f, rd_interp));
        
        // VTMAGNを取得・補間
        var vtmagn0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        var vtmagn1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        var vtmagn2Ptr = NativeLLSM.llsm_container_get(frame2.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        var vtmagn3Ptr = NativeLLSM.llsm_container_get(frame3.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        
        float[] vtmagn_interp = null;
        if (vtmagn0Ptr != IntPtr.Zero && vtmagn1Ptr != IntPtr.Zero && 
            vtmagn2Ptr != IntPtr.Zero && vtmagn3Ptr != IntPtr.Zero)
        {
            int nspec0 = NativeLLSM.llsm_fparray_length(vtmagn0Ptr);
            int nspec1 = NativeLLSM.llsm_fparray_length(vtmagn1Ptr);
            int nspec2 = NativeLLSM.llsm_fparray_length(vtmagn2Ptr);
            int nspec3 = NativeLLSM.llsm_fparray_length(vtmagn3Ptr);
            int nspec = Math.Min(Math.Min(nspec0, nspec1), Math.Min(nspec2, nspec3));
            
            if (nspec > 0)
            {
                float[] vtmagn0 = new float[nspec];
                float[] vtmagn1 = new float[nspec];
                float[] vtmagn2 = new float[nspec];
                float[] vtmagn3 = new float[nspec];
                Marshal.Copy(vtmagn0Ptr, vtmagn0, 0, nspec);
                Marshal.Copy(vtmagn1Ptr, vtmagn1, 0, nspec);
                Marshal.Copy(vtmagn2Ptr, vtmagn2, 0, nspec);
                Marshal.Copy(vtmagn3Ptr, vtmagn3, 0, nspec);
                
                vtmagn_interp = new float[nspec];
                for (int i = 0; i < nspec; i++)
                {
                    vtmagn_interp[i] = CubicInterp(vtmagn0[i], vtmagn1[i], vtmagn2[i], vtmagn3[i], ratio);
                    if (float.IsNaN(vtmagn_interp[i]) || float.IsInfinity(vtmagn_interp[i]))
                    {
                        vtmagn_interp[i] = vtmagn1[i] * (1 - ratio) + vtmagn2[i] * ratio;
                    }
                    vtmagn_interp[i] = Math.Max(-80.0f, vtmagn_interp[i]);
                }
            }
        }
        
        // VSPHSEは線形補間（位相のキュービック補間は巻き戻りの可能性がある）
        var vsphse1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
        var vsphse2Ptr = NativeLLSM.llsm_container_get(frame2.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
        
        float[] vsphse_interp = null;
        if (vsphse1Ptr != IntPtr.Zero && vsphse2Ptr != IntPtr.Zero)
        {
            int nhar1 = NativeLLSM.llsm_fparray_length(vsphse1Ptr);
            int nhar2 = NativeLLSM.llsm_fparray_length(vsphse2Ptr);
            int minnhar = Math.Min(nhar1, nhar2);
            int maxnhar = Math.Max(nhar1, nhar2);
            
            if (maxnhar > 0)
            {
                float[] vsphse1 = new float[nhar1];
                float[] vsphse2 = new float[nhar2];
                Marshal.Copy(vsphse1Ptr, vsphse1, 0, nhar1);
                Marshal.Copy(vsphse2Ptr, vsphse2, 0, nhar2);
                
                vsphse_interp = new float[maxnhar];
                for (int i = 0; i < minnhar; i++)
                {
                    // 円形補間を使用（demo-stretch.c の linterpc 実装）
                    vsphse_interp[i] = CircularInterpolatePhase(vsphse1[i], vsphse2[i], ratio);
                }
                if (nhar2 > minnhar)
                {
                    for (int i = minnhar; i < maxnhar; i++)
                        vsphse_interp[i] = vsphse2[i];
                }
            }
        }
        
        // NMは線形補間
        var nm1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_NM);
        var nm2Ptr = NativeLLSM.llsm_container_get(frame2.Ptr, NativeLLSM.LLSM_FRAME_NM);
        
        IntPtr nmInterpPtr = IntPtr.Zero;
        if (nm1Ptr != IntPtr.Zero && nm2Ptr != IntPtr.Zero)
        {
            var nm1 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm1Ptr);
            var nm2 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm2Ptr);
            
            if (nm1.npsd == nm2.npsd && nm1.npsd > 0)
            {
                float[] psd1 = new float[nm1.npsd];
                float[] psd2 = new float[nm2.npsd];
                Marshal.Copy(nm1.psd, psd1, 0, nm1.npsd);
                Marshal.Copy(nm2.psd, psd2, 0, nm2.npsd);
                
                float[] psd_interp = new float[nm1.npsd];
                for (int p = 0; p < nm1.npsd; p++)
                {
                    psd_interp[p] = psd1[p] * (1 - ratio) + psd2[p] * ratio;
                }
                
                nmInterpPtr = NativeLLSM.llsm_create_nmframe(nm1.nchannel, 0, nm1.npsd);
                var nmInterp = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmInterpPtr);
                Marshal.Copy(psd_interp, 0, nmInterp.psd, nm1.npsd);
            }
        }
        
        // 新しいフレームを作成
        var newFrame = NativeLLSM.llsm_create_frame(0, 0, 0, 0);
        
        var f0Ptr = NativeLLSM.llsm_create_fp(f0_interp);
        NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_F0,
            f0Ptr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
        
        var rdPtr = NativeLLSM.llsm_create_fp(rd_interp);
        NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_RD,
            rdPtr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
        
        if (vtmagn_interp != null)
        {
            var vtmagnPtr = NativeLLSM.llsm_create_fparray(vtmagn_interp.Length);
            Marshal.Copy(vtmagn_interp, 0, vtmagnPtr, vtmagn_interp.Length);
            NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_VTMAGN,
                vtmagnPtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
        }
        
        if (vsphse_interp != null)
        {
            var vsphsePtr = NativeLLSM.llsm_create_fparray(vsphse_interp.Length);
            Marshal.Copy(vsphse_interp, 0, vsphsePtr, vsphse_interp.Length);
            NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_VSPHSE,
                vsphsePtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
        }
        
        if (nmInterpPtr != IntPtr.Zero)
        {
            NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_NM,
                nmInterpPtr, Marshal.GetFunctionPointerForDelegate(_deleteNm), IntPtr.Zero);
        }
        
        return newFrame;
    }
    
    /// <summary>
    /// 位相の円形補間（demo-stretch.c の linterpc を実装）
    /// 位相は角度なので、cos/sin で変換してから補間することで滑らかな遷移を実現
    /// </summary>
    static float CircularInterpolatePhase(float phase1, float phase2, float ratio)
    {
        // 位相を単位円上の座標に変換
        float ax = MathF.Cos(phase1);
        float ay = MathF.Sin(phase1);
        float bx = MathF.Cos(phase2);
        float by = MathF.Sin(phase2);
        
        // 座標を線形補間
        float cx = ax * (1.0f - ratio) + bx * ratio;
        float cy = ay * (1.0f - ratio) + by * ratio;
        
        // 補間された座標から位相を復元
        return MathF.Atan2(cy, cx);
    }
    
    /// <summary>
    /// 2つのLayer1フレームを線形補間して新しいフレームを生成
    /// RD、VTMAGN、VSPHSEなどのLayer1パラメータを正しく補間
    /// </summary>
    static IntPtr InterpolateFrame(ContainerRef frame0, ContainerRef frame1, float ratio)
    {
        // F0を補間
        float f0_0 = Llsm.GetFrameF0(frame0);
        float f0_1 = Llsm.GetFrameF0(frame1);
        
        // 無声音（F0=0 または極端に低い）の処理
        const float minVoicedF0 = 50.0f;  // 50Hz未満は無声音扱い
        bool voiced0 = f0_0 >= minVoicedF0;
        bool voiced1 = f0_1 >= minVoicedF0;
        
        // 両方とも無声音の場合
        if (!voiced0 && !voiced1)
        {
            return Llsm.CopyFrame(frame0);
        }
        
        // 有声音と無声音をまたぐ場合は、有声音側をコピー（フェード付き）
        if (!voiced0 && voiced1)
        {
            var frame = Llsm.CopyFrame(frame1);
            // 無声音から有声音へのフェードイン（ratioが小さいと減衰）
            ApplyVoicedFade(frame, ratio);
            return frame;
        }
        if (voiced0 && !voiced1)
        {
            var frame = Llsm.CopyFrame(frame0);
            // 有声音から無声音へのフェードアウト（ratioが大きいと減衰）
            ApplyVoicedFade(frame, 1.0f - ratio);
            return frame;
        }
        
        // 両方とも有声音の場合: Layer1パラメータを補間
        
        // F0を補間（最小値50Hzを保証）
        float f0_interp = f0_0 * (1 - ratio) + f0_1 * ratio;
        f0_interp = Math.Max(minVoicedF0, f0_interp);
        
        // RDを取得・補間
        var rd0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_RD);
        var rd1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_RD);
        
        float rd0 = rd0Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd0Ptr) : 1.0f;
        float rd1 = rd1Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd1Ptr) : 1.0f;
        float rd_interp = rd0 * (1 - ratio) + rd1 * ratio;
        
        // VTMAGN（声道スペクトル）を取得・補間
        var vtmagn0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        var vtmagn1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        
        float[] vtmagn_interp = null;
        if (vtmagn0Ptr != IntPtr.Zero && vtmagn1Ptr != IntPtr.Zero)
        {
            int nspec0 = NativeLLSM.llsm_fparray_length(vtmagn0Ptr);
            int nspec1 = NativeLLSM.llsm_fparray_length(vtmagn1Ptr);
            int nspec = Math.Min(nspec0, nspec1);
            
            if (nspec > 0)
            {
                float[] vtmagn0 = new float[nspec];
                float[] vtmagn1 = new float[nspec];
                Marshal.Copy(vtmagn0Ptr, vtmagn0, 0, nspec);
                Marshal.Copy(vtmagn1Ptr, vtmagn1, 0, nspec);
                
                vtmagn_interp = new float[nspec];
                for (int i = 0; i < nspec; i++)
                {
                    vtmagn_interp[i] = vtmagn0[i] * (1 - ratio) + vtmagn1[i] * ratio;
                    // NaN/Inf チェック
                    if (float.IsNaN(vtmagn_interp[i]) || float.IsInfinity(vtmagn_interp[i]))
                    {
                        vtmagn_interp[i] = Math.Max(vtmagn0[i], vtmagn1[i]);
                    }
                    // -80dB以下にはしない（demo-stretch.cと同じ）
                    vtmagn_interp[i] = Math.Max(-80.0f, vtmagn_interp[i]);
                }
            }
        }
        
        // VSPHSE（声帯位相）を取得・円形補間
        var vsphse0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
        var vsphse1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
        
        float[] vsphse_interp = null;
        if (vsphse0Ptr != IntPtr.Zero && vsphse1Ptr != IntPtr.Zero)
        {
            int nhar0 = NativeLLSM.llsm_fparray_length(vsphse0Ptr);
            int nhar1 = NativeLLSM.llsm_fparray_length(vsphse1Ptr);
            int minnhar = Math.Min(nhar0, nhar1);
            int maxnhar = Math.Max(nhar0, nhar1);
            
            if (maxnhar > 0)
            {
                float[] vsphse0 = new float[nhar0];
                float[] vsphse1 = new float[nhar1];
                Marshal.Copy(vsphse0Ptr, vsphse0, 0, nhar0);
                Marshal.Copy(vsphse1Ptr, vsphse1, 0, nhar1);
                
                vsphse_interp = new float[maxnhar];
                // 円形補間を使用（demo-stretch.cと同じ、位相の滑らかな遷移を保証）
                for (int i = 0; i < minnhar; i++)
                {
                    vsphse_interp[i] = CircularInterpolatePhase(vsphse0[i], vsphse1[i], ratio);
                }
                // 長い方の余分な部分はそのままコピー
                if (nhar0 < nhar1)
                {
                    for (int i = minnhar; i < maxnhar; i++)
                        vsphse_interp[i] = vsphse1[i];
                }
            }
        }
        
        // NMフレーム（ノイズモデル）も補間
        var nm0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_NM);
        var nm1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_NM);
        
        IntPtr nmInterpPtr = IntPtr.Zero;
        if (nm0Ptr != IntPtr.Zero && nm1Ptr != IntPtr.Zero)
        {
            var nm0 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm0Ptr);
            var nm1 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm1Ptr);
            
            if (nm0.npsd == nm1.npsd && nm0.npsd > 0)
            {
                // PSDを補間
                float[] psd0 = new float[nm0.npsd];
                float[] psd1 = new float[nm1.npsd];
                Marshal.Copy(nm0.psd, psd0, 0, nm0.npsd);
                Marshal.Copy(nm1.psd, psd1, 0, nm1.npsd);
                
                float[] psd_interp = new float[nm0.npsd];
                for (int p = 0; p < nm0.npsd; p++)
                {
                    psd_interp[p] = psd0[p] * (1 - ratio) + psd1[p] * ratio;
                }
                
                // 新しいNMフレームを作成
                nmInterpPtr = NativeLLSM.llsm_create_nmframe(nm0.nchannel, 0, nm0.npsd);
                var nmInterp = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmInterpPtr);
                Marshal.Copy(psd_interp, 0, nmInterp.psd, nm0.npsd);
                
                // edcも補間
                if (nm0.edc != IntPtr.Zero && nm1.edc != IntPtr.Zero && nm0.nchannel > 0)
                {
                    float[] edc0 = new float[nm0.nchannel];
                    float[] edc1 = new float[nm1.nchannel];
                    Marshal.Copy(nm0.edc, edc0, 0, nm0.nchannel);
                    Marshal.Copy(nm1.edc, edc1, 0, nm1.nchannel);
                    
                    float[] edc_interp = new float[nm0.nchannel];
                    for (int c = 0; c < nm0.nchannel; c++)
                    {
                        edc_interp[c] = edc0[c] * (1 - ratio) + edc1[c] * ratio;
                    }
                    Marshal.Copy(edc_interp, 0, nmInterp.edc, nm0.nchannel);
                }
            }
        }
        
        // 新しいLayer1フレームコンテナを作成
        var frameInterpPtr = NativeLLSM.llsm_create_container(13);
        
        // F0を設定
        var f0Ptr = NativeLLSM.llsm_create_fp(f0_interp);
        NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_F0,
            f0Ptr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
        
        // RDを設定
        var rdPtr = NativeLLSM.llsm_create_fp(rd_interp);
        NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_RD,
            rdPtr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
        
        // VTMAGNを設定
        if (vtmagn_interp != null)
        {
            var vtmagnPtr = NativeLLSM.llsm_create_fparray(vtmagn_interp.Length);
            Marshal.Copy(vtmagn_interp, 0, vtmagnPtr, vtmagn_interp.Length);
            NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_VTMAGN,
                vtmagnPtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
        }
        
        // VSPHSEを設定
        if (vsphse_interp != null)
        {
            var vsphsePtr = NativeLLSM.llsm_create_fparray(vsphse_interp.Length);
            Marshal.Copy(vsphse_interp, 0, vsphsePtr, vsphse_interp.Length);
            NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_VSPHSE,
                vsphsePtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
        }
        
        // NMを設定
        if (nmInterpPtr != IntPtr.Zero)
        {
            NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_NM,
                nmInterpPtr, Marshal.GetFunctionPointerForDelegate(_deleteNm), IntPtr.Zero);
        }
        
        return frameInterpPtr;
    }
    
    /// <summary>
    /// 円形補間: 2つの角度（ラジアン）を補間
    /// -π ~ +π の範囲で連続性を保つ
    /// </summary>
    static float CircularInterp(float a, float b, float ratio)
    {
        // 角度を単位円上のベクトルに変換
        float ax = MathF.Cos(a);
        float ay = MathF.Sin(a);
        float bx = MathF.Cos(b);
        float by = MathF.Sin(b);
        
        // ベクトルを線形補間
        float cx = ax * (1 - ratio) + bx * ratio;
        float cy = ay * (1 - ratio) + by * ratio;
        
        // ベクトルから角度に戻す
        return MathF.Atan2(cy, cx);
    }
    
    /// <summary>
    /// Catmull-Romスプライン補間：4つの値からキュービック補間
    /// </summary>
    static float CubicInterp(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        
        // Catmull-Romスプライン式
        return 0.5f * (
            2f * p1 +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
    
    /// <summary>
    /// M+用：ノート境界でmodulationを動的にフェード（クロスフェード領域でのピッチずれ防止）
    /// </summary>
    static float GetDynamicModulation(int frameIndex, int totalFrames, int baseModulation, float thop, float overlapMs = 0)
    {
        if (baseModulation == 0) return 0;  // mod=0なら何もしない
        
        // フェード時間: overlapMsが指定されていればそれを使用、なければデフォルト80ms
        float fadeTimeMs = overlapMs > 0 ? overlapMs : 80.0f;
        float fadeDurationFrames = fadeTimeMs / (thop * 1000.0f);
        
        float fadeInRatio = 1.0f;
        float fadeOutRatio = 1.0f;
        
        // フェードイン（開始部：0% → 100%）
        if (frameIndex < fadeDurationFrames)
        {
            fadeInRatio = frameIndex / fadeDurationFrames;
        }
        
        // フェードアウト（終了部：100% → 0%）
        if (frameIndex >= totalFrames - fadeDurationFrames)
        {
            fadeOutRatio = (totalFrames - 1 - frameIndex) / fadeDurationFrames;
        }
        
        // 両方のフェードを適用（最小値を使用）
        float fadeRatio = Math.Min(fadeInRatio, fadeOutRatio);
        fadeRatio = Math.Max(0.0f, Math.Min(1.0f, fadeRatio));  // 0-1にクリップ
        
        float result = baseModulation * fadeRatio;
        
        // デバッグ：最初と最後、そしてフェード領域でログ出力
        if (frameIndex < fadeDurationFrames || frameIndex > totalFrames - fadeDurationFrames || frameIndex == 0 || frameIndex == totalFrames - 1)
        {
            // 初回のみフレーム情報を出力（ログ過多防止）
            if (frameIndex == 0)
            {
                Console.WriteLine($"    [M+] Fade duration: {fadeDurationFrames:F1} frames ({fadeTimeMs}ms), totalFrames={totalFrames}");
            }
            if (frameIndex == 0 || frameIndex == totalFrames - 1 || frameIndex == (int)fadeDurationFrames || frameIndex == totalFrames - (int)fadeDurationFrames)
            {
                Console.WriteLine($"    [M+] Frame {frameIndex}: fadeRatio={fadeRatio:F3}, modulation={result:F1}% (in={fadeInRatio:F3}, out={fadeOutRatio:F3})");
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// 配列を周波数軸上でリサンプリング（3次エルミート補間）
    /// スペクトルの細部を保持し、倍音構造を損なわない高品質補間
    /// </summary>
    static float[] ResampleArray(float[] source, float ratio)
    {
        var result = new float[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            float srcIdx = i / ratio;
            
            if (srcIdx <= 0)
                result[i] = source[0];
            else if (srcIdx >= source.Length - 1)
                result[i] = source[source.Length - 1];
            else
            {
                // 3次エルミート補間（Catmull-Rom spline）
                // 4点を使用してスムーズな曲線補間
                int idx1 = (int)srcIdx;
                int idx0 = Math.Max(0, idx1 - 1);
                int idx2 = Math.Min(source.Length - 1, idx1 + 1);
                int idx3 = Math.Min(source.Length - 1, idx1 + 2);
                
                float t = srcIdx - idx1;
                float t2 = t * t;
                float t3 = t2 * t;
                
                float v0 = source[idx0];
                float v1 = source[idx1];
                float v2 = source[idx2];
                float v3 = source[idx3];
                
                // Catmull-Rom係数
                result[i] = 0.5f * (
                    (2 * v1) +
                    (-v0 + v2) * t +
                    (2*v0 - 5*v1 + 4*v2 - v3) * t2 +
                    (-v0 + 3*v1 - 3*v2 + v3) * t3
                );
            }
        }
        return result;
    }
    
    /// <summary>
    /// 有声/無声音遷移時のフェード処理（VTMAGNに適用）
    /// demo-stretch.cのmag2db(ratio)に相当
    /// </summary>
    static void ApplyVoicedFade(IntPtr frame, float fadeRatio)
    {
        var vtmagnPtr = NativeLLSM.llsm_container_get(frame, NativeLLSM.LLSM_FRAME_VTMAGN);
        if (vtmagnPtr == IntPtr.Zero) return;
        
        int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
        if (nspec <= 0) return;
        
        // fadeRatioをdB値に変換（ratioが小さいほど減衰）
        float fadeDb = 20.0f * MathF.Log10(MathF.Max(0.001f, fadeRatio));
        
        float[] vtmagn = new float[nspec];
        Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
        
        for (int i = 0; i < nspec; i++)
        {
            vtmagn[i] += fadeDb;
            vtmagn[i] = MathF.Max(-80.0f, vtmagn[i]);
        }
        
        Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
    }
    
    /// <summary>
    /// フレーム間位相スムージング: 隣接フレーム間の位相ジャンプを検出・修正
    /// タイムストレッチ時の非整数補間で発生する位相不連続（びりびり音の原因）を削減
    /// </summary>
    static void SmoothInterFramePhase(ChunkHandle chunk, int nfrm, float[] f0Array)
    {
        if (nfrm < 2) return;
        
        const float phaseJumpThreshold = MathF.PI;  // π以上のジャンプを検出
        const float smoothingStrength = 0.3f;  // スムージング強度（0.0～1.0）
        
        for (int i = 1; i < nfrm; i++)
        {
            if (f0Array[i] <= 0 || f0Array[i - 1] <= 0) continue;  // 無声音はスキップ
            
            var prevFrame = Llsm.GetFrame(chunk, i - 1);
            var currFrame = Llsm.GetFrame(chunk, i);
            
            // 両フレームのhmframeを取得
            var prevHmPtr = NativeLLSM.llsm_container_get(prevFrame.Ptr, NativeLLSM.LLSM_FRAME_HM);
            var currHmPtr = NativeLLSM.llsm_container_get(currFrame.Ptr, NativeLLSM.LLSM_FRAME_HM);
            
            if (prevHmPtr == IntPtr.Zero || currHmPtr == IntPtr.Zero) continue;
            
            var prevHm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(prevHmPtr);
            var currHm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(currHmPtr);
            
            if (prevHm.nhar <= 0 || currHm.nhar <= 0) continue;
            
            // 低次調波（1～3次）の位相差をチェック
            int maxHarmonicsToCheck = Math.Min(3, Math.Min(prevHm.nhar, currHm.nhar));
            float[] prevPhases = new float[maxHarmonicsToCheck];
            float[] currPhases = new float[maxHarmonicsToCheck];
            
            Marshal.Copy(prevHm.phse, prevPhases, 0, maxHarmonicsToCheck);
            Marshal.Copy(currHm.phse, currPhases, 0, maxHarmonicsToCheck);
            
            // 第1調波の位相差を計算（-π～+π範囲に正規化）
            float phaseDiff = currPhases[0] - prevPhases[0];
            phaseDiff = ((phaseDiff + MathF.PI) % (2 * MathF.PI)) - MathF.PI;
            
            // 大きな位相ジャンプを検出
            if (MathF.Abs(phaseDiff) > phaseJumpThreshold)
            {
                // スムージング: 位相差を緩和する補正を適用
                float correction = phaseDiff * smoothingStrength;
                
                // 現在フレームの全調波に補正を適用
                float[] allCurrPhases = new float[currHm.nhar];
                Marshal.Copy(currHm.phse, allCurrPhases, 0, currHm.nhar);
                
                for (int h = 0; h < currHm.nhar; h++)
                {
                    allCurrPhases[h] -= correction;
                }
                
                Marshal.Copy(allCurrPhases, 0, currHm.phse, currHm.nhar);
            }
        }
    }
    
    /// <summary>
    /// 息成分（breathiness）をフレームのノイズモデル（NM）に適用
    /// UTAU仕様: 0=息なし（クリアな声）、50=標準、100=ささやき（息成分2倍）
    /// dB単位でスケーリングして自然な変化を実現
    /// </summary>
    static void ApplyBreathiness(ContainerRef frame, int breathiness)
    {
        if (breathiness == 50) return;  // 標準値（デフォルト）ならスキップ
        
        // breathinessは声成分を減らし、ノイズ成分を増やす処理
        // B0 = 声100%・息0%、B50 = 標準、B100 = 声0%・息100%
        
        // breathinessから変化量を計算
        float ratio = (breathiness - 50) / 50.0f;  // -1.0 ~ +1.0
        
        // 1. 声成分（VTMAGN）を減衰
        // B100で-30dB、B0で+6dB
        float voiceAttenuation = ratio * 30.0f;  // B100で-30dB
        if (ratio < 0)  // B0側は控えめに強調
        {
            voiceAttenuation = ratio * 6.0f;
        }
        
        var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        if (vtmagnPtr != IntPtr.Zero)
        {
            int vtmagnSize = NativeLLSM.llsm_fparray_length(vtmagnPtr);
            if (vtmagnSize > 0)
            {
                float[] magnitudes = new float[vtmagnSize];
                Marshal.Copy(vtmagnPtr, magnitudes, 0, vtmagnSize);
                
                for (int i = 0; i < vtmagnSize; i++)
                {
                    magnitudes[i] -= voiceAttenuation;
                    magnitudes[i] = Math.Max(-80f, magnitudes[i]);  // 下限-80dB
                }
                
                Marshal.Copy(magnitudes, 0, vtmagnPtr, vtmagnSize);
            }
        }
        
        // 2. ノイズ成分（NM）を増幅
        // B100で+12dB、B0で-6dB
        float noiseGain = ratio * 12.0f;  // B100で+12dB
        if (ratio < 0)  // B0側は控えめに減衰
        {
            noiseGain = ratio * 6.0f;
        }
        
        var nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
        if (nmPtr != IntPtr.Zero)
        {
            var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
            
            // PSD（パワースペクトル密度）を増幅
            if (nm.psd != IntPtr.Zero && nm.npsd > 0)
            {
                float[] psd = new float[nm.npsd];
                Marshal.Copy(nm.psd, psd, 0, nm.npsd);
                
                for (int i = 0; i < nm.npsd; i++)
                {
                    psd[i] += noiseGain;
                    psd[i] = Math.Max(-120f, psd[i]);  // 下限-120dB
                }
                
                Marshal.Copy(psd, 0, nm.psd, nm.npsd);
            }
            
            // EDC（エネルギー分布）を線形スケール
            if (nm.edc != IntPtr.Zero && nm.nchannel > 0)
            {
                float linearScale = MathF.Pow(10, noiseGain / 20.0f);
                float[] edc = new float[nm.nchannel];
                Marshal.Copy(nm.edc, edc, 0, nm.nchannel);
                
                for (int i = 0; i < nm.nchannel; i++)
                {
                    edc[i] = Math.Max(0f, edc[i] * linearScale);
                }
                
                Marshal.Copy(edc, 0, nm.edc, nm.nchannel);
            }
        }
    }
    
    /// <summary>
    /// ジェンダーファクター（gフラグ）をフレームのVTMAGN（フォルマント）に適用
    /// genderFactor: 0=変化なし、正の値=男性的（フォルマント周波数低下）、負の値=女性的（フォルマント周波数上昇）
    /// Layer1でfparrayとして処理（補間後のフレームに適用）
    /// </summary>
    static void ApplyGenderFactor(ContainerRef frame, int genderFactor)
    {
        if (genderFactor == 0) return;
        
        var f0 = Llsm.GetFrameF0(frame);
        if (f0 <= 0) return;  // 無声音ではフォルマントシフトしない
        
        var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        if (vtmagnPtr == IntPtr.Zero) return;
        
        // genderFactorを周波数シフト率に変換（符号を反転）
        // より強い効果: g+100 → 約0.84倍（-3セミトーン、男性的）、g-100 → 約1.19倍（+3セミトーン、女性的）
        float shiftRatio = MathF.Pow(2.0f, -genderFactor / 400.0f);
        
        // Layer1のVTMAGNはfparray（スペクトル包絡）として処理
        int vtmagnSize = NativeLLSM.llsm_fparray_length(vtmagnPtr);
        if (vtmagnSize <= 0) return;
        
        float[] vtmagn = new float[vtmagnSize];
        Marshal.Copy(vtmagnPtr, vtmagn, 0, vtmagnSize);
        
        // 周波数軸リサンプリング（Catmull-Rom補間で高品質）
        var shiftedVtmagn = ResampleArray(vtmagn, shiftRatio);
        
        // 結果を書き戻す
        Marshal.Copy(shiftedVtmagn, 0, vtmagnPtr, vtmagnSize);
    }
    
    /// <summary>
    /// fparrayフレーム（補間後のdstChunk）に対して適応的フォルマント処理を適用
    /// </summary>
    static void ApplyAdaptiveFormantToFparray(ContainerRef frame, float pitchRatio, int formantFollow)
    {
        if (formantFollow == 100) return;  // 100%追従 = デフォルト動作、処理不要
        if (Math.Abs(pitchRatio - 1.0f) < 0.05f) return;  // 音程変化が小さい場合は処理不要
        
        var f0 = Llsm.GetFrameF0(frame);
        if (f0 <= 0) return;  // 無声音では処理しない
        
        // フォルマント追従率を計算
        // Layer0変換時にLLSMが自動的にF0変化に応じてフォルマントを追従させる（pitchRatio倍）
        // 追従を抑制するには、事前に逆方向にシフトして相殺する
        // 
        // formantFollow=0 → 完全固定（1.0倍、声質保持）
        // formantFollow=50 → 50%追従（自然なバランス）
        // formantFollow=100 → 完全追従（処理スキップ、デフォルト動作）
        float followRatio = formantFollow / 100.0f;
        
        // 目標のフォルマント比率（1.0 = 元の周波数）
        // followRatio=0 → 1.0（固定）
        // followRatio=0.5 → 1.0 + (pitchRatio-1.0)*0.5（50%追従）
        // followRatio=1.0 → pitchRatio（完全追従、でもここには来ない）
        float targetFormantRatio = 1.0f + (pitchRatio - 1.0f) * followRatio;
        
        // Layer0変換後に目標比率になるよう、事前にシフト
        // Layer0適用後: formantShiftRatio * pitchRatio = targetFormantRatio
        float formantShiftRatio = targetFormantRatio / pitchRatio;
        
        // VTMAGNを取得（fparrayとして）
        var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        if (vtmagnPtr == IntPtr.Zero) return;
        
        // fparrayのサイズを取得
        int vtmagnSize = NativeLLSM.llsm_fparray_length(vtmagnPtr);
        if (vtmagnSize <= 0 || vtmagnSize > 10000) return;
        
        // 元のVTMAGNをコピー
        var newVtmagnPtr = NativeLLSM.llsm_copy_fparray(vtmagnPtr);
        if (newVtmagnPtr == IntPtr.Zero) return;
        
        // データを読み取る（fparrayはfloat*として直接アクセス）
        float[] magnitudes = new float[vtmagnSize];
        Marshal.Copy(newVtmagnPtr, magnitudes, 0, vtmagnSize);
        
        // 周波数軸リサンプリング（共通処理）
        var shiftedMagnitudes = ResampleArray(magnitudes, formantShiftRatio);
        Marshal.Copy(shiftedMagnitudes, 0, newVtmagnPtr, vtmagnSize);
        
        // 元のVTMAGNを新しいものと置き換える
        NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN,
            newVtmagnPtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), IntPtr.Zero);
    }
    
    /// <summary>
    /// ノート名を Hz に変換 (例: "C4" -> 261.63)
    /// </summary>
    static float NoteNameToHz(string noteName)
    {
        if (string.IsNullOrEmpty(noteName)) return 440f;
        
        // ノート名をパース
        int noteIndex = 0;
        int octave = 4;
        int i = 0;
        
        // ノート名部分
        char note = char.ToUpper(noteName[i++]);
        int[] noteOffsets = { 9, 11, 0, 2, 4, 5, 7 }; // A, B, C, D, E, F, G
        if (note >= 'A' && note <= 'G')
        {
            noteIndex = noteOffsets[note - 'A'];
        }
        
        // シャープ/フラット
        if (i < noteName.Length)
        {
            if (noteName[i] == '#') { noteIndex++; i++; }
            else if (noteName[i] == 'b') { noteIndex--; i++; }
        }
        
        // オクターブ
        if (i < noteName.Length && int.TryParse(noteName.Substring(i), out var oct))
        {
            octave = oct;
        }
        
        // A4 = 440Hz を基準に計算
        int midiNote = 12 * (octave + 1) + noteIndex;
        return 440f * (float)Math.Pow(2, (midiNote - 69) / 12.0);
    }
    
    /// <summary>
    /// 子音/母音境界を検出（F0 + スペクトル傾斜 + エネルギー変化）
    /// 複数の音響特徴を組み合わせて高精度に境界を推定
    /// F0ベースで無声→有声境界を検出
    /// 無声子音（F0=0）から有声音（F0>0）に変わる最初のフレームを探す
    /// </summary>
    static int DetectConsonantBoundary(ChunkHandle chunk, int nfrm)
    {
        if (nfrm < 5) return 0;
        
        // 各フレームのF0とVTMAGNエネルギーを取得
        float[] f0Array = new float[nfrm];
        float[] energyArray = new float[nfrm];
        float[] spectralSlopeArray = new float[nfrm];
        
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            f0Array[i] = Llsm.GetFrameF0(frame);
            
            // VTMAGNからエネルギーとスペクトル傾斜を計算
            var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnPtr != IntPtr.Zero)
            {
                int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                if (nspec > 0)
                {
                    float[] vtmagn = new float[nspec];
                    Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                    
                    // 平均エネルギー（dB平均）
                    energyArray[i] = vtmagn.Average();
                    
                    // スペクトル傾斜（高域/低域エネルギー比）
                    // 母音は低域が強く、子音は高域が強い傾向
                    int lowBand = nspec / 4;
                    int highBand = nspec * 3 / 4;
                    float lowEnergy = vtmagn.Take(lowBand).Average();
                    float highEnergy = vtmagn.Skip(highBand).Average();
                    spectralSlopeArray[i] = highEnergy - lowEnergy;  // 正の値=高域優勢（子音的）
                }
            }
        }
        
        // エネルギーとスペクトル傾斜を正規化（0-1範囲）
        float minEnergy = energyArray.Min();
        float maxEnergy = energyArray.Max();
        float energyRange = maxEnergy - minEnergy;
        if (energyRange > 1e-6f)
        {
            for (int i = 0; i < nfrm; i++)
                energyArray[i] = (energyArray[i] - minEnergy) / energyRange;
        }
        
        float minSlope = spectralSlopeArray.Min();
        float maxSlope = spectralSlopeArray.Max();
        float slopeRange = maxSlope - minSlope;
        if (slopeRange > 1e-6f)
        {
            for (int i = 0; i < nfrm; i++)
                spectralSlopeArray[i] = (spectralSlopeArray[i] - minSlope) / slopeRange;
        }
        
        // 統合スコア計算: F0有声 + 高エネルギー + 低スペクトル傾斜（低域優勢）= 母音的
        float[] vowelScore = new float[nfrm];
        for (int i = 0; i < nfrm; i++)
        {
            float f0Score = f0Array[i] > 0 ? 1.0f : 0.0f;
            float energyScore = energyArray[i];
            float slopeScore = 1.0f - spectralSlopeArray[i];  // 低域優勢ほど高スコア
            
            // 重み付け統合（F0を最重視）
            vowelScore[i] = 0.5f * f0Score + 0.3f * energyScore + 0.2f * slopeScore;
        }
        
        // 母音区間の開始を検出（3フレーム連続で高スコア）
        const int minVoicedRun = 3;
        const float vowelThreshold = 0.6f;  // 0.6以上で母音と判定
        
        int highScoreCount = 0;
        for (int i = 0; i < nfrm; i++)
        {
            if (vowelScore[i] >= vowelThreshold)
            {
                highScoreCount++;
                if (highScoreCount >= minVoicedRun)
                {
                    // 母音区間の開始位置を返す（子音の終わり）
                    return i - minVoicedRun + 1;
                }
            }
            else
            {
                highScoreCount = 0;
            }
        }
        
        // 全てが子音（または無声）の場合は0を返す
        return 0;
    }
    
    /// <summary>
    /// <summary>
    /// ピッチベンドを線形補間
    /// UTAUタイミング配列から出力タイミング配列に補間
    /// </summary>
    static float[] InterpolatePitchBend(float[] utauT, float[] outputT, float[] utauPb)
    {
        if (utauT.Length < 2 || outputT.Length == 0)
        {
            return new float[outputT.Length];
        }
        
        float[] result = new float[outputT.Length];
        float span = utauT[1] - utauT[0];
        
        for (int i = 0; i < outputT.Length; i++)
        {
            float t = outputT[i];
            
            // tがutauTのどの区間にあるかを計算
            int index = (int)((t - utauT[0]) / span);
            if (index < 0) index = 0;
            if (index >= utauT.Length - 1) index = utauT.Length - 2;
            
            if (Math.Abs(utauT[index] - t) < 1e-6f)
            {
                result[i] = utauPb[index];
            }
            else
            {
                // Catmull-Romスプライン補間（滑らかなピッチ遷移）
                float t0 = utauT[index];
                float t1 = utauT[index + 1];
                float localT = (t - t0) / (t1 - t0);  // 0～1の範囲に正規化
                
                // 4点を取得（境界では線形補間にフォールバック）
                if (index > 0 && index < utauT.Length - 2)
                {
                    // Catmull-Romスプライン（4点使用）
                    float p0 = utauPb[index - 1];  // 前の点
                    float p1 = utauPb[index];      // 開始点
                    float p2 = utauPb[index + 1];  // 終了点
                    float p3 = utauPb[index + 2];  // 次の点
                    
                    result[i] = CatmullRomInterpolate(p0, p1, p2, p3, localT);
                }
                else
                {
                    // 境界では線形補間
                    float v0 = utauPb[index];
                    float v1 = utauPb[index + 1];
                    result[i] = v0 + (v1 - v0) * localT;
                }
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Catmull-Romスプライン補間（1次元）
    /// </summary>
    static float CatmullRomInterpolate(float p0, float p1, float p2, float p3, float t)
    {
        // Catmull-Romスプライン公式
        // q(t) = 0.5 * [(2*p1) + (-p0 + p2)*t + (2*p0 - 5*p1 + 4*p2 - p3)*t^2 + (-p0 + 3*p1 - 3*p2 + p3)*t^3]
        float t2 = t * t;
        float t3 = t2 * t;
        
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
    
    /// <summary>
    /// ピッチベンドのラップアラウンドを補正
    /// 4096 の境界を跨ぐ急激な変化を検出して連続値に変換
    /// </summary>
    static List<int> UnwrapPitchBend(List<int> pitchBend)
    {
        if (pitchBend.Count == 0) return pitchBend;
        
        var result = new List<int>(pitchBend.Count);
        result.Add(pitchBend[0]);
        
        int offset = 0;
        int wrapCount = 0;
        for (int i = 1; i < pitchBend.Count; i++)
        {
            int diff = pitchBend[i] - pitchBend[i - 1];
            
            // 急激な変化（2048以上）はラップアラウンドと判断
            if (diff > 2048)
            {
                // 上にジャンプ: 例 4 → 4095 は実際は 4 → -1 (4096を引く)
                offset -= 4096;
                wrapCount++;
            }
            else if (diff < -2048)
            {
                // 下にジャンプ: 例 4095 → 5 は実際は 4095 → 4101 (4096を足す)
                offset += 4096;
                wrapCount++;
            }
            
            result.Add(pitchBend[i] + offset);
        }
        
        if (wrapCount > 0)
        {
            Console.WriteLine($"    [Unwrap] Detected {wrapCount} wraps, final offset: {offset}");
        }
        
        return result;
    }
    
    /// <summary>
    /// UTAU のピッチベンド文字列をパース
    /// 形式: !数字,Base64文字列#リピート数#Base64文字列...
    /// 戻り値: (tempo, pitchBendList)
    /// </summary>
    static (int tempo, List<int> pitchBend) ParsePitchBendWithTempo(string[] args, int startIndex)
    {
        var result = new List<int>();
        int tempo = 120;  // デフォルトテンポ
        
        for (int i = startIndex; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.IsNullOrEmpty(arg)) continue;
            
            // !数字 はテンポ
            if (arg.StartsWith("!"))
            {
                if (int.TryParse(arg.Substring(1), out int t))
                {
                    tempo = t;
                }
                continue;
            }
            
            // #数字# はリピート回数
            // 例: "7g7b7X#62#7X7c" → 7g7b7X を62回リピート、その後 7X7c
            result.AddRange(DecodeUtauPitchBendWithRepeat(arg));
        }
        
        return (tempo, result);
    }
    
    /// <summary>
    /// UTAU のピッチベンド文字列をパース（互換用）
    /// 形式: !数字,Base64文字列#リピート数#Base64文字列...
    /// </summary>
    static List<int> ParsePitchBend(string[] args, int startIndex)
    {
        return ParsePitchBendWithTempo(args, startIndex).pitchBend;
    }
    
    /// <summary>
    /// リピート記号付きのピッチベンドをデコード
    /// </summary>
    static List<int> DecodeUtauPitchBendWithRepeat(string input)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(input)) return result;
        
        int pos = 0;
        int lastValue = 0;
        
        while (pos < input.Length)
        {
            // #数字# を探す
            int hashStart = input.IndexOf('#', pos);
            
            if (hashStart < 0)
            {
                // 残りは通常のBase64
                var decoded = DecodeUtauPitchBend(input.Substring(pos));
                result.AddRange(decoded);
                if (decoded.Count > 0) lastValue = decoded[decoded.Count - 1];
                break;
            }
            
            // # の前の部分をデコード
            if (hashStart > pos)
            {
                var decoded = DecodeUtauPitchBend(input.Substring(pos, hashStart - pos));
                result.AddRange(decoded);
                if (decoded.Count > 0) lastValue = decoded[decoded.Count - 1];
            }
            
            // #数字# を探す
            int hashEnd = input.IndexOf('#', hashStart + 1);
            if (hashEnd < 0)
            {
                // 閉じ # がない、残りをデコード
                var decoded = DecodeUtauPitchBend(input.Substring(hashStart + 1));
                result.AddRange(decoded);
                break;
            }
            
            // リピート回数を取得
            string repeatStr = input.Substring(hashStart + 1, hashEnd - hashStart - 1);
            if (int.TryParse(repeatStr, out int repeatCount))
            {
                // 直前の値をリピート
                for (int i = 0; i < repeatCount; i++)
                {
                    result.Add(lastValue);
                }
            }
            
            pos = hashEnd + 1;
        }
        
        return result;
    }
    
    /// <summary>
    /// UTAU のピッチベンド (Base64) をデコード（リピートなし）
    /// 戻り値は -2048 to +2047 の範囲（符号付き）、0 が中心（0 cent）
    /// </summary>
    static List<int> DecodeUtauPitchBend(string base64)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(base64)) return result;
        
        // UTAU 独自の Base64 エンコーディング
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        
        for (int i = 0; i < base64.Length; i += 2)
        {
            if (i + 1 >= base64.Length) break;
            
            int idx1 = chars.IndexOf(base64[i]);
            int idx2 = chars.IndexOf(base64[i + 1]);
            
            if (idx1 < 0 || idx2 < 0) continue;
            
            // 12ビット値として計算
            int value = idx1 * 64 + idx2;
            // 符号付きに変換: 2048以上は負の値
            if (value >= 2048) value -= 4096;
            result.Add(value);
        }
        
        return result;
    }
    
    /// <summary>
    /// デモモード
    /// </summary>
    static void RunDemo()
    {
        string voicebankPath = @"G:\libllsm2\闇音レンリ・連続音Ver1.5";
        string pitchFolder = "N_D4";  // D4 = 293.66 Hz
        
        Console.WriteLine("=== LLSM UTAU Engine Prototype ===");
        Console.WriteLine($"Voicebank: {voicebankPath}");
        
        // oto.ini を読み込み
        string otoPath = Path.Combine(voicebankPath, pitchFolder, "oto.ini");
        var otoEntries = ParseOtoIni(otoPath);
        Console.WriteLine($"Loaded {otoEntries.Count} oto entries");
        
        // 「あ」の単独音を探す（連続音の場合は「- あ」）
        var targetEntry = otoEntries.FirstOrDefault(e => 
            e.Alias.Contains("あ") && e.Alias.Contains("-")) 
            ?? otoEntries.FirstOrDefault(e => e.Alias.Contains("あ"));
        
        if (targetEntry == null)
        {
            Console.WriteLine("Could not find 'あ' entry");
            // 最初のエントリを使う
            targetEntry = otoEntries.First();
        }
        
        Console.WriteLine($"\nUsing entry: {targetEntry.Alias}");
        Console.WriteLine($"  File: {targetEntry.FileName}");
        Console.WriteLine($"  Offset: {targetEntry.Offset}ms, Consonant: {targetEntry.Consonant}ms");
        Console.WriteLine($"  Cutoff: {targetEntry.Cutoff}ms, Preutterance: {targetEntry.Preutterance}ms");
        Console.WriteLine($"  Overlap: {targetEntry.Overlap}ms");
        
        // wav を読み込み
        string wavPath = Path.Combine(voicebankPath, pitchFolder, targetEntry.FileName);
        Console.WriteLine($"\nLoading: {wavPath}");
        
        var (samples, wavFs) = Wav.ReadMono(wavPath);
        Console.WriteLine($"  Samples: {samples.Length}, SampleRate: {wavFs}");
        
        // oto パラメータをサンプル数に変換
        int offsetSamples = (int)(targetEntry.Offset / 1000.0 * wavFs);
        int consonantSamples = (int)(targetEntry.Consonant / 1000.0 * wavFs);
        
        // Cutoff の処理（負の値は末尾からのオフセット）
        int endSamples;
        if (targetEntry.Cutoff < 0)
        {
            endSamples = samples.Length + (int)(targetEntry.Cutoff / 1000.0 * wavFs);
        }
        else
        {
            endSamples = (int)(targetEntry.Cutoff / 1000.0 * wavFs);
        }
        
        // 使用範囲を抽出
        int startSample = offsetSamples;
        int length = endSamples - startSample;
        if (length <= 0 || startSample >= samples.Length)
        {
            Console.WriteLine("Invalid oto parameters, using full sample");
            startSample = 0;
            length = samples.Length;
        }
        
        float[] segment = new float[length];
        Array.Copy(samples, startSample, segment, 0, Math.Min(length, samples.Length - startSample));
        
        Console.WriteLine($"  Using segment: {startSample} to {startSample + length} ({length} samples, {length / (float)wavFs * 1000:F1}ms)");
        
        // PYIN で F0 を推定
        Console.WriteLine("\nAnalyzing with PYIN...");
        int nhop = (int)(Thop * wavFs);
        float[] f0 = Pyin.Analyze(segment, wavFs, nhop, 60, 800);
        
        // 原音の平均 F0 を計算
        var voicedF0 = f0.Where(x => x > 0).ToArray();
        float avgF0 = voicedF0.Length > 0 ? voicedF0.Average() : 293.66f;  // D4
        Console.WriteLine($"  Average F0: {avgF0:F1} Hz (frames: {f0.Length}, voiced: {voicedF0.Length})");
        
        // LLSM で解析
        Console.WriteLine("\nAnalyzing with LLSM...");
        using var aopt = Llsm.CreateAnalysisOptions();
        using var masterChunk = Llsm.Analyze(aopt, segment, wavFs, f0, f0.Length);
        
        int nfrm = Llsm.GetNumFrames(masterChunk);
        Console.WriteLine($"  Frames: {nfrm}");
        
        // テスト: 元のピッチで再合成
        Console.WriteLine("\n=== Test 1: Original pitch resynthesis ===");
        {
            // チャンクをコピーしてから処理
            using var chunk = Llsm.CopyChunk(masterChunk);
            var original = SynthesizeChunk(chunk, wavFs);
            Wav.WriteMono16("utau_original.wav", original, wavFs);
            Console.WriteLine($"Wrote utau_original.wav ({original.Length} samples)");
        }
        
        // テスト: C4 (261.63 Hz) にピッチシフト
        Console.WriteLine("\n=== Test 2: Pitch shift to C4 ===");
        float targetF0_C4 = 261.63f;
        {
            using var chunk = Llsm.CopyChunk(masterChunk);
            var pitched_C4 = SynthesizeWithPitch(chunk, wavFs, avgF0, targetF0_C4);
            Wav.WriteMono16("utau_C4.wav", pitched_C4, wavFs);
            Console.WriteLine($"Wrote utau_C4.wav");
        }
        
        // テスト: A4 (440 Hz) にピッチシフト
        Console.WriteLine("\n=== Test 3: Pitch shift to A4 ===");
        float targetF0_A4 = 440.0f;
        {
            using var chunk = Llsm.CopyChunk(masterChunk);
            var pitched_A4 = SynthesizeWithPitch(chunk, wavFs, avgF0, targetF0_A4);
            Wav.WriteMono16("utau_A4.wav", pitched_A4, wavFs);
            Console.WriteLine($"Wrote utau_A4.wav");
        }
        
        // テスト: タイムストレッチ (1.5倍)
        Console.WriteLine("\n=== Test 4: Time stretch 1.5x ===");
        {
            using var chunk = Llsm.CopyChunk(masterChunk);
            var stretched = SynthesizeWithStretch(chunk, wavFs, 1.5f);
            Wav.WriteMono16("utau_stretch.wav", stretched, wavFs);
            Console.WriteLine($"Wrote utau_stretch.wav ({stretched.Length} samples)");
        }
        
        // テスト: ピッチシフト + タイムストレッチ
        Console.WriteLine("\n=== Test 5: A4 + 1.5x stretch ===");
        {
            using var chunk = Llsm.CopyChunk(masterChunk);
            var combined = SynthesizeWithPitchAndStretch(chunk, wavFs, avgF0, 440.0f, 1.5f);
            Wav.WriteMono16("utau_A4_stretch.wav", combined, wavFs);
            Console.WriteLine($"Wrote utau_A4_stretch.wav");
        }
        
        // テスト: resampler モードテスト
        Console.WriteLine("\n=== Test 6: Resampler mode test ===");
        {
            // A4 で 500ms 出力をリクエスト
            string testInput = wavPath;
            string testOutput = "test_resampler_A4.wav";
            Resample(testInput, testOutput, 440f, 100, "", 
                     targetEntry.Offset, 500f, targetEntry.Consonant, 
                     targetEntry.Cutoff, 100, 0, new List<int>());
            Console.WriteLine($"Wrote {testOutput}");
        }
        
        // テスト: 子音部固定のストレッチ比較
        Console.WriteLine("\n=== Test 6b: Consonant-fixed stretch comparison ===");
        {
            // oto からの値: Offset=522ms, Consonant=107ms
            float consonantMs = targetEntry.Consonant;  // 107ms
            int consonantFrames = (int)(consonantMs / 1000.0 / Thop);
            Console.WriteLine($"  Consonant: {consonantMs}ms = {consonantFrames} frames");
            Console.WriteLine($"  Total frames: {nfrm}");
            
            // 子音部固定で 2.0x ストレッチ（比較用）
            using var chunkFixed = Llsm.CopyChunk(masterChunk);
            var fixedStretch = SynthesizeWithConsonantAndStretch(chunkFixed, wavFs, avgF0, avgF0, 
                                                                  consonantFrames, 1.0f, 2.0f, new List<int>(), 120);
            Wav.WriteMono16("utau_stretch_consonant_fixed.wav", fixedStretch, wavFs);
            Console.WriteLine($"  Fixed consonant stretch: {fixedStretch.Length} samples");
            
            // 比較: 通常の全体ストレッチ
            using var chunkFull = Llsm.CopyChunk(masterChunk);
            var fullStretch = SynthesizeWithStretch(chunkFull, wavFs, 2.0f);
            Wav.WriteMono16("utau_stretch_full.wav", fullStretch, wavFs);
            Console.WriteLine($"  Full stretch: {fullStretch.Length} samples");
        }
        
        // テスト: 実際の UTAU 引数をシミュレート
        Console.WriteLine("\n=== Test 7: Real UTAU args simulation ===");
        {
            // UTAUからの実際の引数（リピート記号付き）
            string realInput = @"G:\libllsm2\闇音レンリ・連続音Ver1.5\W_A3\_どんどどだどでぃど.wav";
            string realOutput = "test_utau_real.wav";
            
            // 実際の UTAU から渡されるピッチベンド文字列（#数字# がリピート）
            string[] pitchBendArgs = new[] {
                "!203",
                "7g7b7X7V7U#62#7X7c7k7u768I8X8m829G9U9i9u939/+E+G#2#+F+D+B9+97939z9v9q9m9h9c9W9R9M9H9C8+85818y8v8s8q8o8m#2#8n8r8y869F9S9f9v9++P+f+w+//O/b/m"
            };
            
            // 新しいパーサーでデコード
            var pitchBends = ParsePitchBend(pitchBendArgs, 0);
            
            Console.WriteLine($"  Input: {realInput}");
            Console.WriteLine($"  Pitch: B3 ({NoteNameToHz("B3"):F1} Hz)");
            Console.WriteLine($"  PitchBend points: {pitchBends.Count}");
            
            // ピッチベンドの内容を表示（最初と最後の数点）
            if (pitchBends.Count > 0)
            {
                var first5 = pitchBends.Take(5).Select(x => x.ToString());
                var last5 = pitchBends.Skip(Math.Max(0, pitchBends.Count - 5)).Select(x => x.ToString());
                Console.WriteLine($"  First 5: [{string.Join(", ", first5)}]");
                Console.WriteLine($"  Last 5: [{string.Join(", ", last5)}]");
            }
            
            if (File.Exists(realInput))
            {
                Resample(realInput, realOutput, NoteNameToHz("B3"), 100, "", 
                         1735.0f, 450f, 375.0f, -583.333f, 100, 0, pitchBends);
                Console.WriteLine($"  Wrote {realOutput}");
            }
            else
            {
                Console.WriteLine($"  Skipped: input file not found");
            }
        }
        
        // テスト: 実際の UTAU 引数形式のパースをシミュレート
        Console.WriteLine("\n=== Test 7b: Full UTAU args parse test ===");
        {
            // 実際に UTAU から渡される引数をシミュレート（新しいテストケース）
            // C_G4\_ぬんぬぬねぬのぬ.wav を A#4 で、1450ms 出力
            // oto.ini: u のCG4,642.255,375.0,-583.333,250.0,83.333
            // UTAU からの引数を元に戻す
            string[] simulatedArgs = new[] {
                @"G:\libllsm2\闇音レンリ・連続音Ver1.5\C_G4\_ぬんぬぬねぬのぬ.wav",
                "test_utau_full_args.wav",
                "A#4",
                "100",
                "",  // flags
                "3174.523",  // UTAU から渡された offset
                "1450",
                "375.0",
                "-583.333",
                "101",
                "0",
                "!203",
                "5w#35#5+6c7F7w8W8w84#23#83#5#82#2#818180808z8y8y9F9N9U9b9j9p9u91959/+C+G+J+K+M#2#+L+K+G+D9/969z9s9l9c9T9J8/818q8g8V8K7/707p7g7W7N7E68616u6o6j6f6c6Y6W6V#2#6X6Z6c6g6k6o6u60667B7H7P7W7d7m7v768F8Q8d8q849F9S9g9v98+K+X+k+w+7/F/Q/Y/g/o/u/z/3/6/8/9/8/6/4/0/v/q/k/f/a/W/S/P/M/K/I/H#2#/I/K/M/O/S/V/Z/e/j/o/t/z/5//AFALARAXAcAiAnAsAwA0A4A7A+BABCBD#3#BCBAA+A7A4A1AxAtAoAkAfAaAUAPAKAE///6/1/w/s/o/k/g/d/a/Y/W/V/U#2#/V/W/X/Z/b/e/h/l/o/s/w/1/5/+ACAHALAQAUAYAcAgAjAnApAsAuAwAxAy#3#AxAwAvAtArAoAlAiAfAbAYAUAQAMAIAEAA/9/5/2/y/v/s/q/n/l/k/i/h/h/g/h/h/i/j/k/m/n/q/s/v/x/0/3/6/9ABAEAHAKANAQATAVAXAaAbAdAfAgAhAhAiAiAhAhAgAfAeAcAbAZAXAVASAQAOALAJAGAEAB///8/6/4/2/0/z/x/w/v/u/u/t#3#/u/u/v/w/x/z/0/1/3/5/6/8/+AAACADAFAHAIAKALAMANAOAPAQAQAR#4#AQAQAPAPAOANAMALAKAJAIAGAFAEADACABAA///+/9/8/8/7/7/6#9#/7/7/8"
            };
            
            Console.WriteLine($"  Simulated args count: {simulatedArgs.Length}");
            Console.WriteLine($"  Input: {simulatedArgs[0]}");
            Console.WriteLine($"  Output: {simulatedArgs[1]}");
            Console.WriteLine($"  Pitch: {simulatedArgs[2]}");
            Console.WriteLine($"  Offset: {simulatedArgs[5]}ms");
            Console.WriteLine($"  LengthReq: {simulatedArgs[6]}ms");
            Console.WriteLine($"  Consonant: {simulatedArgs[7]}ms");
            Console.WriteLine($"  Cutoff: {simulatedArgs[8]}ms");
            
            // oto.ini から該当エントリを確認
            string otoDir = Path.GetDirectoryName(simulatedArgs[0]) ?? "";
            string otoFilePath = Path.Combine(otoDir, "oto.ini");
            if (File.Exists(otoFilePath))
            {
                var otoLines = File.ReadAllLines(otoFilePath, Encoding.GetEncoding("shift_jis"));
                var wavName = Path.GetFileName(simulatedArgs[0]);
                var matchingEntries = otoLines.Where(l => l.StartsWith(wavName)).Take(3).ToList();
                Console.WriteLine($"  OTO entries for {wavName}:");
                foreach (var entry in matchingEntries)
                {
                    Console.WriteLine($"    {entry}");
                }
            }
            
            // ピッチベンドをデバッグ出力
            var pbDebug = ParsePitchBend(simulatedArgs, 11);
            Console.WriteLine($"  PitchBend total points: {pbDebug.Count}");
            if (pbDebug.Count > 0)
            {
                // 元の 12ビット値の範囲
                Console.WriteLine($"  Original 12bit range: {pbDebug.Min()} to {pbDebug.Max()}");
                
                var rawValues = pbDebug.Select(x => x - 2048).ToArray();
                Console.WriteLine($"  Raw value range (from 2048): {rawValues.Min()} to {rawValues.Max()}");
                
                // 値の分布を見る
                var below2048 = pbDebug.Count(x => x < 2048);
                var above2048 = pbDebug.Count(x => x >= 2048);
                Console.WriteLine($"  Distribution: {below2048} below 2048, {above2048} at/above 2048");
                
                // 最初の50ポイントを見る
                var first50 = pbDebug.Take(50).Select(x => x.ToString()).ToArray();
                Console.WriteLine($"  First 50 raw: [{string.Join(",", first50)}]");
            }
            
            // RunAsResampler と同じ処理をシミュレート
            if (File.Exists(simulatedArgs[0]))
            {
                float targetF0 = NoteNameToHz(simulatedArgs[2]);
                int velocity = int.Parse(simulatedArgs[3]);
                float offset = float.Parse(simulatedArgs[5]);
                float lengthReq = float.Parse(simulatedArgs[6]);
                float consonant = float.Parse(simulatedArgs[7]);
                float cutoff = float.Parse(simulatedArgs[8]);
                int volume = int.Parse(simulatedArgs[9]);
                int modulation = int.Parse(simulatedArgs[10]);
                
                var pitchBends2 = ParsePitchBend(simulatedArgs, 11);
                Console.WriteLine($"  PitchBend points: {pitchBends2.Count}");
                
                // ピッチベンドありで合成
                Resample(simulatedArgs[0], simulatedArgs[1], targetF0, velocity, "",
                         offset, lengthReq, consonant, cutoff, volume, modulation, pitchBends2);
                Console.WriteLine($"  Wrote {simulatedArgs[1]}");
                
                // ピッチベンドなしで比較用に合成
                Resample(simulatedArgs[0], "test_utau_no_pitchbend.wav", targetF0, velocity, "",
                         offset, lengthReq, consonant, cutoff, volume, modulation, new List<int>());
                Console.WriteLine($"  Wrote test_utau_no_pitchbend.wav (no pitchbend for comparison)");
                
                // 既存エンジンの出力と比較
                string existingOutput = @"G:\libllsm2\csharp\samples\UtauEngine\u+の.wav";
                if (File.Exists(existingOutput))
                {
                    Console.WriteLine($"\n  === Comparing with existing engine output ===");
                    var (existingSamples, existingFs) = Wav.ReadMono(existingOutput);
                    var (llsmSamples, llsmFs) = Wav.ReadMono("test_utau_no_pitchbend.wav");
                    
                    Console.WriteLine($"  Existing: {existingSamples.Length} samples ({existingSamples.Length / (float)existingFs * 1000:F1}ms)");
                    Console.WriteLine($"  LLSM: {llsmSamples.Length} samples ({llsmSamples.Length / (float)llsmFs * 1000:F1}ms)");
                    
                    // 両方の F0 を PYIN で測定
                    int nhop2 = (int)(Thop * existingFs);
                    var existingF0 = Pyin.Analyze(existingSamples, existingFs, nhop2, 60, 800);
                    var llsmF0 = Pyin.Analyze(llsmSamples, llsmFs, nhop2, 60, 800);
                    
                    // ピッチベンドありの出力も測定
                    var (llsmPbSamples, _) = Wav.ReadMono("test_utau_full_args.wav");
                    var llsmPbF0 = Pyin.Analyze(llsmPbSamples, llsmFs, nhop2, 60, 800);
                    
                    var existingVoiced = existingF0.Where(x => x > 0).ToArray();
                    var llsmVoiced = llsmF0.Where(x => x > 0).ToArray();
                    var llsmPbVoiced = llsmPbF0.Where(x => x > 0).ToArray();
                    
                    if (existingVoiced.Length > 0)
                        Console.WriteLine($"  Existing F0: avg={existingVoiced.Average():F1}Hz, min={existingVoiced.Min():F1}, max={existingVoiced.Max():F1}");
                    if (llsmVoiced.Length > 0)
                        Console.WriteLine($"  LLSM(noPB) F0: avg={llsmVoiced.Average():F1}Hz, min={llsmVoiced.Min():F1}, max={llsmVoiced.Max():F1}");
                    if (llsmPbVoiced.Length > 0)
                        Console.WriteLine($"  LLSM+PB F0: avg={llsmPbVoiced.Average():F1}Hz, min={llsmPbVoiced.Min():F1}, max={llsmPbVoiced.Max():F1}");
                    Console.WriteLine($"  Target: {targetF0:F1}Hz (A#4)");
                    
                    // エネルギープロファイルを比較（50msウィンドウ）
                    Console.WriteLine($"\n  === Energy profile comparison (50ms windows) ===");
                    int windowSize = (int)(0.05f * existingFs);  // 50ms
                    Console.WriteLine($"  Time(ms) | Existing | LLSM");
                    for (int t = 0; t < 600; t += 50)  // 最初600msを見る
                    {
                        int sampleOffset = (int)(t / 1000.0 * existingFs);
                        
                        // Existing のエネルギー
                        double existingEnergy = 0;
                        int countE = 0;
                        for (int s = sampleOffset; s < Math.Min(sampleOffset + windowSize, existingSamples.Length); s++, countE++)
                            existingEnergy += existingSamples[s] * existingSamples[s];
                        if (countE > 0) existingEnergy = Math.Sqrt(existingEnergy / countE);
                        
                        // LLSM のエネルギー
                        double llsmEnergy = 0;
                        int countL = 0;
                        for (int s = sampleOffset; s < Math.Min(sampleOffset + windowSize, llsmPbSamples.Length); s++, countL++)
                            llsmEnergy += llsmPbSamples[s] * llsmPbSamples[s];
                        if (countL > 0) llsmEnergy = Math.Sqrt(llsmEnergy / countL);
                        
                        string existingBar = new string('█', (int)(existingEnergy * 50));
                        string llsmBar = new string('░', (int)(llsmEnergy * 50));
                        Console.WriteLine($"  {t,4}ms | {existingEnergy:F3} {existingBar,-20} | {llsmEnergy:F3} {llsmBar}");
                    }
                    Console.WriteLine($"  Note: Consonant region is 0-375ms in LLSM");
                }
            }
            else
            {
                Console.WriteLine($"  Skipped: input file not found");
            }
        }
        
        // テスト 7c: 子音部を短くして試す（Preutterance を考慮）
        Console.WriteLine("\n=== Test 7c: Shortened consonant (using preutterance) ===");
        {
            string inputWav = @"G:\libllsm2\闇音レンリ・連続音Ver1.5\C_G4\_ぬんぬぬねぬのぬ.wav";
            if (File.Exists(inputWav))
            {
                float targetF0 = NoteNameToHz("A#4");
                float offset = 3174.523f;
                float lengthReq = 1450f;
                float cutoff = -583.333f;
                float preutterance = 250.0f;  // oto.ini から
                float otoConsonant = 375.0f;  // oto.ini から
                
                // ピッチベンドを取得
                string[] testArgs = new[] {
                    "", "", "", "", "", "", "", "", "", "", "",
                    "!203",
                    "5w#35#5+6c7F7w8W8w84#23#83#5#82#2#818180808z8y8y9F9N9U9b9j9p9u91959/+C+G+J+K+M#2#+L+K+G+D9/969z9s9l9c9T9J8/818q8g8V8K7/707p7g7W7N7E68616u6o6j6f6c6Y6W6V#2#6X6Z6c6g6k6o6u60667B7H7P7W7d7m7v768F8Q8d8q849F9S9g9v98+K+X+k+w+7/F/Q/Y/g/o/u/z/3/6/8/9/8/6/4/0/v/q/k/f/a/W/S/P/M/K/I/H#2#/I/K/M/O/S/V/Z/e/j/o/t/z/5//AFALARAXAcAiAnAsAwA0A4A7A+BABCBD#3#BCBAA+A7A4A1AxAtAoAkAfAaAUAPAKAE///6/1/w/s/o/k/g/d/a/Y/W/V/U#2#/V/W/X/Z/b/e/h/l/o/s/w/1/5/+ACAHALAQAUAYAcAgAjAnApAsAuAwAxAy#3#AxAwAvAtArAoAlAiAfAbAYAUAQAMAIAEAA/9/5/2/y/v/s/q/n/l/k/i/h/h/g/h/h/i/j/k/m/n/q/s/v/x/0/3/6/9ABAEAHAKANAQATAVAXAaAbAdAfAgAhAhAiAiAhAhAgAfAeAcAbAZAXAVASAQAOALAJAGAEAB///8/6/4/2/0/z/x/w/v/u/u/t#3#/u/u/v/w/x/z/0/1/3/5/6/8/+AAACADAFAHAIAKALAMANAOAPAQAQAR#4#AQAQAPAPAOANAMALAKAJAIAGAFAEADACABAA///+/9/8/8/7/7/6#9#/7/7/8"
                };
                var pitchBends = ParsePitchBend(testArgs, 11);
                
                // 既存エンジンと同じ長さ（1283ms）で出力
                Console.WriteLine("\n  === Testing: match existing engine length ===");
                Resample(inputWav, "test_utau_match_length.wav", targetF0, 100, "",
                         offset, 1283f, otoConsonant, cutoff, 100, 0, pitchBends);
                var (matchSamples, matchFs) = Wav.ReadMono("test_utau_match_length.wav");
                Console.WriteLine($"  Wrote test_utau_match_length.wav (length={matchSamples.Length / (float)matchFs * 1000:F1}ms, same as existing)");
                
                // 既存エンジンとの長さ比較
                var files = new[] { "test_utau_full_args.wav", "test_utau_match_length.wav", "u+の.wav" };
                Console.WriteLine("\n  === Length comparison ===");
                foreach (var f in files)
                {
                    if (File.Exists(f))
                    {
                        var (s, fs) = Wav.ReadMono(f);
                        Console.WriteLine($"  {f}: {s.Length / (float)fs * 1000:F1}ms");
                    }
                }
                
                // 原音の該当部分を切り出して確認
                Console.WriteLine("\n  === Source audio analysis ===");
                var (srcSamples, srcFs) = Wav.ReadMono(inputWav);
                int srcOffset = (int)(offset / 1000.0 * srcFs);
                int srcEnd = srcOffset + (int)(Math.Abs(cutoff) / 1000.0 * srcFs);
                Console.WriteLine($"  Source segment: {offset:F1}ms to {offset + Math.Abs(cutoff):F1}ms ({Math.Abs(cutoff):F1}ms)");
                
                // 原音の該当部分を出力
                float[] srcSegment = new float[srcEnd - srcOffset];
                Array.Copy(srcSamples, srcOffset, srcSegment, 0, srcSegment.Length);
                Wav.WriteMono16("test_source_segment.wav", srcSegment, srcFs);
                Console.WriteLine($"  Wrote test_source_segment.wav (raw source segment)");
                
                // 既存エンジン出力のスペクトログラム風分析
                Console.WriteLine("\n  === Detailed waveform analysis ===");
                var (existingSamples, existingFs) = Wav.ReadMono("u+の.wav");
                var (llsmNoPbSamples, _) = Wav.ReadMono("test_utau_trans125.wav");
                var (llsmPbSamples, llsmFs) = Wav.ReadMono("test_utau_full_args.wav");
                
                // 100msごとのピッチとエネルギー
                int hopMs = 100;
                Console.WriteLine($"  Time | Existing | LLSM(noPB) | LLSM(+PB)");
                int nhopAnalysis = (int)(0.005f * existingFs);
                for (int t = 0; t < 800; t += hopMs)
                {
                    int start = (int)(t / 1000.0 * existingFs);
                    int end = (int)((t + hopMs) / 1000.0 * existingFs);
                    
                    // 既存
                    float existPitch = 0;
                    if (end <= existingSamples.Length)
                    {
                        float[] seg = new float[end - start];
                        Array.Copy(existingSamples, start, seg, 0, seg.Length);
                        var f0s = Pyin.Analyze(seg, existingFs, nhopAnalysis, 60, 800);
                        var voiced = f0s.Where(x => x > 0).ToArray();
                        if (voiced.Length > 0) existPitch = voiced.Average();
                    }
                    
                    // LLSM (no PB)
                    float llsmNoPbPitch = 0;
                    if (end <= llsmNoPbSamples.Length)
                    {
                        float[] seg = new float[end - start];
                        Array.Copy(llsmNoPbSamples, start, seg, 0, seg.Length);
                        var f0s = Pyin.Analyze(seg, llsmFs, nhopAnalysis, 60, 800);
                        var voiced = f0s.Where(x => x > 0).ToArray();
                        if (voiced.Length > 0) llsmNoPbPitch = voiced.Average();
                    }
                    
                    // LLSM (+PB)
                    float llsmPbPitch = 0;
                    if (end <= llsmPbSamples.Length)
                    {
                        float[] seg = new float[end - start];
                        Array.Copy(llsmPbSamples, start, seg, 0, seg.Length);
                        var f0s = Pyin.Analyze(seg, llsmFs, nhopAnalysis, 60, 800);
                        var voiced = f0s.Where(x => x > 0).ToArray();
                        if (voiced.Length > 0) llsmPbPitch = voiced.Average();
                    }
                    
                    Console.WriteLine($"  {t,4}ms | {existPitch,6:F1}Hz | {llsmNoPbPitch,6:F1}Hz | {llsmPbPitch,6:F1}Hz");
                }
                Console.WriteLine($"  Target: 466.2Hz (A#4), PB range: -40 to +6.7 cents");
            }
        }
        
        // テスト: .frq ファイルの読み込み
        Console.WriteLine("\n=== Test 8: FRQ file analysis ===");
        {
            string frqPath = wavPath + ".frq";  // _ああいあうえあ.wav.frq
            // または _ああいあうえあ_wav.frq
            if (!File.Exists(frqPath))
            {
                frqPath = Path.ChangeExtension(wavPath, null) + "_wav.frq";
            }
            
            Console.WriteLine($"  Looking for: {frqPath}");
            if (File.Exists(frqPath))
            {
                var frqData = ReadFrqFile(frqPath);
                Console.WriteLine($"  FRQ loaded: {frqData.SamplesPerFrame} samples/frame");
                Console.WriteLine($"  Average F0: {frqData.AverageF0:F1} Hz");
                Console.WriteLine($"  Frame count: {frqData.F0Values.Length}");
                if (frqData.F0Values.Length > 0)
                {
                    var voiced = frqData.F0Values.Where(x => x > 0).ToArray();
                    if (voiced.Length > 0)
                    {
                        Console.WriteLine($"  Voiced frames: {voiced.Length}, avg={voiced.Average():F1} Hz");
                    }
                }
                
                // .frq から F0 を取得して合成テスト
                Console.WriteLine("\n=== Test 9: Synthesis with FRQ F0 ===");
                
                // .frq から LLSM 用の F0 配列を作成
                int frqStartFrame = (int)(targetEntry.Offset / 1000.0 * wavFs / frqData.SamplesPerFrame);
                float[] frqF0 = new float[f0.Length];
                for (int i = 0; i < f0.Length; i++)
                {
                    float frqIdx = frqStartFrame + (float)i * nhop / frqData.SamplesPerFrame;
                    int idx = (int)frqIdx;
                    if (idx >= 0 && idx < frqData.F0Values.Length)
                    {
                        frqF0[i] = (float)frqData.F0Values[idx];
                    }
                }
                
                // .frq F0 で解析
                using var aoptFrq = Llsm.CreateAnalysisOptions();
                using var chunkFrq = Llsm.Analyze(aoptFrq, segment, wavFs, frqF0, frqF0.Length);
                
                // 再合成
                var resultFrq = SynthesizeChunk(chunkFrq, wavFs);
                Wav.WriteMono16("utau_frq_resynth.wav", resultFrq, wavFs);
                Console.WriteLine($"  Wrote utau_frq_resynth.wav ({resultFrq.Length} samples)");
                
                // 比較: PYIN F0 での再合成
                Console.WriteLine("  (Compare with utau_original.wav which uses PYIN F0)");
            }
            else
            {
                Console.WriteLine($"  FRQ file not found");
                // フォルダ内の .frq ファイルをリスト
                string dir = Path.GetDirectoryName(wavPath) ?? ".";
                var frqFiles = Directory.GetFiles(dir, "*.frq").Take(5);
                Console.WriteLine($"  Available .frq files:");
                foreach (var f in frqFiles)
                {
                    Console.WriteLine($"    {Path.GetFileName(f)}");
                }
            }
        }
        
        Console.WriteLine("\nDone!");
    }
    
    /// <summary>
    /// .frq ファイルを読み込む
    /// </summary>
    static FrqData ReadFrqFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        
        // ヘッダー: "FREQ0003" (8バイト)
        string header = new string(br.ReadChars(8));
        
        // サンプル/フレーム (4バイト int)
        int samplesPerFrame = br.ReadInt32();
        
        // 平均 F0 (8バイト double)
        double averageF0 = br.ReadDouble();
        
        // 不明フィールド (16バイト、スキップ)
        br.ReadBytes(16);
        
        // フレーム数 (4バイト int)
        int frameCount = br.ReadInt32();
        
        // F0 値と振幅値の配列
        double[] f0Values = new double[frameCount];
        double[] amplitudes = new double[frameCount];
        
        for (int i = 0; i < frameCount; i++)
        {
            f0Values[i] = br.ReadDouble();
            amplitudes[i] = br.ReadDouble();
        }
        
        return new FrqData
        {
            Header = header,
            SamplesPerFrame = samplesPerFrame,
            AverageF0 = averageF0,
            F0Values = f0Values,
            Amplitudes = amplitudes
        };
    }
    
    /// <summary>
    /// .frq ファイルのデータ
    /// </summary>
    class FrqData
    {
        public string Header { get; set; } = "";
        public int SamplesPerFrame { get; set; }
        public double AverageF0 { get; set; }
        public double[] F0Values { get; set; } = Array.Empty<double>();
        public double[] Amplitudes { get; set; } = Array.Empty<double>();
    }
    
    /// <summary>
    /// チャンクをそのまま合成（ピッチ変更なし）
    /// </summary>
    static float[] SynthesizeChunk(ChunkHandle chunk, int fs)
    {
        // 解析直後は Layer0 なので、そのまま合成
        // 位相の再計算はしない（解析時の位相をそのまま使用）
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, chunk);
        return Llsm.ReadOutput(output);
    }
    
    /// <summary>
    /// ピッチシフトして合成（フレームごとのF0比を使用）
    /// </summary>
    static float[] SynthesizeWithPitch(ChunkHandle srcChunk, int fs, float srcF0, float targetF0, int formantFollow = 100, int modulation = 100, bool useModPlus = false, float actualThop = 0.005f, float overlapMs = 0)
    {
        Console.WriteLine($"  Pitch shift: targetF0={targetF0:F1}Hz (using frame-by-frame F0 ratios, modulation={modulation}{(useModPlus ? " [M+]" : "")})");
        
        // Layer1 に変換（NFFT=4096で低音域の倍音解像度向上）
        Llsm.ChunkToLayer1(srcChunk, 4096);
        
        // 適応的フォルマント処理（Fフラグ）をsrcChunkに適用（Layer1状態、逆位相伝播前）
        // 注意：この処理は現在無効化されています（VTMAGNアクセスに問題があるため）
        // 代わりに、F0シフトのみを行い、フォルマント処理は後で行います
        
        // 逆位相伝播
        Llsm.ChunkPhasePropagate(srcChunk, -1);
        
        int nfrm = Llsm.GetNumFrames(srcChunk);
        
        // 各フレームの F0 を変更（modulation適用）
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(srcChunk, i);
            float originalF0 = Llsm.GetFrameF0(frame);
            if (originalF0 > 0)
            {
                // M+フラグ: ノート境界でmodulationをフェード
                float dynamicMod = useModPlus
                    ? GetDynamicModulation(i, nfrm, modulation, actualThop, overlapMs)
                    : modulation;
                
                // modulation適用：原音の揺らぎを調整
                float deviation = originalF0 - srcF0;  // 平均からのずれ
                float modRatio = dynamicMod / 100.0f;
                float adjustedSourceF0 = srcF0 + deviation * modRatio;  // 揺らぎをスケール
                
                float pitchRatio = targetF0 / srcF0;
                float newF0 = adjustedSourceF0 * pitchRatio;  // 揺らぎを保持してシフト
                
                Llsm.SetFrameF0(frame, newF0);
                
                // デバッグ: 最初と最後のフレームを出力
                if (i == 0 || i == nfrm - 1)
                {
                    Console.WriteLine($"    [Frame {i}] srcF0={originalF0:F1} (dev={deviation:+0.0;-0.0}, mod={dynamicMod:F0}%) -> newF0={newF0:F1}Hz");
                }
            }
        }
        
        // Layer0 に変換
        Llsm.ChunkToLayer0(srcChunk);
        
        // 順方向位相伝播
        Llsm.ChunkPhasePropagate(srcChunk, +1);
        
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, srcChunk);
        return Llsm.ReadOutput(output);
    }
    
    /// <summary>
    /// タイムストレッチして合成（フレームコピー方式）
    /// </summary>
    static float[] SynthesizeWithStretch(ChunkHandle srcChunk, int fs, float stretchRatio)
    {
        // Layer1 に変換（NFFT=4096で低音域の倍音解像度向上）
        Llsm.ChunkToLayer1(srcChunk, 4096);
        
        // 逆位相伝播
        Llsm.ChunkPhasePropagate(srcChunk, -1);
        
        int srcNfrm = Llsm.GetNumFrames(srcChunk);
        int dstNfrm = (int)(srcNfrm * stretchRatio);
        
        Console.WriteLine($"  Stretch: {srcNfrm} -> {dstNfrm} frames");
        
        // 新しい chunk を作成
        var conf = Llsm.GetConf(srcChunk);
        var confCopy = Llsm.CopyContainer(conf);
        var nfrmPtr = NativeLLSM.llsm_container_get(confCopy.Ptr, NativeLLSM.LLSM_CONF_NFRM);
        Marshal.WriteInt32(nfrmPtr, dstNfrm);
        
        var dstChunk = Llsm.CreateChunk(confCopy, 0);
        
        // フレームコピー（最近傍）
        for (int i = 0; i < dstNfrm; i++)
        {
            float srcIdxF = (float)i / dstNfrm * srcNfrm;
            int idx0 = Math.Min((int)srcIdxF, srcNfrm - 1);
            
            var srcFrame = Llsm.GetFrame(srcChunk, idx0);
            var newFramePtr = Llsm.CopyFrame(srcFrame);
            Llsm.SetFrame(dstChunk, i, newFramePtr);
        }
        
        // Layer0 に変換
        Llsm.ChunkToLayer0(dstChunk);
        
        // 順方向位相伝播
        Llsm.ChunkPhasePropagate(dstChunk, +1);
        
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, dstChunk);
        return Llsm.ReadOutput(output);
    }
    
    /// <summary>
    /// ピッチシフト + タイムストレッチして合成
    /// </summary>
    static float[] SynthesizeWithPitchAndStretch(ChunkHandle srcChunk, int fs, float avgSrcF0, float targetF0, float stretchRatio)
    {
        return SynthesizeWithPitchAndStretch(srcChunk, fs, avgSrcF0, targetF0, stretchRatio, new List<int>());
    }
    
    /// <summary>
    /// ピッチシフト + タイムストレッチして合成（ピッチベンド対応）
    /// </summary>
    static float[] SynthesizeWithPitchAndStretch(ChunkHandle srcChunk, int fs, float avgSrcF0, float targetF0, float stretchRatio, List<int> pitchBend)
    {
        float basePitchRatio = targetF0 / avgSrcF0;
        
        // Layer1 に変換（NFFT=4096で低音域の倍音解像度向上）
        Llsm.ChunkToLayer1(srcChunk, 4096);
        
        // 逆位相伝播
        Llsm.ChunkPhasePropagate(srcChunk, -1);
        
        int srcNfrm = Llsm.GetNumFrames(srcChunk);
        int dstNfrm = (int)(srcNfrm * stretchRatio);
        
        // 新しい chunk を作成
        var conf = Llsm.GetConf(srcChunk);
        var confCopy = Llsm.CopyContainer(conf);
        var nfrmPtr = NativeLLSM.llsm_container_get(confCopy.Ptr, NativeLLSM.LLSM_CONF_NFRM);
        Marshal.WriteInt32(nfrmPtr, dstNfrm);
        
        var dstChunk = Llsm.CreateChunk(confCopy, 0);
        
        // フレームコピー + ピッチ変更
        for (int i = 0; i < dstNfrm; i++)
        {
            float srcIdxF = (float)i / dstNfrm * srcNfrm;
            int idx0 = Math.Min((int)srcIdxF, srcNfrm - 1);
            
            var srcFrame = Llsm.GetFrame(srcChunk, idx0);
            var newFramePtr = Llsm.CopyFrame(srcFrame);
            
            // ピッチベンドを適用（セント単位、1ベンドポイント = 1フレーム として簡略化）
            float pitchRatio = basePitchRatio;
            if (pitchBend.Count > 0)
            {
                // ピッチベンドの補間インデックスを計算
                float pbIdxF = (float)i / dstNfrm * pitchBend.Count;
                int pbIdx = Math.Min((int)pbIdxF, pitchBend.Count - 1);
                int cents = pitchBend[pbIdx];
                // セント→比率: 2^(cents/1200)
                pitchRatio *= (float)Math.Pow(2, cents / 1200.0);
            }
            
            // F0 にピッチシフトを適用
            float f0 = Llsm.GetFrameF0(srcFrame);
            if (f0 > 0)
            {
                var newF0Ptr = NativeLLSM.llsm_create_fp(f0 * pitchRatio);
                NativeLLSM.llsm_container_attach_(newFramePtr, NativeLLSM.LLSM_FRAME_F0,
                    newF0Ptr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
            }
            
            Llsm.SetFrame(dstChunk, i, newFramePtr);
        }
        
        // Layer0 に変換
        Llsm.ChunkToLayer0(dstChunk);
        
        // 順方向位相伝播
        Llsm.ChunkPhasePropagate(dstChunk, +1);
        
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, dstChunk);
        return Llsm.ReadOutput(output);
    }
    
    /// <summary>
    /// oto.ini をパース（Shift-JIS）
    /// </summary>
    static List<OtoEntry> ParseOtoIni(string path)
    {
        var entries = new List<OtoEntry>();
        
        // Shift-JIS で読み込み
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var lines = File.ReadAllLines(path, Encoding.GetEncoding("shift_jis"));
        
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            // 形式: filename.wav=alias,offset,consonant,cutoff,preutterance,overlap
            int eqIdx = line.IndexOf('=');
            if (eqIdx < 0) continue;
            
            string fileName = line.Substring(0, eqIdx);
            string rest = line.Substring(eqIdx + 1);
            
            var parts = rest.Split(',');
            if (parts.Length < 6) continue;
            
            try
            {
                entries.Add(new OtoEntry
                {
                    FileName = fileName,
                    Alias = parts[0],
                    Offset = float.Parse(parts[1]),
                    Consonant = float.Parse(parts[2]),
                    Cutoff = float.Parse(parts[3]),
                    Preutterance = float.Parse(parts[4]),
                    Overlap = float.Parse(parts[5])
                });
            }
            catch
            {
                // パース失敗は無視
            }
        }
        
        return entries;
    }
}

/// <summary>
/// oto.ini のエントリ
/// </summary>
class OtoEntry
{
    public string FileName { get; set; } = "";
    public string Alias { get; set; } = "";
    public float Offset { get; set; }      // 左ブランク (ms)
    public float Consonant { get; set; }   // 固定範囲 (ms)
    public float Cutoff { get; set; }      // 右ブランク (ms)、負の値は末尾からのオフセット
    public float Preutterance { get; set; } // 先行発声 (ms)
    public float Overlap { get; set; }     // オーバーラップ (ms)
}

// Wav クラス
public static class Wav
{
    public static void WriteMono16(string path, float[] samples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        int numChannels = 1;
        short bitsPerSample = 16;
        int byteRate = sampleRate * numChannels * bitsPerSample / 8;
        short blockAlign = (short)(numChannels * bitsPerSample / 8);

        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = MathF.Max(-1f, MathF.Min(1f, samples[i]));
            pcm[i] = (short)MathF.Round(s * 32767f);
        }
        int dataSize = pcm.Length * sizeof(short);

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)numChannels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        var span = MemoryMarshal.AsBytes(pcm.AsSpan());
        bw.Write(span);
    }

    public static (float[] samples, int sampleRate) ReadMono(string path)
    {
        // パスのデバッグ（Shift-JIS パス問題の診断用）
        if (!File.Exists(path))
        {
            // バイト列を確認
            var bytes = Encoding.UTF8.GetBytes(path);
            throw new FileNotFoundException($"File not found: {path} (bytes: {string.Join(",", bytes.Take(50))})");
        }
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not RIFF");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not WAVE");

        short audioFormat = 1;
        short numChannels = 1;
        short bitsPerSample = 16;
        int sampleRate = 0;
        byte[]? data = null;

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            long next = br.BaseStream.Position + size;
            if (id == "fmt ")
            {
                audioFormat = br.ReadInt16();
                numChannels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bitsPerSample = br.ReadInt16();
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            br.BaseStream.Position = next;
        }

        if (data == null) throw new InvalidDataException("No data chunk");

        if (audioFormat == 1 && bitsPerSample == 16)
        {
            int samples = data.Length / 2 / numChannels;
            var output = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                int offset = i * numChannels * 2;
                short s = BitConverter.ToInt16(data, offset);
                output[i] = MathF.Max(-1f, MathF.Min(1f, s / 32768f));
            }
            return (output, sampleRate);
        }
        else if (audioFormat == 3 && bitsPerSample == 32)
        {
            int samples = data.Length / 4 / numChannels;
            var output = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                int offset = i * numChannels * 4;
                output[i] = BitConverter.ToSingle(data, offset);
            }
            return (output, sampleRate);
        }
        else
        {
            throw new NotSupportedException($"Unsupported WAV: format={audioFormat}, bits={bitsPerSample}");
        }
    }
}
}
