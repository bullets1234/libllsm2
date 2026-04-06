using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
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
    const float Thop = 0.005f;     // 5ms（LLSM安定設定、PBはフレーム間補間で精度確保）
    
    // (旧 USE_MONOTONIC_CUBIC フラグは秋間補間に統一したため廃止)
    
    /// <summary>
    /// ピッチマーク: F0情報から抽出されたピッチ周期の境界点
    /// </summary>
    struct PitchMark
    {
        public float Time;         // 時刻 (秒)
        public int FrameIndex;     // フレームインデックス
        public float Period;       // ピッチ周期 (秒)
        public bool IsVoiced;      // 有声/無声フラグ
        public float F0;           // 基本周波数 (Hz)
        
        public PitchMark(float time, int frameIndex, float period, bool isVoiced, float f0)
        {
            Time = time;
            FrameIndex = frameIndex;
            Period = period;
            IsVoiced = isVoiced;
            F0 = f0;
        }
    }
    
    // ネイティブ関数デリゲート
    delegate void DeleteFpDelegate(IntPtr ptr);
    static readonly DeleteFpDelegate _deleteFp = NativeLLSM.llsm_delete_fp;
    static readonly DeleteFpDelegate _deleteFpArray = NativeLLSM.llsm_delete_fparray;
    static readonly DeleteFpDelegate _deleteHm = NativeLLSM.llsm_delete_hmframe;
    static readonly DeleteFpDelegate _deleteNm = NativeLLSM.llsm_delete_nmframe;
    
    delegate IntPtr CopyFpArrayDelegate(IntPtr ptr);
    static readonly CopyFpArrayDelegate _copyFpArrayFunc = NativeLLSM.llsm_copy_fparray;
    static readonly CopyFpArrayDelegate _copyNm = NativeLLSM.llsm_copy_nmframe;
    
    // PBP (Pulse-by-Pulse) 関連デリゲート
    static readonly DeleteFpDelegate _deletePbpEffect = NativeLLSM.llsm_delete_pbpeffect;
    static readonly CopyFpArrayDelegate _copyPbpEffect = NativeLLSM.llsm_copy_pbpeffect;
    static readonly DeleteFpDelegate _deleteInt = NativeLLSM.llsm_delete_int;
    static readonly NativeLLSM.CopyFunc _copyInt = NativeLLSM.llsm_copy_int;
    
    // グロウルコールバック用のGC保護(デリゲートがガベージコレクトされないように)
    static NativeLLSM.llsm_fgfm _growlCallback = null;
    
    // Cフラグ: 分解出力（正弦波/ノイズ分離）のための共有バッファ
    // 合成メソッド内でセットされ、Resample()のCフラグブレンドで参照される
    static bool _needDecomposedOutput = false;
    static float[]? _lastSynthSin = null;
    static float[]? _lastSynthNoise = null;
    
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
            // ★PYIN 2パス推定（R: レンジ絞り込みによるオクターブ誤推定抑制）
            // 1パス目: 広レンジ(60-800Hz)で粗推定 → 中央値取得
            // 2パス目: 中央値±マージンに絞って再推定 → オクターブ混同確率を激減
            f0 = Pyin.Analyze(segment, fs, nhop, 60, 800);
            var voicedF0Pass1 = f0.Where(x => x > 0).ToArray();
            
            if (voicedF0Pass1.Length > 2)
            {
                Array.Sort(voicedF0Pass1);
                float medianF0Pass1 = voicedF0Pass1[voicedF0Pass1.Length / 2];
                
                // 2パス目: 中央値を基準にレンジを絞る
                // 下限: median×0.55（½オクターブ＋マージン — 低い声域変動に対応）
                // 上限: median×1.8（1オクターブ弱 — 高い声域変動に対応）
                float fmin2 = MathF.Max(50f, medianF0Pass1 * 0.55f);
                float fmax2 = MathF.Min(1000f, medianF0Pass1 * 1.8f);
                
                // レンジが十分狭まった場合のみ2パス目実行（効果がある場合のみ）
                if (fmax2 / fmin2 < 800f / 60f * 0.9f)
                {
                    float[] f0Pass1 = (float[])f0.Clone();  // 1パス目結果を保存
                    f0 = Pyin.Analyze(segment, fs, nhop, fmin2, fmax2);
                    
                    // ハイブリッド: 2パスで失われたvoicedフレームを1パスで補填
                    // 2パスの狭いレンジでCV遷移部のF0候補が消え、unvoicedになる問題を回避
                    int restored = 0;
                    for (int i = 0; i < Math.Min(f0.Length, f0Pass1.Length); i++)
                    {
                        if (f0[i] <= 0 && f0Pass1[i] > 0)
                        {
                            f0[i] = f0Pass1[i];
                            restored++;
                        }
                    }
                    Console.WriteLine($"  [Pitch] PYIN 2-pass: range narrowed to {fmin2:F0}-{fmax2:F0}Hz (median={medianF0Pass1:F1}Hz){(restored > 0 ? $", restored {restored} frames from pass1" : "")}");
                }
            }
            
            var voicedF0 = f0.Where(x => x > 0).ToArray();
            srcF0 = voicedF0.Length > 0 ? voicedF0.Average() : targetF0;
            
            // ★局所オクターブジャンプ修正（M': 局所ウィンドウ式）
            // グローバル中央値ではなく前後±15フレームの局所中央値を基準に修正
            // 音域変化の大きい歌唱でも追従可能
            // ★V/UV境界でウィンドウ打ち切り: VCVの前母音/後母音が異なる音高でも誤修正しない
            if (voicedF0.Length > 2)
            {
                const int octaveWindowHalf = 15;  // ±15フレーム = ±75ms
                int corrected = 0;
                for (int i = 0; i < f0.Length; i++)
                {
                    if (f0[i] <= 0) continue;
                    
                    // 局所ウィンドウ内のvoicedフレームを収集
                    // V/UV境界（F0=0のフレーム）でウィンドウを打ち切る
                    var localVoiced = new List<float>();
                    // 前方向: F0=0に当たったら停止
                    for (int j = i - 1; j >= Math.Max(0, i - octaveWindowHalf); j--)
                    {
                        if (f0[j] <= 0) break;  // V/UV境界で打ち切り
                        localVoiced.Add(f0[j]);
                    }
                    // 後方向: F0=0に当たったら停止
                    for (int j = i + 1; j <= Math.Min(f0.Length - 1, i + octaveWindowHalf); j++)
                    {
                        if (f0[j] <= 0) break;  // V/UV境界で打ち切り
                        localVoiced.Add(f0[j]);
                    }
                    
                    // 局所フレームが少なすぎる場合はグローバル中央値にフォールバック
                    float refF0;
                    if (localVoiced.Count >= 3)
                    {
                        localVoiced.Sort();
                        refF0 = localVoiced[localVoiced.Count / 2];
                    }
                    else
                    {
                        Array.Sort(voicedF0);
                        refF0 = voicedF0[voicedF0.Length / 2];
                    }
                    
                    float ratio = f0[i] / refF0;
                    if (ratio > 1.6f)
                    {
                        f0[i] /= MathF.Round(ratio);
                        corrected++;
                    }
                    else if (ratio < 0.625f && ratio > 0.01f)
                    {
                        f0[i] *= MathF.Round(1.0f / ratio);
                        corrected++;
                    }
                }
                if (corrected > 0)
                {
                    Console.WriteLine($"  [Pitch] Corrected {corrected} octave jumps (local window ±{octaveWindowHalf})");
                    var correctedVoiced = f0.Where(x => x > 0).ToArray();
                    srcF0 = correctedVoiced.Length > 0 ? correctedVoiced.Average() : targetF0;
                }
            }
            
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
        
        // Hフラグ: High-resolution mode（超解像度モード）
        // 指定なし=標準品質（maxnhar=800固定）、指定あり=動的倍音数調整（F0に応じて20kHzまでカバー）
        // 分析オプション設定で使用するため、ここで先に解析
        bool useHighResolution = flags.Contains("H", StringComparison.OrdinalIgnoreCase);
        
        // Qフラグ: Quality F0 refinement（F0精密化）
        // 指定なし=標準F0、指定あり=高調波解析ベースのF0精密化
        // ビブラート・ポルタメントの滑らかさ向上、子音→母音遷移の自然さ向上
        bool useF0Refine = flags.Contains("Q", StringComparison.OrdinalIgnoreCase);
        
        // Wフラグ: Adaptive Window Size（適応的窓サイズ）
        // 指定なし=固定4.0周期、指定あり=F0に応じて動的調整（低音→3.0、高音→5.0）
        // 低音での子音明瞭度向上、高音での倍音豊かさ向上
        bool useAdaptiveWindow = flags.Contains("W", StringComparison.OrdinalIgnoreCase);
        
        // Uフラグ: Unvoiced attenuation（無声音減衰）
        // U9 = -9dB（標準）、U12 = -12dB（強調）、U15 = -15dB（最大）
        // 無声子音（さ行/た行等）の明瞭度向上、有声成分混入を防止
        int unvoicedAttenuation = 0;  // 0 = オフ
        var uMatch = Regex.Match(flags, @"U(\d+)", RegexOptions.IgnoreCase);
        if (uMatch.Success)
        {
            unvoicedAttenuation = int.Parse(uMatch.Groups[1].Value);
            unvoicedAttenuation = Math.Clamp(unvoicedAttenuation, 6, 20);  // 6-20dB範囲
            Console.WriteLine($"  [Unvoiced] Attenuation: -{unvoicedAttenuation}dB");
        }
        
        // Tフラグ: Spectral Tilt（スペクトル傾斜）
        // T0 = フラット、T-6 = -6dB/oct（暗い声）、T+6 = +6dB/oct（明るい声）
        // 周波数全域でスペクトル傾斜を調整、声質の明るさ/暗さをコントロール
        int spectralTilt = 0;  // デフォルト0 = フラット
        var tMatch = Regex.Match(flags, @"T([+-]?\d+)", RegexOptions.IgnoreCase);
        if (tMatch.Success)
        {
            spectralTilt = int.Parse(tMatch.Groups[1].Value);
            spectralTilt = Math.Clamp(spectralTilt, -12, 12);  // ±12dB/oct範囲
        }
        
        // Sフラグ: Shift-noise（ノイズ成分ピッチシフト追従）
        // 指定なし = ノイズ成分はピッチシフトしない（デフォルト、自然な息成分維持）
        // 指定あり = ノイズ成分もピッチシフトに追従（高音で明るく、低音で暗くなる）
        bool pitchShiftNoise = flags.Contains("S", StringComparison.OrdinalIgnoreCase);
        if (pitchShiftNoise)
        {
            Console.WriteLine($"  [Noise] Shift-noise enabled (S flag) - NM follows pitch");
        }
        
        // Gフラグ: Growl（グロウル効果）
        // G0 = なし（デフォルト）、G1-G100 = 弱い～強いグロウル
        // 声帯の不規則な振動をシミュレート：サブハーモニクス、ジッター、声門波形変調
        // 注意: 大文字Gのみ（小文字gはジェンダーファクター）
        int growlStrength = 0;  // 0 = オフ
        var growlMatch = Regex.Match(flags, @"G(\d+)");  // 大文字Gのみ
        if (growlMatch.Success)
        {
            growlStrength = int.Parse(growlMatch.Groups[1].Value);
            growlStrength = Math.Clamp(growlStrength, 1, 100);  // 1-100範囲
            Console.WriteLine($"  [Growl] Strength: {growlStrength}% (G flag, PBP synthesis)");
        }
        
        // Kフラグ: 声門閉鎖係数（Glottal Closure）調整
        // K50 = 標準（デフォルト）、K0 = 完全開放（息漏れ声）、K100 = 完全閉鎖（硬い声）
        // GFMパラメータのRg（声門閉鎖係数）を調整して声質を変更
        int glottalClosure = 50;  // デフォルト50 = 標準
        var kMatch = Regex.Match(flags, @"K(\d+)", RegexOptions.IgnoreCase);
        if (kMatch.Success)
        {
            glottalClosure = int.Parse(kMatch.Groups[1].Value);
            glottalClosure = Math.Clamp(glottalClosure, 0, 100);  // 0-100範囲
            if (glottalClosure != 50)
            {
                Console.WriteLine($"  [GlottalClosure] K{glottalClosure} (Rg adjustment)");
            }
        }
        
        // Lフラグ: 声門パラメータ自動推定 + リップ放射フィルタ（デフォルトoff）
        bool useGlottalAutoEstimate = flags.Contains("L", StringComparison.OrdinalIgnoreCase);
        if (useGlottalAutoEstimate)
        {
            Console.WriteLine($"  [GlottalEstimate] Auto-estimate Rd parameter from source (L flag)");
        }
        
        // Cフラグ: 子音原音ブレンド（LLSM再合成で劣化する子音を原音PCMで置換）
        // C0=オフ、C50=50%ブレンド（デフォルト）、C100=子音完全置換
        int consonantBlendStrength = 0;  // デフォルトオフ
        var cBlendMatch = System.Text.RegularExpressions.Regex.Match(flags, @"C(\d+)");
        if (cBlendMatch.Success)
        {
            consonantBlendStrength = int.Parse(cBlendMatch.Groups[1].Value);
            consonantBlendStrength = Math.Clamp(consonantBlendStrength, 1, 100);
            Console.WriteLine($"  [ConsonantBlend] C{consonantBlendStrength} - original PCM blend {consonantBlendStrength}%");
        }
        
        // Dフラグ: HNR（倍音対雑音比）改善（デフォルト0 = off）
        int hnrEnhancement = 0;
        var dMatch = System.Text.RegularExpressions.Regex.Match(flags, @"D(\d+)");
        if (dMatch.Success)
        {
            hnrEnhancement = int.Parse(dMatch.Groups[1].Value);
            hnrEnhancement = Math.Clamp(hnrEnhancement, 1, 100);
            Console.WriteLine($"  [HNR] D{hnrEnhancement} - noise reduction strength {hnrEnhancement}%");
        }
        
        // Xフラグ: 振幅補正を固定比率(targetF0/srcF0)に切替
        // 省略 = フレームごとの実F0比率（デフォルト、ピッチベンド追従が正確）
        // X = 固定比率（一部の音源で安定する場合がある）
        bool useFixedAmplitudeRatio = flags.Contains('X', StringComparison.OrdinalIgnoreCase);
        if (useFixedAmplitudeRatio)
        {
            Console.WriteLine($"  [AmplComp] X - fixed ratio mode (targetF0/srcF0)");
        }
        
        // 分析オプション最適化（高品質設定）
        int maxnhar, maxnhar_e;
        
        if (useHighResolution)
        {
            // Hフラグ: 超解像度モード - 動的倍音数調整
            // サンプリングレート44.1kHzでナイキスト周波数22.05kHzまでカバー
            // 低音（F0=100Hz）→ 210倍音で21kHz、高音（F0=500Hz）→ 42倍音で21kHz
            float maxFreq = 21000.0f;  // 21kHz（高音の劣化防止、ナイキスト周波数22.05kHzの95%）
            maxnhar = (int)Math.Ceiling(maxFreq / srcF0);
            maxnhar = Math.Clamp(maxnhar, 100, 2000);  // 100-2000倍音の範囲に制限
            
            // エンベロープ倍音数: F0依存の動的調整（子音品質向上）
            // 低F0（80-150Hz）: 15-20倍音 → 周波数分解能 4-10Hz（細かい子音変化を捉える）
            // 中F0（150-300Hz）: 10-12倍音 → 周波数分解能 12-25Hz（バランス重視）
            // 高F0（300-600Hz）: 8-10倍音  → 周波数分解能 30-60Hz（計算効率重視）
            float maxnhar_e_ratio = srcF0 < 120f ? 0.12f :   // 低音: 12%
                                    srcF0 < 200f ? 0.08f :   // 中低音: 8%
                                    srcF0 < 350f ? 0.05f :   // 中音: 5%
                                                   0.03f;    // 高音: 3%
            // ★高音ザラつき対策: maxnhar_eの下限を12に引き上げ
            // 高F0(500Hz)×8倍音=4kHzでは高域ノイズ包絡が粗い。12倍音=6kHzでカバー
            maxnhar_e = Math.Clamp((int)(maxnhar * maxnhar_e_ratio), 12, 24);
            
            Console.WriteLine($"  [High-Res] Dynamic harmonics: maxnhar={maxnhar}, maxnhar_e={maxnhar_e} (F0={srcF0:F1}Hz)");
        }
        else
        {
            // 標準モード: 固定値（子音品質向上のため8に引き上げ）
            maxnhar = 800;       // 最大倍音数（8kHzまでカバー、F0=100Hz時）
            maxnhar_e = 12;      // ★高音ザラつき対策: 8→12に引き上げ（高域ノイズ包絡精度向上）
        }
        
        unsafe
        {
            var aoptPtr = (NativeLLSM.llsm_aoptions*)aopt.DangerousGetHandle().ToPointer();
            aoptPtr->npsd = 256;          // PSDサイズ（LLSMデフォルト: 256、ノイズ解像度向上）
            aoptPtr->maxnhar = maxnhar;
            aoptPtr->maxnhar_e = maxnhar_e;
            aoptPtr->hm_method = 1;       // LLSM_AOPTION_HMCZT（CZT法、高品質）
            
            // Qフラグ: F0リファインメント有効化
            if (useF0Refine)
            {
                aoptPtr->f0_refine = 1;   // 高調波解析ベースのF0精密化
                Console.WriteLine($"  [Quality] F0 refinement enabled (Q flag)");
            }
            else
            {
                aoptPtr->f0_refine = 0;   // 標準（デフォルト）
            }
            
            // Wフラグ: 適応的窓サイズ調整
            if (useAdaptiveWindow)
            {
                // F0統計を計算（有音部のみ）
                var validF0 = f0.Where(x => x > 0).ToArray();
                if (validF0.Length > 0)
                {
                    float avgF0 = validF0.Average();
                    float minF0 = validF0.Min();
                    float maxF0 = validF0.Max();
                    float f0Range = maxF0 - minF0;
                    
                    // F0範囲が広い場合は中間値、狭い場合は平均値を使用
                    float refF0 = (f0Range > 200) ? (minF0 + maxF0) / 2 : avgF0;
                    
                    // 対数スケールで滑らかに調整（200Hzを基準点）
                    float logF0 = MathF.Log(refF0 / 200.0f);
                    float rel_winsize = 4.0f * MathF.Exp(-0.2f * logF0);
                    rel_winsize = Math.Clamp(rel_winsize, 2.5f, 6.0f);  // 安全範囲
                    
                    aoptPtr->rel_winsize = rel_winsize;
                    Console.WriteLine($"  [Window] Adaptive: F0={refF0:F1}Hz → rel_winsize={rel_winsize:F2} (W flag)");
                }
                else
                {
                    aoptPtr->rel_winsize = 4.0f;  // F0が全て0の場合はデフォルト
                }
            }
            else
            {
                aoptPtr->rel_winsize = 4.0f;  // 標準（デフォルト: 4周期）
            }
        }
        
        // ★分析時2xオーバーサンプリング（常時有効）
        // 倍音間のスペクトル分離を改善し、CZT調波推定精度とフォルマント解像度を向上
        int analysisFs = fs * 2;
        float[] upsampledSegment = Upsample2x(segment);
        Console.WriteLine($"  [OversampledAnalysis] {fs}Hz -> {analysisFs}Hz ({segment.Length} -> {upsampledSegment.Length} samples)");
        
        using var chunk = Llsm.Analyze(aopt, upsampledSegment, analysisFs, f0, f0.Length);
        
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
        // 注意: 小文字gのみ（大文字Gはグロウル効果）
        int genderFactor = 0;
        var gMatch = System.Text.RegularExpressions.Regex.Match(flags, @"g([+-]?\d+)");  // 小文字gのみ
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
        
        // Rフラグ: RPS (Repeated Phase Synchronization)（指定なし=Frame-levelのみ、指定あり=Chunk-level RPSも有効化）
        // 長時間タイムストレッチ時の位相歪み補正に効果的
        bool useChunkRPS = flags.Contains("R", StringComparison.OrdinalIgnoreCase);
        
        // eフラグ: Elastic stretch (Élastiqueスタイル、トランジェント保護）
        // 指定なし=標準タイムストレッチ、指定あり=適応的タイムストレッチ（子音の明瞭度向上）
        bool useElasticStretch = flags.Contains("e", StringComparison.OrdinalIgnoreCase);
        
        // Aフラグ: Pitch-mark driven stretch (ピッチマーク駆動、連続音最適化、Approach A)
        bool usePitchMarkStretch = flags.Contains("A", StringComparison.OrdinalIgnoreCase);
        
        if (useElasticStretch)
        {
            Console.WriteLine($"  [Elastic] e flag enabled: transient-preserving time stretch");
        }
        if (usePitchMarkStretch)
        {
            Console.WriteLine($"  [PitchMark] A flag enabled: pitch-mark driven stretch (Approach A)");
        }
        
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
                // 中央値をそのまま使用（外れ値にロバスト）
                llsmF0Values.Sort();
                int count = llsmF0Values.Count;
                srcF0 = (count % 2 == 0)
                    ? (llsmF0Values[count / 2 - 1] + llsmF0Values[count / 2]) / 2f
                    : llsmF0Values[count / 2];
                Console.WriteLine($"  [Pitch] srcF0 from LLSM (median): {srcF0:F1}Hz ({count} voiced frames)");
            }
            
            // ★Post-LLSM 孤立フレームオクターブ修正（S: 最終防衛ライン）
            // IF精密化を通過した残留オクターブエラーをピンポイント修正
            // 前後フレームとの比が1.8倍/0.55倍を超える孤立1-2フレームだけ修正
            {
                int isolatedFixed = 0;
                for (int i = 0; i < nfrm; i++)
                {
                    var frame = Llsm.GetFrame(chunk, i);
                    float fi = Llsm.GetFrameF0(frame);
                    if (fi <= 0) continue;
                    
                    // 前後の有声フレームF0を取得
                    float fPrev = 0, fNext = 0;
                    for (int j = i - 1; j >= Math.Max(0, i - 3); j--)
                    {
                        var pf = Llsm.GetFrame(chunk, j);
                        float pf0 = Llsm.GetFrameF0(pf);
                        if (pf0 > 0) { fPrev = pf0; break; }
                    }
                    for (int j = i + 1; j <= Math.Min(nfrm - 1, i + 3); j++)
                    {
                        var nf = Llsm.GetFrame(chunk, j);
                        float nf0 = Llsm.GetFrameF0(nf);
                        if (nf0 > 0) { fNext = nf0; break; }
                    }
                    
                    // 前後両方が存在し、両方との比が異常な場合のみ修正（孤立判定）
                    if (fPrev > 0 && fNext > 0)
                    {
                        float ratioPrev = fi / fPrev;
                        float ratioNext = fi / fNext;
                        // 前後両方に対して同方向のオクターブジャンプ
                        if (ratioPrev > 1.8f && ratioNext > 1.8f)
                        {
                            float avgRatio = (ratioPrev + ratioNext) / 2f;
                            float corrected = fi / MathF.Round(avgRatio);
                            Llsm.SetFrameF0(frame, corrected);
                            isolatedFixed++;
                        }
                        else if (ratioPrev < 0.55f && ratioNext < 0.55f && ratioPrev > 0.01f)
                        {
                            float avgRatio = (1f / ratioPrev + 1f / ratioNext) / 2f;
                            float corrected = fi * MathF.Round(avgRatio);
                            Llsm.SetFrameF0(frame, corrected);
                            isolatedFixed++;
                        }
                    }
                }
                if (isolatedFixed > 0)
                {
                    Console.WriteLine($"  [Pitch] Fixed {isolatedFixed} isolated octave-jump frames (post-LLSM)");
                    // srcF0を再計算
                    var finalF0Values = new List<float>();
                    for (int i = 0; i < nfrm; i++)
                    {
                        var frame = Llsm.GetFrame(chunk, i);
                        float ff = Llsm.GetFrameF0(frame);
                        if (ff > 0) finalF0Values.Add(ff);
                    }
                    if (finalF0Values.Count > 0)
                    {
                        finalF0Values.Sort();
                        int cnt = finalF0Values.Count;
                        srcF0 = (cnt % 2 == 0)
                            ? (finalF0Values[cnt / 2 - 1] + finalF0Values[cnt / 2]) / 2f
                            : finalF0Values[cnt / 2];
                        Console.WriteLine($"  [Pitch] srcF0 updated after isolation fix: {srcF0:F1}Hz");
                    }
                }
            }
            
            // ★息成分（NM PSD）の品質改善処理
            // F0誤推定による突発的なリーケージを除去
            ApplyMedianFilterToNmPsd(chunk, nfrm);
            
            // V/UV境界での強烈なリーケージを抑制
            ConstrainBoundaryNmPsd(chunk, nfrm);
        }
        
        // 子音速度（velocity）から子音ストレッチ比率を計算
        // velocity = 100: 標準速度（1.0x）
        // velocity < 100: 子音が長くなる（例：50 = 2.0x）
        // velocity > 100: 子音が短くなる（例：200 = 0.5x）
        // UTAU仕様: 実効子音長 = fixed_ms × 2^((100 - velocity) / 100)
        // velocity=100: 標準(1.0x), velocity=200: 約半分(0.5x), velocity=0: 約2倍(2.0x)
        float consonantStretch = MathF.Pow(2.0f, (100.0f - velocity) / 100.0f);
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
        
        // HNR改善（Dフラグ）: Layer0状態でノイズ成分を適応的に低減
        if (hnrEnhancement > 0)
        {
            EnhanceHNR(chunk, nfrm, fs, hnrEnhancement);
        }
        
        // 合成（子音部velocity + 伸縮部）
        // Cフラグ有効時: 分解出力（正弦波/ノイズ分離）を要求
        _needDecomposedOutput = (consonantBlendStrength > 0);
        _lastSynthSin = null;
        _lastSynthNoise = null;
        
        float[] output;
        if (Math.Abs(stretchRatio - 1.0f) < 0.01f && Math.Abs(consonantStretch - 1.0f) < 0.01f && pitchBend.Count == 0)
        {
            // ストレッチなし、ピッチベンドなし → 単純なピッチシフト
            output = SynthesizeWithPitch(chunk, fs, srcF0, targetF0, formantFollow, modulation, useModPlus, actualThop, overlapMs);
        }
        else
        {
            // 子音部velocity + 伸縮部ストレッチ（フラグでElastic/PitchMark/標準を切り替え）
            if (usePitchMarkStretch)
            {
                // Aフラグ: ピッチマーク駆動ストレッチ（連続音最適化、Approach A）
                output = ApplyPitchMarkDrivenStretch(chunk, fs, srcF0, targetF0, 
                                                      consonantFrames, consonantStretch, stretchRatio, pitchBend, tempo, breathiness, genderFactor, formantFollow, actualThop, useOversampling, useChunkRPS, modulation, useModPlus, overlapMs, unvoicedAttenuation, spectralTilt, pitchShiftNoise, growlStrength, glottalClosure, useGlottalAutoEstimate);
            }
            else if (useElasticStretch)
            {
                // eフラグ: Elasticストレッチ（トランジェント保護）
                output = SynthesizeWithElasticStretch(chunk, fs, srcF0, targetF0, 
                                                        consonantFrames, consonantStretch, stretchRatio, pitchBend, tempo, breathiness, genderFactor, formantFollow, actualThop, useOversampling, useChunkRPS, modulation, useModPlus, overlapMs, unvoicedAttenuation, spectralTilt, pitchShiftNoise, growlStrength, glottalClosure, useGlottalAutoEstimate);
            }
            else
            {
                // 標準: 均一な補間ベースのストレッチ
                output = SynthesizeWithConsonantAndStretch(chunk, fs, srcF0, targetF0, 
                                                            consonantFrames, consonantStretch, stretchRatio, pitchBend, tempo, breathiness, genderFactor, formantFollow, actualThop, useOversampling, useChunkRPS, modulation, useModPlus, overlapMs, unvoicedAttenuation, spectralTilt, pitchShiftNoise, growlStrength, glottalClosure, useGlottalAutoEstimate, useFixedAmplitudeRatio);
            }
        }
        
        // ★Cフラグ: 子音原音ブレンド（LLSM再合成で劣化する子音を原音PCMで置換）
        // LLSMの調和+ノイズモデルは母音には最適だが、子音（破裂音・摩擦音・トランジェント）は
        // 分析→合成の過程で情報が失われやすい。原音PCMを子音部分に使うことで鮮明さを復元する。
        if (consonantBlendStrength > 0 && consonantSamples > 0 && output.Length > 0)
        {
            // velocity適用後の子音長（サンプル数）
            // 合成メソッドと一致させるため、consonantFrames ベースで計算
            int nhopLocal = (int)(actualThop * fs);
            int dstConsonantFrames_blend = (int)(consonantFrames * consonantStretch);
            int dstConsonantSamples = dstConsonantFrames_blend * nhopLocal;
            
            if (dstConsonantSamples > 0 && dstConsonantSamples < output.Length)
            {
                float blendRatio = consonantBlendStrength / 100.0f;
                
                // 原音子音PCMを取得（元のconsonantSamplesベース）
                int srcConsonantLen = Math.Min(consonantSamples, segment.Length);
                float[] originalConsonant = new float[srcConsonantLen];
                Array.Copy(segment, 0, originalConsonant, 0, srcConsonantLen);
                
                // velocity適用（子音の時間伸縮）: 線形補間リサンプル
                float[] adjustedConsonant;
                if (Math.Abs(consonantStretch - 1.0f) > 0.01f)
                {
                    adjustedConsonant = new float[dstConsonantSamples];
                    for (int i = 0; i < dstConsonantSamples; i++)
                    {
                        float srcPos = (float)i / dstConsonantSamples * srcConsonantLen;
                        int idx = (int)srcPos;
                        float frac = srcPos - idx;
                        if (idx + 1 < srcConsonantLen)
                            adjustedConsonant[i] = originalConsonant[idx] * (1 - frac) + originalConsonant[idx + 1] * frac;
                        else if (idx < srcConsonantLen)
                            adjustedConsonant[i] = originalConsonant[idx];
                    }
                }
                else
                {
                    adjustedConsonant = originalConsonant;
                    dstConsonantSamples = srcConsonantLen;
                }
                
                int blendEnd = Math.Min(dstConsonantSamples, output.Length);
                
                // クロスフェード長: 子音→母音境界で滑らかに遷移
                // velocity に比例させる（速い子音には短いクロスフェード）
                float crossfadeMs = Math.Max(5.0f, 15.0f * consonantStretch);
                int crossfadeSamples = (int)(crossfadeMs / 1000.0f * fs);
                crossfadeSamples = Math.Min(crossfadeSamples, blendEnd / 2);  // ブレンド領域の半分以下
                int crossfadeStart = Math.Max(0, blendEnd - crossfadeSamples);
                
                // 分解出力が利用可能かチェック
                bool hasDecomposed = (_lastSynthSin != null && _lastSynthNoise != null &&
                                      _lastSynthSin.Length >= blendEnd && _lastSynthNoise.Length >= blendEnd);
                
                if (hasDecomposed)
                {
                    // ★分解出力モード: ノイズ成分のみ原音で置換
                    // LLSMの正弦波成分（ピッチシフト済み）はそのまま残し、
                    // ノイズ成分（摩擦音・息・破裂音など）だけを原音に差し替える
                    // → ピッチ混入なしで子音のノイズ質感を改善
                    // → 有声子音（な行等）でもノイズ部分だけを置換できる
                    
                    // RMSボリュームマッチング（ノイズ成分 vs 原音で計算）
                    float origRms = 0, noiseRms = 0;
                    for (int i = 0; i < blendEnd; i++)
                    {
                        float origSample = (i < adjustedConsonant.Length) ? adjustedConsonant[i] : 0;
                        origRms += origSample * origSample;
                        noiseRms += _lastSynthNoise[i] * _lastSynthNoise[i];
                    }
                    origRms = MathF.Sqrt(origRms / blendEnd);
                    noiseRms = MathF.Sqrt(noiseRms / blendEnd);
                    // ノイズ成分のRMSに合わせて原音をスケール
                    float volumeMatch = (origRms > 0.001f) ? noiseRms / origRms : 1.0f;
                    volumeMatch = Math.Clamp(volumeMatch, 0.1f, 5.0f);
                    
                    for (int i = 0; i < blendEnd; i++)
                    {
                        if (i >= adjustedConsonant.Length) break;
                        
                        // ブレンド強度計算（velocity追従: 出力サンプル→原音フレーム位置に変換）
                        float srcSamplePos = (dstConsonantSamples > 0) 
                            ? (float)i / dstConsonantSamples * srcConsonantLen 
                            : i;
                        int frameIdx = (int)(srcSamplePos / nhopLocal);
                        frameIdx = Math.Max(0, Math.Min(frameIdx, f0.Length - 1));
                        bool isUnvoiced = (frameIdx >= 0 && frameIdx < f0.Length && f0[frameIdx] <= 0);
                        
                        // 有声フレーム: output をそのまま維持（分解・再合成による浮動小数点誤差を回避）
                        if (!isUnvoiced) continue;
                        
                        float localBlend = blendRatio;
                        
                        if (i >= crossfadeStart)
                        {
                            float t = (float)(i - crossfadeStart) / crossfadeSamples;
                            float fade = 0.5f * (1.0f - MathF.Cos(MathF.PI * t));
                            localBlend *= (1.0f - fade);
                        }
                        
                        float origNoise = adjustedConsonant[i] * volumeMatch;
                        
                        // ノイズ成分のみ差し替え: output = y_sin + lerp(y_noise, originalNoise, blend)
                        float blendedNoise = _lastSynthNoise[i] * (1.0f - localBlend) + origNoise * localBlend;
                        output[i] = _lastSynthSin[i] + blendedNoise;
                    }
                    
                    Console.WriteLine($"  [ConsonantBlend] Decomposed mode C{consonantBlendStrength}: noise-only replacement, {dstConsonantSamples} samples ({dstConsonantSamples / (float)fs * 1000:F1}ms), volMatch={volumeMatch:F2}");
                }
                else
                {
                    // フォールバック: 分解出力が利用不可の場合、従来のPCMブレンド
                    float origRms = 0, llsmRms = 0;
                    for (int i = 0; i < blendEnd; i++)
                    {
                        float origSample = (i < adjustedConsonant.Length) ? adjustedConsonant[i] : 0;
                        origRms += origSample * origSample;
                        llsmRms += output[i] * output[i];
                    }
                    origRms = MathF.Sqrt(origRms / blendEnd);
                    llsmRms = MathF.Sqrt(llsmRms / blendEnd);
                    float volumeMatch = (origRms > 0.001f) ? llsmRms / origRms : 1.0f;
                    volumeMatch = Math.Clamp(volumeMatch, 0.3f, 3.0f);
                    
                    for (int i = 0; i < blendEnd; i++)
                    {
                        if (i >= adjustedConsonant.Length) break;
                        
                        float origSample = adjustedConsonant[i] * volumeMatch;
                        float srcSamplePos = (dstConsonantSamples > 0) 
                            ? (float)i / dstConsonantSamples * srcConsonantLen 
                            : i;
                        int frameIdx = (int)(srcSamplePos / nhopLocal);
                        frameIdx = Math.Max(0, Math.Min(frameIdx, f0.Length - 1));
                        bool isUnvoiced = (frameIdx >= 0 && frameIdx < f0.Length && f0[frameIdx] <= 0);
                        
                        float localBlend = isUnvoiced ? blendRatio : 0.0f;
                        
                        if (i >= crossfadeStart)
                        {
                            float t = (float)(i - crossfadeStart) / crossfadeSamples;
                            float fade = 0.5f * (1.0f - MathF.Cos(MathF.PI * t));
                            localBlend *= (1.0f - fade);
                        }
                        
                        output[i] = output[i] * (1.0f - localBlend) + origSample * localBlend;
                    }
                    
                    Console.WriteLine($"  [ConsonantBlend] Fallback mode C{consonantBlendStrength}: {dstConsonantSamples} samples ({dstConsonantSamples / (float)fs * 1000:F1}ms), volMatch={volumeMatch:F2}");
                }
            }
        }
        
        // 分解出力バッファのクリーンアップ
        _needDecomposedOutput = false;
        _lastSynthSin = null;
        _lastSynthNoise = null;
        
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
        
        // 音量範囲調整：UTAUのミキサーでオーバーラップ加算されるため控えめに
        // ノート単体で-3dB程度に抑えて、合成時のクリップを防止
        const float minPeak = 0.25f;  // -12dB: これより小さい音声は引き上げる
        const float maxPeak = 0.70f;  // -3dB: これより大きい音声は抑える
        
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
        
        // Neural Vocoder適用（Nフラグ）
        if (neuralVocoderStrength > 0)
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
                    // 注意: Neural Vocoder出力は非常に強いため、極めて控えめにブレンド
                    float blendRatio = neuralVocoderStrength / 10000.0f; // 1/100に減衰（N100で1%）
                    int minLen = Math.Min(output.Length, enhanced.Length);
                    for (int i = 0; i < minLen; i++)
                    {
                        output[i] = output[i] * (1 - blendRatio) + enhanced[i] * blendRatio;
                    }
                    Console.WriteLine($"  [NeuralVocoder] Enhancement complete (blend={blendRatio:F4}, strength={neuralVocoderStrength})");
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
    /// ピッチマークを抽出（F0情報からピッチ周期の境界点を検出）
    /// </summary>
    static List<PitchMark> ExtractPitchMarks(ChunkHandle chunk, float thop)
    {
        var marks = new List<PitchMark>();
        int nfrm = Llsm.GetNumFrames(chunk);
        
        // 各フレームのF0を取得
        float[] f0Array = new float[nfrm];
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            f0Array[i] = Llsm.GetFrameF0(frame);
        }
        
        // ピッチマークを生成（各フレームの開始点）
        // 注意: LLSMフレームは固定間隔（thop）で並んでいる
        float accumulatedTime = 0;
        for (int i = 0; i < nfrm; i++)
        {
            float f0 = f0Array[i];
            bool isVoiced = (f0 >= 50.0f);  // 50Hz以上を有声とする（InterpolateFrameと統一）
            
            marks.Add(new PitchMark(
                time: accumulatedTime,
                frameIndex: i,
                period: thop,  // フレーム間隔はthopで固定
                isVoiced: isVoiced,
                f0: f0
            ));
            
            accumulatedTime += thop;
        }
        
        return marks;
    }
    
    /// <summary>
    /// ピッチマーク駆動ストレッチ: F0情報に基づいてフレームをピッチ周期境界に配置
    /// </summary>
    static float[] ApplyPitchMarkDrivenStretch(ChunkHandle srcChunk, int fs, float srcF0, float targetF0,
                                                int consonantFrames, float consonantStretch, float stretchRatio, 
                                                List<int> pitchBend, int tempo = 120, int breathiness = 50, 
                                                int genderFactor = 0, int formantFollow = 100, float actualThop = 0.005f, 
                                                bool useOversampling = false, bool useChunkRPS = false, int modulation = 100, 
                                                bool useModPlus = false, float overlapMs = 0, int unvoicedAttenuation = 0, 
                                                int spectralTilt = 0, bool pitchShiftNoise = false, int growlStrength = 0, 
                                                int glottalClosure = 50, bool useGlottalAutoEstimate = false)
    {
        Console.WriteLine($"[PitchMark] Using pitch-mark driven stretch (Approach A)");
        
        // ピッチベンドのラップアラウンドを補正
        var unwrappedPb = UnwrapPitchBend(pitchBend);
        
        int srcNfrm = Llsm.GetNumFrames(srcChunk);
        
        // ★Lフラグ: 声門パラメータ自動推定（Layer1変換前に実行）
        if (useGlottalAutoEstimate)
        {
            EstimateAndApplyGlottalParameters(srcChunk, srcNfrm, fs);
        }
        
        // Layer1に変換（tolayer1を先に実行してからphasepropagate(-1)を呼ぶ：demo-stretch.c準拠）
        // NFFT=16384: 2xオーバーサンプリング分析に対応（nspec=8193→トリム後4097）
        Llsm.ChunkToLayer1(srcChunk, 16384);
        DownsampleChunkSpectrum(srcChunk, fs);
        
        // 逆位相伝播（編集前に位相依存性を除去）
        Llsm.ChunkPhasePropagate(srcChunk, -1);
        
        // ピッチマークを抽出
        var srcMarks = ExtractPitchMarks(srcChunk, actualThop);
        Console.WriteLine($"[PitchMark] Extracted {srcMarks.Count} pitch marks from source");
        
        // 出力フレーム数を計算（標準的なストレッチロジック）
        int stretchableFrames = srcNfrm - consonantFrames;
        int dstConsonantFrames = (int)Math.Round(consonantFrames * consonantStretch);
        int dstStretchedFrames = (int)Math.Round(stretchableFrames * stretchRatio);
        int dstNfrm = dstConsonantFrames + dstStretchedFrames;
        
        Console.WriteLine($"[PitchMark] Consonant frames: {consonantFrames} -> {dstConsonantFrames} (stretch={consonantStretch:F3})");
        Console.WriteLine($"[PitchMark] Vowel frames: {stretchableFrames} -> {dstStretchedFrames} (stretch={stretchRatio:F3})");
        Console.WriteLine($"[PitchMark] Total output frames: {dstNfrm}");
        
        // ピッチベンドを出力フレーム数に合わせて補間
        float[] interpolatedPb = new float[dstNfrm];
        if (unwrappedPb.Count > 0)
        {
            float outputDurationMs = dstNfrm * actualThop * 1000f;
            float utauPbIntervalMs = 60.0f / 96.0f / tempo * 1000f;
            int utauPbLength = (int)(outputDurationMs / utauPbIntervalMs) + 1;
            
            float[] paddedPb = new float[utauPbLength];
            for (int j = 0; j < utauPbLength; j++)
            {
                if (j < unwrappedPb.Count)
                    paddedPb[j] = unwrappedPb[j];
                else
                    paddedPb[j] = 0;
            }
            
            float[] utauT = new float[utauPbLength];
            double utauPbIntervalMsD = (double)utauPbIntervalMs;
            for (int j = 0; j < utauPbLength; j++)
            {
                utauT[j] = (float)(j * utauPbIntervalMsD);
            }
            
            float[] outputT = new float[dstNfrm];
            double actualThopMsD = (double)actualThop * 1000.0;
            for (int j = 0; j < dstNfrm; j++)
            {
                outputT[j] = (float)(j * actualThopMsD);
            }
            
            interpolatedPb = InterpolatePitchBend(utauT, outputT, paddedPb);
        }
        
        // 注: tolayer1 + phasepropagate(-1) は既に上で実行済み（重複実行を排除）
        
        // 新しいchunkを構築（Layer1状態）
        Console.WriteLine($"[PitchMark] Building chunk from pitch marks (dstNfrm={dstNfrm})...");
        var dstChunk = BuildChunkFromPitchMarks(srcChunk, srcMarks, dstNfrm, dstConsonantFrames, 
                                                 consonantStretch, stretchRatio, srcF0, targetF0, interpolatedPb, 
                                                 breathiness, genderFactor, formantFollow, 
                                                 unvoicedAttenuation, spectralTilt, pitchShiftNoise, 
                                                 growlStrength, glottalClosure);
        
        Console.WriteLine($"[PitchMark] Chunk built successfully");
        
        // T flag: スペクトル傾斜（Layer1状態で実行）
        if (spectralTilt != 0)
        {
            Console.WriteLine($"[PitchMark] Applying spectral tilt: {spectralTilt:+0;-0}dB/oct (T flag)");
            ApplySpectralTilt(dstChunk, spectralTilt, fs);
        }
        
        // U flag: 無声音減衰（Layer1状態で実行）
        if (unvoicedAttenuation > 0)
        {
            Console.WriteLine($"[PitchMark] Applying unvoiced attenuation: -{unvoicedAttenuation}dB (U flag)");
            Llsm.AttenuateUnvoiced(dstChunk, uvDb: -unvoicedAttenuation);
        }
        
        // Layer0に変換（F0/フォルマント変更を音響合成に反映）
        Console.WriteLine($"[PitchMark] Converting to Layer0 for synthesis...");
        Llsm.ChunkToLayer0(dstChunk);
        
        // Chunk-level RPS（位相連続性の確保）
        Console.WriteLine($"[PitchMark] Chunk-level RPS (Layer0, mandatory for quality)...");
        NativeLLSM.llsm_chunk_phasesync_rps(dstChunk.DangerousGetHandle(), 0);
        
        // 順方向位相伝播
        Console.WriteLine($"[PitchMark] Applying forward phase propagation...");
        Llsm.ChunkPhasePropagate(dstChunk, 1);
        
        // 合成
        Console.WriteLine($"[PitchMark] Synthesizing output...");
        var sopts = Llsm.CreateSynthesisOptions(fs);
        var outputHandle = Llsm.Synthesize(sopts, dstChunk);
        Console.WriteLine($"[PitchMark] Synthesis completed");
        float[] output;
        if (_needDecomposedOutput)
        {
            var (y, ySin, yNoise) = Llsm.ReadOutputDecomposed(outputHandle);
            output = y;
            _lastSynthSin = ySin;
            _lastSynthNoise = yNoise;
        }
        else
        {
            output = Llsm.ReadOutput(outputHandle);
        }
        
        // クリーンアップ
        outputHandle.Dispose();
        sopts.Dispose();
        
        return output;
    }
    
    /// <summary>
    /// ピッチマークからchunkを構築（有声部はコピー、無声部は補間）
    /// </summary>
    static ChunkHandle BuildChunkFromPitchMarks(ChunkHandle srcChunk, List<PitchMark> srcMarks, 
                                                 int dstNfrm, int dstConsonantFrames, 
                                                 float consonantStretch, float stretchRatio, float srcF0, float targetF0,
                                                 float[] interpolatedPb,
                                                 int breathiness, int genderFactor, int formantFollow,
                                                 int unvoicedAttenuation, int spectralTilt, 
                                                 bool pitchShiftNoise, int growlStrength, int glottalClosure)
    {
        int srcNfrm = srcMarks.Count;
        Console.WriteLine($"[BuildChunk] srcNfrm={srcNfrm}, dstNfrm={dstNfrm}");
        
        // 新しいchunkを作成（標準実装と同じパターン）
        var conf = Llsm.GetConf(srcChunk);
        var confCopy = Llsm.CopyContainer(conf);
        var nfrmPtr = NativeLLSM.llsm_container_get(confCopy.Ptr, NativeLLSM.LLSM_CONF_NFRM);
        Marshal.WriteInt32(nfrmPtr, dstNfrm);
        
        var dstChunk = Llsm.CreateChunk(confCopy, 0);
        Console.WriteLine($"[BuildChunk] Created new destination chunk");
        
        // 各出力フレームを生成（補間なしでストレッチマッピングをテスト）
        int stretchableFrames = srcNfrm - dstConsonantFrames;
        int dstStretchedFrames = dstNfrm - dstConsonantFrames;
        
        Console.WriteLine($"[BuildChunk] Applying stretch mapping with frame interpolation");
        
        // ソースのスペクトル傾斜から適応的tiltCoeffを算出
        float srcSpectralSlope = MeasureAverageSpectralSlope(srcChunk);
        float adaptiveTiltCoeff = ComputeAdaptiveTiltCoeff(srcSpectralSlope);
        
        for (int i = 0; i < dstNfrm; i++)
        {
            // ソースフレーム位置を計算
            float srcPosFloat;
            if (i < dstConsonantFrames)
            {
                // 子音部: consonantStretch
                float consonantPos = (float)i / dstConsonantFrames;
                srcPosFloat = consonantPos * dstConsonantFrames / consonantStretch;
            }
            else
            {
                // 母音部: stretchRatio
                float stretchedPos = (float)(i - dstConsonantFrames) / dstStretchedFrames;
                srcPosFloat = dstConsonantFrames / consonantStretch + stretchedPos * stretchableFrames;
            }
            
            // フレーム補間（秋間/線形）
            int idx1 = (int)Math.Floor(srcPosFloat);
            float frac = srcPosFloat - idx1;
            idx1 = Math.Max(0, Math.Min(idx1, srcNfrm - 1));
            int idx2 = Math.Min(idx1 + 1, srcNfrm - 1);
            
            IntPtr frameCopy;
            if (frac < 0.001f || idx1 == idx2)
            {
                // ほぼ整数位置 — コピーのみ
                var srcFrame = Llsm.GetFrame(srcChunk, idx1);
                frameCopy = Llsm.CopyFrame(srcFrame);
            }
            else
            {
                // 4点秋間補間を試行（両側に制御点がある場合）
                int idx0 = Math.Max(0, idx1 - 1);
                int idx3 = Math.Min(idx2 + 1, srcNfrm - 1);
                
                if (idx0 < idx1 && idx3 > idx2)
                {
                    // 4点利用可能 — 秋間補間
                    var f0ref = Llsm.GetFrame(srcChunk, idx0);
                    var f1ref = Llsm.GetFrame(srcChunk, idx1);
                    var f2ref = Llsm.GetFrame(srcChunk, idx2);
                    var f3ref = Llsm.GetFrame(srcChunk, idx3);
                    frameCopy = InterpolateFrameCubic(f0ref, f1ref, f2ref, f3ref, frac, i);
                }
                else
                {
                    // エッジケース — 線形補間
                    var f1ref = Llsm.GetFrame(srcChunk, idx1);
                    var f2ref = Llsm.GetFrame(srcChunk, idx2);
                    frameCopy = InterpolateFrame(f1ref, f2ref, frac, i);
                }
                
                // PSDRESをランダム近傍フレームからコピー（demo-stretch.c準拠）
                AttachRandomNearbyPsdres(frameCopy, srcChunk, idx1, srcNfrm);
            }
            
            int srcIdx = (int)Math.Round(srcPosFloat);
            srcIdx = Math.Max(0, Math.Min(srcIdx, srcNfrm - 1));
            
            // ソースF0を取得（0の場合は平均F0を使用）
            float srcF0Val = srcMarks[srcIdx].F0;
            if (srcF0Val < 50.0f)  // 無声フレームの場合
            {
                srcF0Val = srcF0;  // 平均/中央値F0を使用
            }
            
            // 基本ピッチ比率（ピッチベンド適用前）
            float basePitchRatio = targetF0 / srcF0Val;
            
            // ピッチベンドを適用
            float pitchBendCents = (i < interpolatedPb.Length) ? interpolatedPb[i] : 0;
            float pitchBendRatio = (float)Math.Pow(2.0, pitchBendCents / 1200.0);
            float dstF0 = targetF0 * pitchBendRatio;
            
            // 出力フレームの有声/無声は dstF0 で判定（srcではなく）
            bool dstIsVoiced = dstF0 >= 50.0f;
            
            // フレーム参照を作成（補間済みフレームを使用）
            var frameRef = new ContainerRef(frameCopy);
            
            // F0を設定（ピッチシフトを適用）
            Llsm.SetFrameF0(frameRef, dstIsVoiced ? dstF0 : 0);
            
            // ★振幅補正（ピッチシフトに伴うエネルギー感度 + 周波数依存傾斜補正）
            if (dstIsVoiced && Math.Abs(basePitchRatio - 1.0f) > 0.001f)
            {
                float amplitudeCompensation = -20.0f * MathF.Log10(basePitchRatio);
                float tiltCoeff = adaptiveTiltCoeff;
                float spectralTiltCompensation = -tiltCoeff * MathF.Log2(basePitchRatio);
                var vtmagnPtr = NativeLLSM.llsm_container_get(frameCopy, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr != IntPtr.Zero)
                {
                    int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                    float[] vtmagn = new float[nspec];
                    Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                    for (int j = 0; j < nspec; j++)
                    {
                        float normalizedFreq = (float)j / nspec;
                        vtmagn[j] = Math.Max(vtmagn[j] + amplitudeCompensation + spectralTiltCompensation * normalizedFreq, -80.0f);
                    }
                    Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
                }
            }
            
            // フォルマント追従（Fフラグ）
            // 出力が有声の場合のみ適用
            // 注意: basePitchRatioを使用（ピッチベンド含まない基本比率）
            // F=100（デフォルト）: 何もしない = フォルマントがピッチに追従（自然な動作）
            // F<100: 部分的にフォルマントを固定
            if (dstIsVoiced && formantFollow != 100 && Math.Abs(basePitchRatio - 1.0f) > 0.05f)
            {
                ApplyAdaptiveFormantToFparray(frameRef, basePitchRatio, formantFollow);
            }
            
            // パラメータフラグを適用（B/g）
            if (breathiness != 50)
            {
                ApplyBreathiness(frameRef, breathiness);
            }
            
            if (genderFactor != 0)
            {
                ApplyGenderFactor(frameRef, genderFactor);
            }
            
            Llsm.SetFrame(dstChunk, i, frameCopy);
        }
        
        Console.WriteLine($"[BuildChunk] All frames processed successfully (B/g/F flags enabled)");
        return dstChunk;
    }
    
    /// <summary>
    /// HNR（倍音対雑音比）改善: フレームごとのSNRを計算し、
    /// 低SNRフレームのノイズ成分を適応的に低減する。
    /// Layer0状態（tolayer1前）で実行する必要がある。
    /// Dフラグ（D1-D100）で強度を制御。
    /// </summary>
    static void EnhanceHNR(ChunkHandle chunk, int nfrm, int fs, int strength)
    {
        float maxReductionDb = 6.0f * (strength / 100.0f);  // D100で最大6dB低減
        int processedFrames = 0;
        
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            
            // HM（倍音）情報を取得
            IntPtr hmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_HM);
            if (hmPtr == IntPtr.Zero) continue;
            var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
            if (hm.nhar <= 0) continue;  // 無声フレームはスキップ
            
            // NM（ノイズ）情報を取得
            IntPtr nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
            if (nmPtr == IntPtr.Zero) continue;
            var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
            if (nm.npsd <= 0) continue;
            
            // 倍音パワーを計算（線形振幅 → パワー → dB）
            float[] ampl = new float[hm.nhar];
            Marshal.Copy(hm.ampl, ampl, 0, hm.nhar);
            double harmonicPower = 0;
            for (int h = 0; h < hm.nhar; h++)
            {
                harmonicPower += ampl[h] * ampl[h];
            }
            if (harmonicPower <= 1e-20) continue;  // 無音フレームはスキップ
            double harmonicPowerDb = 10.0 * Math.Log10(harmonicPower);
            
            // ノイズPSD平均を計算（すでにdB）
            float[] psd = new float[nm.npsd];
            Marshal.Copy(nm.psd, psd, 0, nm.npsd);
            double noiseMeanDb = 0;
            for (int j = 0; j < nm.npsd; j++)
            {
                noiseMeanDb += psd[j];
            }
            noiseMeanDb /= nm.npsd;
            
            // フレームSNR = 倍音パワー(dB) - ノイズ平均(dB)
            double snrDb = harmonicPowerDb - noiseMeanDb;
            
            // 適応的低減量の計算
            // SNR < 10dB: 最大低減、SNR > 30dB: 低減なし
            double adaptiveFactor;
            if (snrDb <= 10.0)
                adaptiveFactor = 1.0;
            else if (snrDb >= 30.0)
                adaptiveFactor = 0.0;
            else
                adaptiveFactor = 1.0 - (snrDb - 10.0) / 20.0;
            
            if (adaptiveFactor <= 0.01) continue;  // ほぼ低減不要
            
            double reductionDb = maxReductionDb * adaptiveFactor;
            
            // 周波数依存プロファイルでNM PSDを低減
            // 低周波は控えめ（0.3倍）、高周波ほど強く（1.0倍）
            for (int j = 0; j < nm.npsd; j++)
            {
                double freqRatio = (double)j / Math.Max(1, nm.npsd - 1);
                double freqWeight = 0.3 + 0.7 * freqRatio;
                psd[j] -= (float)(reductionDb * freqWeight);
            }
            
            // 更新したPSDを書き戻し
            Marshal.Copy(psd, 0, nm.psd, nm.npsd);
            processedFrames++;
        }
        
        Console.WriteLine($"[HNR] Enhanced {processedFrames}/{nfrm} frames (max reduction: {maxReductionDb:F1}dB)");
    }
    
    /// <summary>
    /// ピッチ誤推定などによるNM PSDの突発的な異常値（リーケージスパイク）を除去するメディアンフィルタ
    /// </summary>
    static void ApplyMedianFilterToNmPsd(ChunkHandle chunk, int nfrm)
    {
        if (nfrm < 3) return;
        
        // 全フレームのNM PSDを取得して2次元配列化
        float[][] psds = new float[nfrm][];
        int npsd_global = -1;
        
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            IntPtr nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
            if (nmPtr != IntPtr.Zero)
            {
                var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                if (nm.npsd > 0)
                {
                    if (npsd_global == -1) npsd_global = nm.npsd;
                    psds[i] = new float[nm.npsd];
                    Marshal.Copy(nm.psd, psds[i], 0, nm.npsd);
                }
            }
        }
        
        if (npsd_global <= 0) return;
        
        // ±1フレーム（タップ数3）のメディアンフィルタ
        int fixedCount = 0;
        for (int i = 1; i < nfrm - 1; i++)
        {
            if (psds[i] == null || psds[i - 1] == null || psds[i + 1] == null) continue;
            if (psds[i].Length != npsd_global || psds[i - 1].Length != npsd_global || psds[i + 1].Length != npsd_global) continue;
            
            // 全ビンメディアンフィルタ（F0誤推定によるリーケージを全帯域で平滑化）
            bool modified = false;
            float[] filteredPsd = new float[npsd_global];
            float[] buf = new float[3];
            
            for (int j = 0; j < npsd_global; j++)
            {
                buf[0] = psds[i - 1][j];
                buf[1] = psds[i][j];
                buf[2] = psds[i + 1][j];
                Array.Sort(buf);
                
                // メディアンを取得
                float med = buf[1];
                filteredPsd[j] = med;
                
                // 元の値がメディアンから大きく離脱している（6dB以上スパイク）場合のみ補正
                // NM PSDのスパイク（周波数の漏れ）が合成時にジリジリ・ブブッの原因になるため、上方向のスパイクを重点抑制
                if (psds[i][j] > med + 6.0f)
                {
                    modified = true;
                }
            }
            
            if (modified)
            {
                var frame = Llsm.GetFrame(chunk, i);
                IntPtr nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
                var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                Marshal.Copy(filteredPsd, 0, nm.psd, nm.npsd);
                fixedCount++;
            }
        }
        
        if (fixedCount > 0)
        {
            Console.WriteLine($"[Noise Model] Applied median filter to PSD on {fixedCount} frames (suppressed spikes)");
        }
    }
    
    /// <summary>
    /// 無声→有声（または有声→無声）のV/UV境界におけるNM PSDのリーケージを抑制
    /// 境界付近のF0推定は本質的に不安定であり、大量の実信号がNM PSDに漏れ込むのを防ぐ
    /// </summary>
    static void ConstrainBoundaryNmPsd(ChunkHandle chunk, int nfrm)
    {
        // 1. 各フレームの有声/無声判定
        bool[] isVoiced = new bool[nfrm];
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            float f0 = Llsm.GetFrameF0(frame);
            IntPtr hmPtr = Llsm.GetFrameHM(frame);
            if (f0 > 0 && hmPtr != IntPtr.Zero)
            {
                var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
                isVoiced[i] = (hm.nhar > 0);
            }
        }
        
        // 2. 境界フレームの特定とNM PSDの安全な値へのクランプ
        int constrainedCount = 0;
        for (int i = 1; i < nfrm - 1; i++)
        {
            // V/UV遷移を検出
            bool isTransition = (isVoiced[i - 1] != isVoiced[i]) || (isVoiced[i] != isVoiced[i + 1]);
            
            if (isTransition)
            {
                var frame = Llsm.GetFrame(chunk, i);
                IntPtr nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
                if (nmPtr != IntPtr.Zero)
                {
                    var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                    if (nm.npsd > 0)
                    {
                        float[] psd = new float[nm.npsd];
                        Marshal.Copy(nm.psd, psd, 0, nm.npsd);
                        
                        // 境界フレームのNM PSDが異常に高い場合はペナルティをかける
                        bool modified = false;
                        for (int j = 0; j < nm.npsd; j++)
                        {
                            float freqRatio = (float)j / (nm.npsd - 1);
                            
                            // 閾値: -30dB(低域) 〜 -10dB(高域)
                            float threshold = -30.0f + (freqRatio * 20.0f);
                            
                            if (psd[j] > threshold)
                            {
                                psd[j] = threshold + (psd[j] - threshold) * 0.5f;
                                modified = true;
                            }
                        }
                        
                        if (modified)
                        {
                            Marshal.Copy(psd, 0, nm.psd, nm.npsd);
                            constrainedCount++;
                        }
                    }
                }
            }
        }
        
        if (constrainedCount > 0)
        {
            Console.WriteLine($"[Noise Model] Constrained NM PSD on {constrainedCount} V/UV boundary frames");
        }
    }
    
    /// <summary>
    /// Q1: V/UV境界付近の有声フレームでeenv（ノイズエンベロープ）振幅を漸減フェード
    /// llsm_synthesize_noise_envelopeで有声(eenv.nhar>0)→無声(nhar=0)の急変が起きる際の
    /// ポップノイズを防止。OLAで部分的に緩和されるが、eenv側でも事前に減衰させることで改善。
    /// </summary>
    static void FadeEenvAtVuvBoundaries(ChunkHandle chunk, float[] dstF0, int dstNfrm)
    {
        const int fadeRadius = 1;  // ±1フレームのみ（子音保護のため縮小）
        
        // 各フレームのV/UV境界からの距離を計算
        int[] distToBoundary = new int[dstNfrm];
        Array.Fill(distToBoundary, fadeRadius + 1);
        
        for (int i = 1; i < dstNfrm; i++)
        {
            bool voicedPrev = dstF0[i - 1] > 0;
            bool voicedCurr = dstF0[i] > 0;
            if (voicedPrev != voicedCurr)
            {
                // i-1とiの間が境界
                for (int k = 0; k <= fadeRadius; k++)
                {
                    if (i - 1 - k >= 0)
                        distToBoundary[i - 1 - k] = Math.Min(distToBoundary[i - 1 - k], k);
                    if (i + k < dstNfrm)
                        distToBoundary[i + k] = Math.Min(distToBoundary[i + k], k);
                }
            }
        }
        
        int fadedCount = 0;
        for (int i = 0; i < dstNfrm; i++)
        {
            if (dstF0[i] <= 0) continue; // 無声フレーム: C合成でnhar=0なのでフェード不要
            if (distToBoundary[i] >= fadeRadius) continue;
            
            // fadeScale: dist=0 → 0.5（最小50%、子音ノイズ保護）
            float fadeScale = Math.Max(0.5f, (float)(distToBoundary[i] + 1) / (fadeRadius + 1));
            
            var frame = Llsm.GetFrame(chunk, i);
            var nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
            if (nmPtr == IntPtr.Zero) continue;
            
            var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
            if (nm.eenv == IntPtr.Zero || nm.nchannel <= 0) continue;
            
            IntPtr[] eenvPtrs = new IntPtr[nm.nchannel];
            Marshal.Copy(nm.eenv, eenvPtrs, 0, nm.nchannel);
            
            for (int ch = 0; ch < nm.nchannel; ch++)
            {
                if (eenvPtrs[ch] == IntPtr.Zero) continue;
                var eenv = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvPtrs[ch]);
                if (eenv.nhar <= 0) continue;
                
                float[] ampl = new float[eenv.nhar];
                Marshal.Copy(eenv.ampl, ampl, 0, eenv.nhar);
                for (int h = 0; h < eenv.nhar; h++)
                    ampl[h] *= fadeScale;
                Marshal.Copy(ampl, 0, eenv.ampl, eenv.nhar);
            }
            fadedCount++;
        }
        
        if (fadedCount > 0)
            Console.WriteLine($"[Noise Model] Faded eenv on {fadedCount} V/UV boundary frames");
    }
    
    /// <summary>
    /// 声門パラメータ自動推定：原音からRdパラメータを推定し、全フレームに適用
    /// Lフラグで有効化
    /// </summary>
    static void EstimateAndApplyGlottalParameters(ChunkHandle chunk, int nfrm, int fs)
    {
        // Rdパラメータの範囲を作成（0.3～2.7の範囲、0.01刻み）
        int nparam = 241;
        float[] rdParams = new float[nparam];
        for (int i = 0; i < nparam; i++)
        {
            rdParams[i] = 0.3f + i * 0.01f;
        }

        // キャッシュされた声門モデルを作成（最大100ハーモニクスまで）
        int maxHar = 100;
        IntPtr glottalModel = IntPtr.Zero;
        try
        {
            glottalModel = NativeLLSM.llsm_create_cached_glottal_model(rdParams, nparam, maxHar);
            if (glottalModel == IntPtr.Zero)
            {
                Console.WriteLine("  [GlottalEstimate] Failed to create glottal model");
                return;
            }

            // 各フレームの声門パラメータを推定（per-frame）
            float[] rdPerFrame = new float[nfrm];
            for (int i = 0; i < nfrm; i++) rdPerFrame[i] = float.NaN;
            
            int voicedCount = 0;
            for (int i = 0; i < nfrm; i++)
            {
                var framePtr = Llsm.GetFrame(chunk, i);
                float f0 = Llsm.GetFrameF0(framePtr);
                
                if (f0 < 50 || f0 > 800)
                    continue;

                IntPtr hmPtr = Llsm.GetFrameHM(framePtr);
                if (hmPtr == IntPtr.Zero)
                    continue;

                int nhar = Llsm.GetHMNHar(hmPtr);
                if (nhar <= 0)
                    continue;

                float[] ampl = Llsm.GetHMAmpl(hmPtr, nhar);
                float estimatedRd = NativeLLSM.llsm_spectral_glottal_fitting(ampl, nhar, glottalModel);
                rdPerFrame[i] = Math.Clamp(estimatedRd, 0.3f, 3.0f);
                voicedCount++;
            }

            // 推定結果を移動平均で平滑化（窓幅5フレーム=25ms @5ms hop）
            if (voicedCount > 0)
            {
                const int smoothWindow = 3;
                float[] rdSmoothed = new float[nfrm];
                for (int i = 0; i < nfrm; i++) rdSmoothed[i] = float.NaN;
                
                for (int i = 0; i < nfrm; i++)
                {
                    if (float.IsNaN(rdPerFrame[i])) continue;
                    
                    float sum = 0;
                    int count = 0;
                    for (int j = Math.Max(0, i - smoothWindow / 2); j <= Math.Min(nfrm - 1, i + smoothWindow / 2); j++)
                    {
                        if (!float.IsNaN(rdPerFrame[j]))
                        {
                            sum += rdPerFrame[j];
                            count++;
                        }
                    }
                    rdSmoothed[i] = Math.Clamp(sum / count, 0.3f, 3.0f);
                }
                
                var validRd = rdSmoothed.Where(x => !float.IsNaN(x)).ToList();
                float meanRd = validRd.Average();
                float stdRd = validRd.Count > 1
                    ? MathF.Sqrt(validRd.Select(x => (x - meanRd) * (x - meanRd)).Average())
                    : 0;
                
                Console.WriteLine($"  [GlottalEstimate] Analyzed {voicedCount}/{nfrm} frames (per-frame + smoothed)");
                Console.WriteLine($"  [GlottalEstimate] Rd parameter: mean={meanRd:F3}, std={stdRd:F3}");
                Console.WriteLine($"  [GlottalEstimate] Range: [{validRd.Min():F3}, {validRd.Max():F3}]");

                // 平滑化されたRd値をフレームごとに適用
                for (int i = 0; i < nfrm; i++)
                {
                    if (float.IsNaN(rdSmoothed[i])) continue;
                    var framePtr = Llsm.GetFrame(chunk, i);
                    Llsm.SetFrameRd(framePtr, rdSmoothed[i]);
                }
            }
            else
            {
                Console.WriteLine("  [GlottalEstimate] No voiced frames found for estimation");
            }
        }
        finally
        {
            if (glottalModel != IntPtr.Zero)
            {
                NativeLLSM.llsm_delete_cached_glottal_model(glottalModel);
            }
        }
    }
    
    /// <summary>
    /// 子音部velocity + 伸縮部ストレッチして合成
    /// </summary>
    static float[] SynthesizeWithConsonantAndStretch(ChunkHandle srcChunk, int fs, float srcF0, float targetF0,
                                                      int consonantFrames, float consonantStretch, float stretchRatio, List<int> pitchBend, int tempo = 120, int breathiness = 50, int genderFactor = 0, int formantFollow = 100, float actualThop = 0.005f, bool useOversampling = false, bool useChunkRPS = false, int modulation = 100, bool useModPlus = false, float overlapMs = 0, int unvoicedAttenuation = 0, int spectralTilt = 0, bool pitchShiftNoise = false, int growlStrength = 0, int glottalClosure = 50, bool useGlottalAutoEstimate = false, bool useFixedAmplitudeRatio = false)
    {
        float basePitchRatio = targetF0 / srcF0;
        
        // ピッチベンドのラップアラウンドを補正
        var unwrappedPb = UnwrapPitchBend(pitchBend);
        
        int srcNfrm = Llsm.GetNumFrames(srcChunk);
        
        // ★Lフラグ: 声門パラメータ自動推定（Layer1変換前、HMフレームが存在する状態で実行）
        if (useGlottalAutoEstimate)
        {
            EstimateAndApplyGlottalParameters(srcChunk, srcNfrm, fs);
        }
        
        // Layer1 に変換（NFFT=16384: 2xオーバーサンプリング分析対応）
        Llsm.ChunkToLayer1(srcChunk, 16384);
        DownsampleChunkSpectrum(srcChunk, fs);
        
        // 逆位相伝播（編集前に位相依存性を除去）
        Llsm.ChunkPhasePropagate(srcChunk, -1);
        int stretchableFrames = srcNfrm - consonantFrames;
        
        // ★末尾アーティファクト対策: ソース末尾の不安定フレームを除外
        // 音源の最終数フレームはF0推定が不安定（発声の切れ目、無音遷移）で、
        // ストレッチで引き伸ばされると「ブブッ」というノイズになる。
        // 伸縮部のマッピング範囲を末尾3フレーム手前で打ち切る。
        int tailMargin = Math.Min(3, stretchableFrames / 4);  // 最大3フレーム、全体の25%以下
        int effectiveStretchableFrames = Math.Max(1, stretchableFrames - tailMargin);
        
        // 子音部はvelocityでストレッチ、伸縮部は lengthReq でストレッチ
        int dstConsonantFrames = (int)Math.Round(consonantFrames * consonantStretch);
        int dstStretchedFrames = (int)Math.Round(stretchableFrames * stretchRatio);
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
            
            // UTAUタイミング配列を生成（double精度で累積誤差を防止）
            float[] utauT = new float[utauPbLength];
            double utauPbIntervalMsD = (double)utauPbIntervalMs;
            for (int j = 0; j < utauPbLength; j++)
            {
                utauT[j] = (float)(j * utauPbIntervalMsD);
            }
            
            // 出力タイミング配列を生成（double精度で累積誤差を防止）
            float[] outputT = new float[dstNfrm];
            double actualThopMsD = (double)actualThop * 1000.0;
            for (int j = 0; j < dstNfrm; j++)
            {
                outputT[j] = (float)(j * actualThopMsD);
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
        
        // ★トランジェント検出：LLSMのスペクトルデータからトランジェントフレームを特定
        // スペクトルフラックス（VTMAGN差分）を使い、子音アタックを検出
        // VCV構造でも正しく動作（VC領域の母音部分は定常状態、子音部分はトランジェント）
        List<ContainerRef> srcFrameList = new List<ContainerRef>();
        for (int i = 0; i < srcNfrm; i++)
        {
            srcFrameList.Add(Llsm.GetFrame(srcChunk, i));
        }
        bool[] isTransient = DetectTransientFrames(srcFrameList, fs);
        
        // dstF0配列を準備（位相同期処理用）
        float[] dstF0 = new float[dstNfrm];
        
        // ソースのスペクトル傾斜から適応的なtiltCoeffを算出
        float srcSpectralSlope = MeasureAverageSpectralSlope(srcChunk);
        float adaptiveTiltCoeff = ComputeAdaptiveTiltCoeff(srcSpectralSlope);
        Console.WriteLine($"[TimeStretch] Adaptive tilt: slope={srcSpectralSlope:F1}dB/nf -> tiltCoeff={adaptiveTiltCoeff:F2}dB/oct");
        
        for (int i = 0; i < dstNfrm; i++)
        {
            // Q3: ストレッチ位置をdouble精度で計算（長いノートでの累積誤差を防止）
            float srcPosFloat;
            if (i < dstConsonantFrames)
            {
                // 子音部: velocity でストレッチ
                srcPosFloat = (float)((double)i / dstConsonantFrames * consonantFrames);
            }
            else
            {
                // 伸縮部: ストレッチ（末尾マージン分を除外）
                srcPosFloat = (float)((double)consonantFrames + (double)(i - dstConsonantFrames) / dstStretchedFrames * effectiveStretchableFrames);
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
            
            // ★トランジェント補間判定：元フレームのトランジェント状態を確認
            // トランジェント = スペクトル変化が大きい（子音アタック、破裂音など）
            // 定常状態 = スペクトルが安定（母音、子音内VC母音部分など）
            bool isSrcTransient1 = (srcIdx1 < isTransient.Length && isTransient[srcIdx1]);
            bool isSrcTransient2 = (srcIdx2 < isTransient.Length && isTransient[srcIdx2]);
            bool isTransientRegion = isSrcTransient1 || isSrcTransient2;
            
            // フレーム補間（トランジェント保護 + Chunk-level RPS）
            IntPtr newFramePtr;
            if (srcIdx1 == srcIdx2 || ratio < 0.01f)
            {
                // 整数位置またはratioがほぼ0: そのままコピー
                var srcFrame = Llsm.GetFrame(srcChunk, srcIdx1);
                var frameCopy = Llsm.CopyFrame(srcFrame);
                newFramePtr = frameCopy;
                AttachRandomNearbyPsdres(newFramePtr, srcChunk, srcIdx1, srcNfrm);
            }
            else if (ratio > 0.99f)
            {
                // ratioがほぼ1: そのままコピー
                var srcFrame = Llsm.GetFrame(srcChunk, srcIdx2);
                var frameCopy = Llsm.CopyFrame(srcFrame);
                newFramePtr = frameCopy;
                AttachRandomNearbyPsdres(newFramePtr, srcChunk, srcIdx2, srcNfrm);
            }
            else
            {
                // ★トランジェント保護：音響特徴に基づいて補間方式を選択
                // - トランジェント領域（子音アタック）→ 最近傍補間（鋭さを保護）
                // - 定常状態領域（母音、クロスフェード）→ キュービック補間（滑らかに）
                // VCV構造でも正しく動作：VC領域の母音は定常状態、子音アタックはトランジェント
                
                if (isTransientRegion)
                {
                    // トランジェント領域：最近傍補間でアタックを保護
                    int nearestIdx = (ratio < 0.5f) ? srcIdx1 : srcIdx2;
                    var nearestFrame = Llsm.GetFrame(srcChunk, nearestIdx);
                    newFramePtr = Llsm.CopyFrame(nearestFrame);
                    AttachRandomNearbyPsdres(newFramePtr, srcChunk, nearestIdx, srcNfrm);
                }
                else
                {
                    // 定常状態領域：キュービック補間で滑らかに
                    // キュービック補間が可能かチェック（前後1フレームずつ必要）
                    bool canUseCubic = (srcIdx1 > 0) && (srcIdx2 < srcNfrm - 1);
                    
                    if (canUseCubic)
                    {
                        // 4点Catmull-Rom補間（高品質なスペクトル補間）
                        // ★品質改善：RPSなしで直接補間、Chunk-levelで後処理
                        var srcFrame0 = Llsm.GetFrame(srcChunk, srcIdx1 - 1);
                        var srcFrame1 = Llsm.GetFrame(srcChunk, srcIdx1);
                        var srcFrame2 = Llsm.GetFrame(srcChunk, srcIdx2);
                        var srcFrame3 = Llsm.GetFrame(srcChunk, srcIdx2 + 1);
                        
                        // 元フレームを直接使用（ContainerRefからContainerRefへ渡す）
                        newFramePtr = InterpolateFrameCubic(srcFrame0, srcFrame1, srcFrame2, srcFrame3, ratio, i);
                    }
                    else
                    {
                        // 境界付近では線形補間にフォールバック（前後フレームが不足）
                        // ★品質改善：RPSなしで直接補間
                        var srcFrame1 = Llsm.GetFrame(srcChunk, srcIdx1);
                        var srcFrame2 = Llsm.GetFrame(srcChunk, srcIdx2);
                        
                        newFramePtr = InterpolateFrame(srcFrame1, srcFrame2, ratio, i);
                    }
                    
                    // PSDRESをランダム近傍フレームからコピー（demo-stretch.c準拠）
                    AttachRandomNearbyPsdres(newFramePtr, srcChunk, srcIdx1, srcNfrm);
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
            
            // 息成分（breathiness）を適用
            if (breathiness != 50)
            {
                ApplyBreathiness(newFrameRef, breathiness);
            }
            
            if (originalF0 > 0)
            {
                // ★全有声フレームにピッチシフト適用（子音/母音を区別しない）
                // M+フラグ: ノート境界でmodulationをフェード
                float dynamicMod = useModPlus 
                    ? GetDynamicModulation(i, dstNfrm, modulation, actualThop, overlapMs)
                    : modulation;
                
                // modulationを適用して原音のピッチ揺らぎを調整（対数空間）
                // ★オクターブジャンプ保護: deviation を ±6半音にクランプ
                float logDeviation = MathF.Log(originalF0 / srcF0);
                float maxLogDev = 6.0f * MathF.Log(2.0f) / 12.0f;  // 6半音
                logDeviation = Math.Clamp(logDeviation, -maxLogDev, maxLogDev);
                float modRatio = dynamicMod / 100.0f;
                float adjustedSourceF0 = srcF0 * MathF.Exp(logDeviation * modRatio);
                
                float pitchRatio = targetF0 / srcF0;
                newF0 = adjustedSourceF0 * pitchRatio;  // 揺らぎを保持してシフト
                
                // ピッチベンドを適用（補間済み配列から取得）
                if (interpolatedPb.Length > 0 && i < interpolatedPb.Length)
                {
                    newF0 *= (float)Math.Pow(2, interpolatedPb[i] / 1200.0);
                }
                
                // ★振幅補正（フラグで切替可能）
                float amplitudeCompensation;
                if (useFixedAmplitudeRatio)
                {
                    amplitudeCompensation = -20.0f * MathF.Log10(pitchShiftRatio);
                }
                else
                {
                    float safeOriginalF0 = (originalF0 >= 50.0f && originalF0 <= 1200.0f)
                        ? originalF0 : srcF0;
                    float actualFrameRatio = Math.Clamp(newF0 / safeOriginalF0, 0.1f, 10.0f);
                    amplitudeCompensation = -20.0f * MathF.Log10(actualFrameRatio);
                }
                var vtmagnPtr = NativeLLSM.llsm_container_get(newFramePtr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr != IntPtr.Zero)
                {
                    int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                    float[] vtmagn = new float[nspec];
                    Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                    float pitchRatioForTilt = useFixedAmplitudeRatio
                        ? pitchShiftRatio
                        : Math.Clamp(newF0 / ((originalF0 >= 50.0f && originalF0 <= 1200.0f) ? originalF0 : srcF0), 0.1f, 10.0f);
                    float tiltCoeff = adaptiveTiltCoeff;
                    float spectralTiltCompensation = -tiltCoeff * MathF.Log2(pitchRatioForTilt);
                    for (int j = 0; j < nspec; j++)
                    {
                        float normalizedFreq = (float)j / nspec;
                        float freqDepCorrection = amplitudeCompensation + spectralTiltCompensation * normalizedFreq;
                        vtmagn[j] = Math.Max(vtmagn[j] + freqDepCorrection, -80.0f);
                    }
                    Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
                }
                
                // 新しいF0をフレームに設定
                var newF0Ptr = NativeLLSM.llsm_create_fp(newF0);
                NativeLLSM.llsm_container_attach_(newFramePtr, NativeLLSM.LLSM_FRAME_F0,
                    newF0Ptr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
                
                // ★P1: ピッチ上昇時のVSPHSE倍音数拡張
                // llsm_frame_tolayer0 で nhar = min(nhar, fnyq/f0) により
                // F0が上がると倍音数が減少 → 高域スペクトル情報消失を防止
                if (newF0 > originalF0 * 1.1f)
                {
                    ExtendVsphseForPitchShift(newFramePtr, originalF0, newF0, fs);
                }
                
                // dstF0配列に保存（位相同期処理用）
                dstF0[i] = newF0;
            }
            else
            {
                // F0がない場合は0を設定
                dstF0[i] = 0;
            }
            
            // ジェンダーファクター（gフラグ）: Layer1での処理
            if (genderFactor != 0)
            {
                ApplyGenderFactor(newFrameRef, genderFactor);
            }
            
            // 適応的フォルマント処理（Fフラグ）: Layer1での処理
            // pitchShiftRatioのみを使用（ピッチベンドの影響を除外）
            if (formantFollow != 100)
            {
                ApplyAdaptiveFormantToFparray(newFrameRef, pitchShiftRatio, formantFollow);
            }
            
            // PSDRESジッタリング後処理は不要 — ランダム近傍コピー方式で解決済み
            
            Llsm.SetFrame(dstChunk, i, newFramePtr);
        }
        
        // ★適応カルマンフィルタによるF0スパイク除去（一時的に無効化）
        // ビブラートや自然な抑揚は追従し、推定エラーのみ自動除去。
        // ApplyAdaptiveKalmanF0(dstF0, dstNfrm, actualThop, dstChunk);
        
        // Q1: V/UV境界でのノイズエンベロープ(eenv)振幅をフェードし、ポップノイズを防止
        FadeEenvAtVuvBoundaries(dstChunk, dstF0, dstNfrm);
        
        // Tフラグ: スペクトル傾斜調整（Layer1状態で実行）
        // フォルマント保存型：チルト適用後にフォルマントピークのレベルを復元
        if (spectralTilt != 0)
        {
            Console.WriteLine($"[Synthesis] Spectral tilt: {spectralTilt:+0;-0}dB/oct (T flag, Layer1, formant-preserving)");
            
            for (int i = 0; i < dstNfrm; i++)
            {
                var frame = Llsm.GetFrame(dstChunk, i);
                float f0 = Llsm.GetFrameF0(frame);
                if (f0 <= 0) continue;  // 無声音はスキップ
                
                // VTMAGNを取得（対数振幅スペクトル）
                var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr == IntPtr.Zero) continue;
                
                int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                float[] vtmagn = new float[nspec];
                Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                
                // Step 1: フォルマントピーク検出（元のスペクトルから）
                List<(int index, float magnitude)> formantPeaks = DetectFormantPeaks(vtmagn, fs, nspec);
                
                // Step 2: スペクトル傾斜を適用
                for (int j = 0; j < nspec; j++)
                {
                    float freqKhz = (float)j / nspec * (fs / 2000.0f);
                    float tiltDb = spectralTilt * MathF.Log2(MathF.Max(freqKhz, 0.1f));
                    vtmagn[j] += tiltDb;
                }
                
                // Step 3: フォルマントピーク周辺の振幅を元のレベルに復元（適応的強度）
                foreach (var (peakIdx, origMagnitude) in formantPeaks)
                {
                    float currentMagnitude = vtmagn[peakIdx];
                    float correction = origMagnitude - currentMagnitude;
                    
                    // 復元強度を方向に応じて調整
                    // マイナス方向（暗く）: 50%復元でこもりを軽減
                    // プラス方向（明るく）: 70%復元でパリパリ感を軽減（高域の過度な強調を抑制）
                    float strength = spectralTilt < 0 ? 0.5f : 0.7f;
                    correction *= strength;
                    
                    // ガウシアンウィンドウで周辺に復元を適用（帯域幅 = サンプリングレートに応じた適応的幅）
                    int bandwidthBins = Math.Max(3, nspec / 100);  // 全体の1%程度の帯域幅
                    for (int k = -bandwidthBins; k <= bandwidthBins; k++)
                    {
                        int idx = peakIdx + k;
                        if (idx >= 0 && idx < nspec)
                        {
                            // ガウシアンウェイト（σ = bandwidth/2）
                            float sigma = bandwidthBins / 2.0f;
                            float weight = MathF.Exp(-(k * k) / (2 * sigma * sigma));
                            vtmagn[idx] += correction * weight;
                        }
                    }
                }
                
                // VTMAGNフロアクランプ（demo-stretch.c準拠: -80dB）
                for (int j = 0; j < nspec; j++)
                    vtmagn[j] = Math.Max(vtmagn[j], -80.0f);
                Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
            }
        }

        
        // Kフラグ: 声門閉鎖係数調整（Layer1状態で実行）
        // GFMのRgパラメータを直接編集
        if (glottalClosure != 50)
        {
            Console.WriteLine($"[Synthesis] Adjusting glottal closure: K{glottalClosure} (Layer1)");
            
            for (int i = 0; i < dstNfrm; i++)
            {
                var frame = Llsm.GetFrame(dstChunk, i);
                float f0 = Llsm.GetFrameF0(frame);
                if (f0 <= 0) continue;  // 無声音はスキップ
                
                // Rdパラメータを取得・編集
                var rdPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_RD);
                if (rdPtr == IntPtr.Zero) continue;
                
                // Rdの標準値は約0.8～2.5
                // K0（息漏れ声）→ Rd = 2.5（緩い声門閉鎖）
                // K50（標準）→ Rd = 1.0（変更なし = identity）
                // K100（硬い声）→ Rd = 0.3（強い声門閉鎖）
                // 区分線形マッピング: K=50が中立点になるよう設計
                float rdScale;
                if (glottalClosure <= 50)
                    rdScale = 2.5f - (glottalClosure / 50.0f) * 1.5f;  // 2.5 → 1.0
                else
                    rdScale = 1.0f - ((glottalClosure - 50) / 50.0f) * 0.7f;  // 1.0 → 0.3
                
                float currentRd = Marshal.PtrToStructure<float>(rdPtr);
                float newRd = currentRd * rdScale;
                
                Marshal.StructureToPtr(newRd, rdPtr, false);
            }
        }
        
        // Uフラグ: 無声音減衰（Layer1状態で実行）
        // F0=0フレームのVTMAGNを減衰し、無声子音の明瞭度を向上
        if (unvoicedAttenuation > 0)
        {
            Console.WriteLine($"[Synthesis] Unvoiced attenuation: -{unvoicedAttenuation}dB (U flag, Layer1)");
            Llsm.AttenuateUnvoiced(dstChunk, uvDb: -unvoicedAttenuation);
        }
        
        // Gフラグ: グロウル効果（Layer1状態、PBP synthesis使用）
        // 声帯の不規則な振動をシミュレート：サブハーモニクス、ジッター、声門波形変調
        if (growlStrength > 0)
        {
            Console.WriteLine($"[Synthesis] Growl effect: {growlStrength}% (G flag, PBP synthesis)");
            
            // Backward phase propagationは削除
            // ピッチベンド適用後はすでに位相が整っているため、
            // ここでbackward (-1) を実行するとピッチベンドがリセットされてしまう
            
            // グロウルコールバック初期化（GCから保護）
            var growlState = new GrowlEffectState(growlStrength);
            GCHandle stateHandle = GCHandle.Alloc(growlState);
            IntPtr statePtr = GCHandle.ToIntPtr(stateHandle);
            
            // コールバックデリゲートを作成してGC保護
            _growlCallback = GrowlEffectCallback;
            IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(_growlCallback);
            
            // 各フレームにPBP効果を設定
            for (int i = 0; i < dstNfrm; i++)
            {
                var frame = Llsm.GetFrame(dstChunk, i);
                float f0 = Llsm.GetFrameF0(frame);
                
                // 有声音フレーム（F0>0）のみにグロウルを適用
                if (f0 > 0)
                {
                    // HMをNULLに設定（Layer1から直接合成するため）
                    // 有声フレームだけをLayer1に変換
                    NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_HM, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    
                    // PBPSYNフラグを有効化
                    IntPtr pbpsynPtr = NativeLLSM.llsm_create_int(1);
                    NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_PBPSYN,
                        pbpsynPtr, Marshal.GetFunctionPointerForDelegate(_deleteInt), 
                        Marshal.GetFunctionPointerForDelegate(_copyInt));
                    
                    // PBP効果オブジェクトを作成
                    IntPtr pbpeffPtr = NativeLLSM.llsm_create_pbpeffect(callbackPtr, statePtr);
                    NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_PBPEFF,
                        pbpeffPtr, Marshal.GetFunctionPointerForDelegate(_deletePbpEffect),
                        Marshal.GetFunctionPointerForDelegate(_copyPbpEffect));
                }
                // F0=0の無声フレームはHMをそのまま残す（Layer0で合成）
            }
            
            // 注意: stateHandleは合成完了まで保持される必要があるため、
            // ここではFreeしない（関数終了後に自動的にGC対象になる）
            
            // Phase propagation（位相伝搬）- test-pbpeffects.c line 133
            // PBP効果適用後、位相の一貫性を保つために必要
            NativeLLSM.llsm_chunk_phasepropagate(dstChunk.DangerousGetHandle(), 1);
        }
        
        // Layer0 変換（Gフラグ使用時はスキップ）
        // PBP synthesisはLayer1状態で実行する必要があるため、
        // グロウル効果使用時はLayer0に変換せずにLayer1のまま合成
        bool useLayer1Synthesis = (growlStrength > 0);
        
        if (!useLayer1Synthesis)
        {
            Console.WriteLine($"[Synthesis] Converting {dstNfrm} frames to Layer0...");
            Llsm.ChunkToLayer0(dstChunk);
            
            // Chunk-level RPS（位相連続性の確保）
            Console.WriteLine($"[Synthesis] Chunk-level RPS (Layer0, mandatory for quality)...");
            NativeLLSM.llsm_chunk_phasesync_rps(dstChunk.DangerousGetHandle(), 0);
            
            // 順方向位相伝播（Layer0）
            Console.WriteLine($"[Synthesis] Phase propagate (Layer0)...");
            Llsm.ChunkPhasePropagate(dstChunk, +1);
        }
        else
        {
            Console.WriteLine($"[Synthesis] Keeping Layer1 for PBP synthesis (Growl effect active)...");
            // Layer1状態を維持、合成時にuse_l1=1を設定
            // Phase propagationはGrowl効果内で既に1回実行済み
        }
        
        // オーバーサンプリング（Oフラグ有効時のみ）
        float[] oversampledOutput;
        if (useOversampling)
        {
            // 4倍オーバーサンプリングで合成（ノイズ削減のため高サンプリングレート使用）
            int oversampleRate = 4;
            int synthesisFs = fs * oversampleRate;
            Console.WriteLine($"[Synthesis] Oversampling enabled: {oversampleRate}x ({synthesisFs}Hz)");
            using var sopt = Llsm.CreateSynthesisOptions(synthesisFs);
            
            // Gフラグ使用時はuse_l1=1を設定（Layer1状態で合成）
            if (useLayer1Synthesis)
            {
                unsafe
                {
                    var soptPtr = (NativeLLSM.llsm_soptions*)sopt.DangerousGetHandle().ToPointer();
                    soptPtr->use_l1 = 1;
                    Console.WriteLine($"[Synthesis] use_l1=1 (Layer1 synthesis for PBP/Growl)");
                }
            }
            
            using var output = Llsm.Synthesize(sopt, dstChunk);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Synthesis] llsm_synthesize succeeded");
            Console.ResetColor();
            
            if (_needDecomposedOutput)
            {
                var (y, ySin, yNoise) = Llsm.ReadOutputDecomposed(output);
                oversampledOutput = y;
                _lastSynthSin = Downsample(ySin, oversampleRate);
                _lastSynthNoise = Downsample(yNoise, oversampleRate);
            }
            else
            {
                oversampledOutput = Llsm.ReadOutput(output);
            }
            
            // ダウンサンプリング
            Console.WriteLine($"[Synthesis] Downsampling from {synthesisFs}Hz to {fs}Hz...");
            return Downsample(oversampledOutput, oversampleRate);
        }
        else
        {
            // 直接44.1kHzで合成（オーバーサンプリングなし）
            Console.WriteLine($"[Synthesis] Direct synthesis at {fs}Hz (no oversampling)");
            using var sopt = Llsm.CreateSynthesisOptions(fs);
            
            // Gフラグ使用時はuse_l1=1を設定（Layer1状態で合成）
            if (useLayer1Synthesis)
            {
                unsafe
                {
                    var soptPtr = (NativeLLSM.llsm_soptions*)sopt.DangerousGetHandle().ToPointer();
                    soptPtr->use_l1 = 1;
                    Console.WriteLine($"[Synthesis] use_l1=1 (Layer1 synthesis for PBP/Growl)");
                }
            }
            
            using var output = Llsm.Synthesize(sopt, dstChunk);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Synthesis] llsm_synthesize succeeded");
            Console.ResetColor();
            
            if (_needDecomposedOutput)
            {
                var (y, ySin, yNoise) = Llsm.ReadOutputDecomposed(output);
                _lastSynthSin = ySin;
                _lastSynthNoise = yNoise;
                return y;
            }
            return Llsm.ReadOutput(output);
        }
    }
    
    /// <summary>
    /// Élastiqueスタイルの適応的タイムストレッチ合成
    /// トランジェント（子音など急激な変化）を検出し、保護しながら母音部を伸縮
    /// </summary>
    static float[] SynthesizeWithElasticStretch(ChunkHandle srcChunk, int fs, float srcF0, float targetF0,
                                                 int consonantFrames, float consonantStretch, float stretchRatio, List<int> pitchBend, int tempo = 120, int breathiness = 50, int genderFactor = 0, int formantFollow = 100, float actualThop = 0.005f, bool useOversampling = false, bool useChunkRPS = false, int modulation = 100, bool useModPlus = false, float overlapMs = 0, int unvoicedAttenuation = 0, int spectralTilt = 0, bool pitchShiftNoise = false, int growlStrength = 0, int glottalClosure = 50, bool useGlottalAutoEstimate = false)
    {
        float basePitchRatio = targetF0 / srcF0;
        
        // ピッチベンドのラップアラウンドを補正
        var unwrappedPb = UnwrapPitchBend(pitchBend);
        
        int srcNfrm = Llsm.GetNumFrames(srcChunk);
        
        // ★Lフラグ: 声門パラメータ自動推定（Layer1変換前に実行）
        if (useGlottalAutoEstimate)
        {
            EstimateAndApplyGlottalParameters(srcChunk, srcNfrm, fs);
        }
        
        // Layer1 に変換（NFFT=16384: 2xオーバーサンプリング分析対応）
        Llsm.ChunkToLayer1(srcChunk, 16384);
        DownsampleChunkSpectrum(srcChunk, fs);
        
        // 逆位相伝播（編集前に位相依存性を除去）
        Llsm.ChunkPhasePropagate(srcChunk, -1);
        
        // 全フレームを取得
        var srcFrames = new List<ContainerRef>();
        for (int i = 0; i < srcNfrm; i++)
        {
            srcFrames.Add(Llsm.GetFrame(srcChunk, i));
        }
        
        // トランジェント検出
        bool[] isTransient = DetectTransientFrames(srcFrames, fs);
        
        // ★子音部内の定常状態フレーム（VC母音）を特定
        Console.WriteLine($"[Elastic Synthesis] Detecting steady-state frames in consonant region...");
        int steadyStateCount = 0;
        for (int i = 0; i < Math.Min(consonantFrames, isTransient.Length); i++)
        {
            if (!isTransient[i]) steadyStateCount++;
        }
        Console.WriteLine($"[Elastic Synthesis] Consonant region: {consonantFrames} frames, {steadyStateCount} steady-state (protected from interpolation)");
        
        // 子音部はvelocityでストレッチ、伸縮部は lengthReq でストレッチ
        int stretchableFrames = srcNfrm - consonantFrames;
        int dstConsonantFrames = (int)Math.Round(consonantFrames * consonantStretch);
        int dstStretchedFrames = (int)Math.Round(stretchableFrames * stretchRatio);
        int dstNfrm = dstConsonantFrames + dstStretchedFrames;
        
        // ピッチベンドを出力フレーム数に合わせて補間
        float[] interpolatedPb = new float[dstNfrm];
        if (unwrappedPb.Count > 0)
        {
            float outputDurationMs = dstNfrm * actualThop * 1000f;
            float utauPbIntervalMs = 60.0f / 96.0f / tempo * 1000f;
            int utauPbLength = (int)(outputDurationMs / utauPbIntervalMs) + 1;
            
            float[] paddedPb = new float[utauPbLength];
            for (int j = 0; j < utauPbLength; j++)
            {
                if (j < unwrappedPb.Count)
                    paddedPb[j] = unwrappedPb[j];
                else
                    paddedPb[j] = 0;
            }
            
            float[] utauT = new float[utauPbLength];
            double utauPbIntervalMsD = (double)utauPbIntervalMs;
            for (int j = 0; j < utauPbLength; j++)
            {
                utauT[j] = (float)(j * utauPbIntervalMsD);
            }
            
            float[] outputT = new float[dstNfrm];
            double actualThopMsD = (double)actualThop * 1000.0;
            for (int j = 0; j < dstNfrm; j++)
            {
                outputT[j] = (float)(j * actualThopMsD);
            }
            
            interpolatedPb = InterpolatePitchBend(utauT, outputT, paddedPb);
        }
        
        // 新しい chunk を作成
        var conf = Llsm.GetConf(srcChunk);
        var confCopy = Llsm.CopyContainer(conf);
        var nfrmPtr = NativeLLSM.llsm_container_get(confCopy.Ptr, NativeLLSM.LLSM_CONF_NFRM);
        Marshal.WriteInt32(nfrmPtr, dstNfrm);
        
        var dstChunk = Llsm.CreateChunk(confCopy, 0);
        
        float[] dstF0 = new float[dstNfrm];
        
        // ソースのスペクトル傾斜から適応的tiltCoeffを算出
        float srcSpectralSlope = MeasureAverageSpectralSlope(srcChunk);
        float adaptiveTiltCoeff = ComputeAdaptiveTiltCoeff(srcSpectralSlope);
        
        // フレーム生成
        for (int i = 0; i < dstNfrm; i++)
        {
            bool isConsonant = i < dstConsonantFrames;
            
            // 元フレームインデックスを計算
            float srcIdx;
            if (isConsonant)
            {
                srcIdx = consonantStretch > 0 ? i / consonantStretch : 0;
            }
            else
            {
                int stretchFrameIdx = i - dstConsonantFrames;
                srcIdx = stretchRatio > 0 
                    ? consonantFrames + stretchFrameIdx / stretchRatio 
                    : consonantFrames;
            }
            
            srcIdx = Math.Max(0, Math.Min(srcNfrm - 1, srcIdx));
            int srcIdx0 = (int)Math.Floor(srcIdx);
            int srcIdx1 = Math.Min(srcIdx0 + 1, srcNfrm - 1);
            float ratio = srcIdx - srcIdx0;
            
            // ★子音部定常状態判定（VC母音保護）
            int nearestSrc = (int)Math.Round(srcIdx);
            nearestSrc = Math.Max(0, Math.Min(nearestSrc, srcNfrm - 1));
            bool isSteadyStateInConsonant = isConsonant && 
                                            (nearestSrc < isTransient.Length) && 
                                            !isTransient[nearestSrc];
            
            // トランジェント判定: 現在または隣接フレームがトランジェントなら保護
            bool isTransientRegion = isTransient[srcIdx0] || isTransient[srcIdx1];
            
            IntPtr newFramePtr;
            
            if (isSteadyStateInConsonant)
            {
                // ★VCV子音部の定常状態フレーム（VC母音）：補間なしで最近傍コピー
                var srcFrame = Llsm.GetFrame(srcChunk, nearestSrc);
                newFramePtr = Llsm.CopyFrame(srcFrame);
            }
            else if (isTransientRegion)
            {
                // トランジェントフレーム: 最近接フレームをコピー（補間なし）
                int nearestIdx = ratio < 0.5f ? srcIdx0 : srcIdx1;
                var nearestFrame = Llsm.GetFrame(srcChunk, nearestIdx);
                newFramePtr = Llsm.CopyFrame(nearestFrame);
            }
            else
            {
                // 定常フレーム: スプライン補間
                int srcIdx_1 = Math.Max(0, srcIdx0 - 1);
                int srcIdx2 = Math.Min(srcIdx1 + 1, srcNfrm - 1);
                
                var frame_1Ref = Llsm.GetFrame(srcChunk, srcIdx_1);
                var frame0Ref = Llsm.GetFrame(srcChunk, srcIdx0);
                var frame1Ref = Llsm.GetFrame(srcChunk, srcIdx1);
                var frame2Ref = Llsm.GetFrame(srcChunk, srcIdx2);
                
                bool canUseCubic = (srcIdx_1 != srcIdx0 && srcIdx2 != srcIdx1);
                
                if (canUseCubic)
                {
                    newFramePtr = InterpolateFrameCubic(frame_1Ref, frame0Ref, frame1Ref, frame2Ref, ratio, i);
                }
                else
                {
                    newFramePtr = InterpolateFrame(frame0Ref, frame1Ref, ratio, i);
                }
                
                // PSDRESをランダム近傍フレームからコピー（demo-stretch.c準拠）
                AttachRandomNearbyPsdres(newFramePtr, srcChunk, srcIdx0, srcNfrm);
            }
            
            var newFrameRef = new ContainerRef(newFramePtr);
            
            // ピッチシフトとF0設定
            float newF0 = 0;
            float originalF0 = Llsm.GetFrameF0(newFrameRef);
            
            if (originalF0 > 0)
            {
                // ★オクターブジャンプ保護: 対数空間で deviation を ±6半音にクランプ
                float logDeviation = MathF.Log(originalF0 / srcF0);
                float maxLogDev = 6.0f * MathF.Log(2.0f) / 12.0f;  // 6半音
                logDeviation = Math.Clamp(logDeviation, -maxLogDev, maxLogDev);
                float modRatio = modulation / 100.0f;
                if (useModPlus)
                {
                    modRatio = GetDynamicModulation(i, dstNfrm, modulation, actualThop, overlapMs) / 100.0f;
                }
                float adjustedSourceF0 = srcF0 * MathF.Exp(logDeviation * modRatio);
                
                float pitchRatio = targetF0 / srcF0;
                newF0 = adjustedSourceF0 * pitchRatio;
                
                if (interpolatedPb.Length > 0 && i < interpolatedPb.Length)
                {
                    float pbCents = interpolatedPb[i];
                    newF0 *= (float)Math.Pow(2, pbCents / 1200.0);
                }
                
                // P1: ピッチ上昇時にVSPHSEを外挿して高調波欠落を防止
                if (newF0 > originalF0 * 1.1f)
                {
                    ExtendVsphseForPitchShift(newFramePtr, originalF0, newF0, fs);
                }
                
                // 振幅補正（有声/無声の区別なし → 周波数依存傾斜補正付き）
                {
                    float amplitudeCompensation = -20.0f * MathF.Log10(basePitchRatio);
                    float tiltCoeff = adaptiveTiltCoeff;
                    float spectralTiltCompensation = -tiltCoeff * MathF.Log2(basePitchRatio);
                    var vtmagnPtr = NativeLLSM.llsm_container_get(newFramePtr, NativeLLSM.LLSM_FRAME_VTMAGN);
                    if (vtmagnPtr != IntPtr.Zero)
                    {
                        int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                        float[] vtmagn = new float[nspec];
                        Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                        for (int j = 0; j < nspec; j++)
                        {
                            float normalizedFreq = (float)j / nspec;
                            vtmagn[j] = Math.Max(vtmagn[j] + amplitudeCompensation + spectralTiltCompensation * normalizedFreq, -80.0f);
                        }
                        Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
                    }
                }
                
                var newF0Ptr = NativeLLSM.llsm_create_fp(newF0);
                NativeLLSM.llsm_container_attach_(newFramePtr, NativeLLSM.LLSM_FRAME_F0,
                    newF0Ptr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
                dstF0[i] = newF0;
            }
            
            // 各種フラグ処理（既存コードと同様）
            if (genderFactor != 0)
            {
                ApplyGenderFactor(newFrameRef, genderFactor);
            }
            
            if (formantFollow != 100)
            {
                ApplyAdaptiveFormantToFparray(newFrameRef, basePitchRatio, formantFollow);
            }
            
            Llsm.SetFrame(dstChunk, i, newFramePtr);
        }
        
        // ★適応カルマンフィルタによるF0スパイク除去（一時的に無効化）
        // ApplyAdaptiveKalmanF0(dstF0, dstNfrm, actualThop, dstChunk);
        
        // 以降は既存の処理と同様（完全実装）
        
        // Tフラグ: スペクトル傾斜調整
        if (spectralTilt != 0)
        {
            Console.WriteLine($"[Elastic Synthesis] Spectral tilt: {spectralTilt:+0;-0}dB/oct (T flag, Layer1)");
            for (int i = 0; i < dstNfrm; i++)
            {
                var frame = Llsm.GetFrame(dstChunk, i);
                float f0_frame = Llsm.GetFrameF0(frame);
                if (f0_frame <= 0) continue;
                
                var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr == IntPtr.Zero) continue;
                
                int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                float[] vtmagn = new float[nspec];
                Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                
                var formantPeaks = DetectFormantPeaks(vtmagn, fs, nspec);
                
                for (int j = 0; j < nspec; j++)
                {
                    float freqKhz = (float)j / nspec * (fs / 2000.0f);
                    float tiltDb = spectralTilt * MathF.Log2(MathF.Max(freqKhz, 0.1f));
                    vtmagn[j] += tiltDb;
                }
                
                foreach (var (peakIdx, origMagnitude) in formantPeaks)
                {
                    float currentMagnitude = vtmagn[peakIdx];
                    float correction = origMagnitude - currentMagnitude;
                    float strength = spectralTilt < 0 ? 0.5f : 0.7f;
                    correction *= strength;
                    
                    int bandwidthBins = Math.Max(3, nspec / 100);
                    for (int k = -bandwidthBins; k <= bandwidthBins; k++)
                    {
                        int idx = peakIdx + k;
                        if (idx >= 0 && idx < nspec)
                        {
                            float sigma = bandwidthBins / 2.0f;
                            float weight = MathF.Exp(-(k * k) / (2 * sigma * sigma));
                            vtmagn[idx] += correction * weight;
                        }
                    }
                }
                
                // VTMAGNフロアクランプ
                for (int j = 0; j < nspec; j++)
                    vtmagn[j] = Math.Max(vtmagn[j], -80.0f);
                Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
            }
        }
        
        // Kフラグ: 声門閉鎖係数調整
        if (glottalClosure != 50)
        {
            Console.WriteLine($"[Elastic Synthesis] Adjusting glottal closure: K{glottalClosure} (Layer1)");
            for (int i = 0; i < dstNfrm; i++)
            {
                var frame = Llsm.GetFrame(dstChunk, i);
                float f0_frame = Llsm.GetFrameF0(frame);
                if (f0_frame <= 0) continue;
                
                var rdPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_RD);
                if (rdPtr == IntPtr.Zero) continue;
                
                // 区分線形マッピング: K=50が中立点
                float rdScale;
                if (glottalClosure <= 50)
                    rdScale = 2.5f - (glottalClosure / 50.0f) * 1.5f;
                else
                    rdScale = 1.0f - ((glottalClosure - 50) / 50.0f) * 0.7f;
                float currentRd = Marshal.PtrToStructure<float>(rdPtr);
                float newRd = currentRd * rdScale;
                Marshal.StructureToPtr(newRd, rdPtr, false);
            }
        }
        
        // Uフラグ: 無声音減衰
        if (unvoicedAttenuation > 0)
        {
            Console.WriteLine($"[Elastic Synthesis] Unvoiced attenuation: -{unvoicedAttenuation}dB (U flag, Layer1)");
            Llsm.AttenuateUnvoiced(dstChunk, uvDb: -unvoicedAttenuation);
        }
        
        // Gフラグ: グロウル効果
        if (growlStrength > 0)
        {
            Console.WriteLine($"[Elastic Synthesis] Growl effect: {growlStrength}% (G flag, PBP synthesis)");
            var growlState = new GrowlEffectState(growlStrength);
            GCHandle stateHandle = GCHandle.Alloc(growlState);
            IntPtr statePtr = GCHandle.ToIntPtr(stateHandle);
            
            _growlCallback = GrowlEffectCallback;
            IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(_growlCallback);
            
            for (int i = 0; i < dstNfrm; i++)
            {
                var frame = Llsm.GetFrame(dstChunk, i);
                float f0_frame = Llsm.GetFrameF0(frame);
                
                if (f0_frame > 0)
                {
                    NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_HM, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    
                    IntPtr pbpsynPtr = NativeLLSM.llsm_create_int(1);
                    NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_PBPSYN,
                        pbpsynPtr, Marshal.GetFunctionPointerForDelegate(_deleteInt), 
                        Marshal.GetFunctionPointerForDelegate(_copyInt));
                    
                    IntPtr pbpeffPtr = NativeLLSM.llsm_create_pbpeffect(callbackPtr, statePtr);
                    NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_PBPEFF,
                        pbpeffPtr, Marshal.GetFunctionPointerForDelegate(_deletePbpEffect),
                        Marshal.GetFunctionPointerForDelegate(_copyPbpEffect));
                }
            }
            
            NativeLLSM.llsm_chunk_phasepropagate(dstChunk.DangerousGetHandle(), 1);
        }
        
        // Layer0変換とRPS
        bool useLayer1Synthesis = (growlStrength > 0);
        
        if (!useLayer1Synthesis)
        {
            Console.WriteLine($"[Elastic Synthesis] Converting {dstNfrm} frames to Layer0...");
            Llsm.ChunkToLayer0(dstChunk);
            
            // Chunk-level RPS（位相連続性の確保）
            Console.WriteLine($"[Elastic Synthesis] Chunk-level RPS (Layer0, mandatory for quality)...");
            NativeLLSM.llsm_chunk_phasesync_rps(dstChunk.DangerousGetHandle(), 0);
            
            Console.WriteLine($"[Elastic Synthesis] Phase propagate (Layer0)...");
            Llsm.ChunkPhasePropagate(dstChunk, +1);
        }
        else
        {
            Console.WriteLine($"[Elastic Synthesis] Keeping Layer1 for PBP synthesis (Growl effect active)...");
        }
        
        // オーバーサンプリング
        if (useOversampling)
        {
            int oversampleRate = 4;
            int synthesisFs = fs * oversampleRate;
            Console.WriteLine($"[Elastic Synthesis] Oversampling enabled: {oversampleRate}x ({synthesisFs}Hz)");
            using var sopt = Llsm.CreateSynthesisOptions(synthesisFs);
            
            if (useLayer1Synthesis)
            {
                unsafe
                {
                    var soptPtr = (NativeLLSM.llsm_soptions*)sopt.DangerousGetHandle().ToPointer();
                    soptPtr->use_l1 = 1;
                    Console.WriteLine($"[Elastic Synthesis] use_l1=1 (Layer1 synthesis for PBP/Growl)");
                }
            }
            
            using var output = Llsm.Synthesize(sopt, dstChunk);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Elastic Synthesis] llsm_synthesize succeeded");
            Console.ResetColor();
            
            var oversampledOutput = Llsm.ReadOutput(output);
            if (_needDecomposedOutput)
            {
                var (_, ySin, yNoise) = Llsm.ReadOutputDecomposed(output);
                _lastSynthSin = Downsample(ySin, oversampleRate);
                _lastSynthNoise = Downsample(yNoise, oversampleRate);
            }
            Console.WriteLine($"[Elastic Synthesis] Downsampling from {synthesisFs}Hz to {fs}Hz...");
            return Downsample(oversampledOutput, oversampleRate);
        }
        else
        {
            Console.WriteLine($"[Elastic Synthesis] Direct synthesis at {fs}Hz (no oversampling)");
            using var sopt = Llsm.CreateSynthesisOptions(fs);
            
            if (useLayer1Synthesis)
            {
                unsafe
                {
                    var soptPtr = (NativeLLSM.llsm_soptions*)sopt.DangerousGetHandle().ToPointer();
                    soptPtr->use_l1 = 1;
                    Console.WriteLine($"[Elastic Synthesis] use_l1=1 (Layer1 synthesis for PBP/Growl)");
                }
            }
            
            using var output = Llsm.Synthesize(sopt, dstChunk);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Elastic Synthesis] llsm_synthesize succeeded");
            Console.ResetColor();
            
            if (_needDecomposedOutput)
            {
                var (y, ySin, yNoise) = Llsm.ReadOutputDecomposed(output);
                _lastSynthSin = ySin;
                _lastSynthNoise = yNoise;
                return y;
            }
            return Llsm.ReadOutput(output);
        }
    }
    
    /// <summary>
    /// 適応カルマンフィルタによるF0スパイク除去（adYANGsaf論文準拠）
    /// 
    /// Kanru Hua, "Improving YANGsaf F0 Estimator with Adaptive Kalman Filter",
    /// INTERSPEECH 2017 の手法を参考に実装。
    /// 
    /// 改善点:
    /// 1. 対数F0空間（log Hz）でフィルタリング — 低音/高音で均等な感度
    /// 2. 適応プロセスノイズQ — イノベーション共分散のロバスト推定から動的更新
    /// 
    /// 注: 論文のSNRベースR推定は分析段階の特徴量を前提とするため、
    /// 合成側フレームでは適用せず、イノベーションベースのスパイク検出で代替。
    /// </summary>
    static void ApplyAdaptiveKalmanF0(float[] f0Array, int length, float thop, ChunkHandle dstChunk)
    {
        if (length < 3) return;
        
        float dt = thop;
        
        // --- パラメータ（対数F0空間） ---
        // log(Hz)単位: 1半音 ≈ log(2)/12 ≈ 0.0578, 1セント ≈ 0.000578
        
        // プロセスノイズ Q: モデル予測の不確実性（1ステップあたり）
        // ビブラート（6Hz, ±50セント）の2次微分効果 ≈ 0.0005 log/step
        float qLogF0_base = 5e-5f;     // logF0分散（~1セント/step相当）
        float qDLogF0_base = 0.05f;    // 速度変化分散（ビブラート加速度に対応）
        
        // 観測ノイズ R: 基本は小さく（観測を信頼）
        float rBase = 1e-4f;           // ~1.7セント std → 通常観測を強く信頼
        float rMax = 10.0f;            // スパイク時の最大R
        float spikeThreshold = 3.0f;   // スパイク判定閾値（半音）
        
        // 適応Qパラメータ（保守的）
        const int innovWindowSize = 15;
        float[] innovHistory = new float[innovWindowSize];
        int innovIdx = 0;
        int innovCount = 0;
        float innovVariance = qLogF0_base;
        float qAdaptRate = 0.15f;      // 控えめな適応率
        float qAdaptFloor = 0.5f;      // 適応Q下限（基本Qの50%）
        float qAdaptCeil = 4.0f;       // 適応Q上限（基本Qの400%）
        
        // --- 状態初期化 ---
        // 状態: [logF0, d(logF0)/dt]
        float stateLogF0 = 0;
        float stateDLogF0 = 0;
        bool initialized = false;
        
        // 誤差共分散行列 P（2x2、対数空間に適したスケール）
        float p00 = 0.01f, p01 = 0, p10 = 0, p11 = 1.0f;
        
        int spikeCount = 0;
        
        for (int i = 0; i < length; i++)
        {
            float observed = f0Array[i];
            bool voiced = observed >= 50.0f;
            
            // === 適応Q: イノベーション分散に基づく動的スケーリング ===
            float qScale = innovVariance / MathF.Max(qLogF0_base, 1e-10f);
            qScale = MathF.Max(qAdaptFloor, MathF.Min(qAdaptCeil, qScale));
            float qLogF0 = qLogF0_base * ((1f - qAdaptRate) + qAdaptRate * qScale);
            float qDLogF0 = qDLogF0_base * ((1f - qAdaptRate) + qAdaptRate * qScale);
            
            if (!voiced)
            {
                // 無声フレーム: 予測のみ実行（観測なし）
                if (initialized)
                {
                    stateLogF0 += stateDLogF0 * dt;
                    float newP00 = p00 + dt * (p10 + p01) + dt * dt * p11 + qLogF0;
                    float newP01 = p01 + dt * p11;
                    float newP10 = p10 + dt * p11;
                    float newP11 = p11 + qDLogF0;
                    p00 = newP00; p01 = newP01; p10 = newP10; p11 = newP11;
                }
                continue;
            }
            
            float obsLogF0 = MathF.Log(observed);
            
            if (!initialized)
            {
                stateLogF0 = obsLogF0;
                stateDLogF0 = 0;
                initialized = true;
                continue;
            }
            
            // === 予測ステップ ===
            float predLogF0 = stateLogF0 + stateDLogF0 * dt;
            float predDLogF0 = stateDLogF0;
            
            float predP00 = p00 + dt * (p10 + p01) + dt * dt * p11 + qLogF0;
            float predP01 = p01 + dt * p11;
            float predP10 = p10 + dt * p11;
            float predP11 = p11 + qDLogF0;
            
            // === イノベーション（対数空間） ===
            float innovation = obsLogF0 - predLogF0;
            float absSemitones = MathF.Abs(innovation) * 12.0f / MathF.Log(2.0f);
            
            // === イノベーション履歴の更新（適応Q用、ロバスト推定） ===
            innovHistory[innovIdx] = innovation * innovation;
            innovIdx = (innovIdx + 1) % innovWindowSize;
            innovCount = Math.Min(innovCount + 1, innovWindowSize);
            
            // トリムド平均: 上位25%を除外し外れ値の影響を制限
            float[] sorted = new float[innovCount];
            Array.Copy(innovHistory, sorted, innovCount);
            Array.Sort(sorted);
            int trimEnd = Math.Max(1, (int)(innovCount * 0.75f));
            float sumInnov = 0;
            for (int j = 0; j < trimEnd; j++) sumInnov += sorted[j];
            innovVariance = sumInnov / trimEnd;
            
            // === 観測ノイズ R: イノベーションベースの適応 ===
            float r = rBase;
            if (absSemitones > spikeThreshold)
            {
                // 閾値超過分の2乗に比例してRを増大（滑らかな遷移）
                float excess = absSemitones - spikeThreshold;
                r = rBase * (1.0f + excess * excess * 100.0f);
                r = MathF.Min(r, rMax);
                spikeCount++;
            }
            
            // === 更新ステップ ===
            float s = predP00 + r;
            float k0 = predP00 / s;
            float k1 = predP10 / s;
            
            stateLogF0 = predLogF0 + k0 * innovation;
            stateDLogF0 = predDLogF0 + k1 * innovation;
            
            p00 = (1.0f - k0) * predP00;
            p01 = (1.0f - k0) * predP01;
            p10 = predP10 - k1 * predP00;
            p11 = predP11 - k1 * predP01;
            
            // 対数空間から線形に変換してクランプ
            float filteredF0 = MathF.Exp(stateLogF0);
            filteredF0 = MathF.Max(50.0f, MathF.Min(filteredF0, 1500.0f));
            f0Array[i] = filteredF0;
        }
        
        // フィルタ後のF0をフレームに書き戻す
        for (int i = 0; i < length; i++)
        {
            if (f0Array[i] >= 50.0f)
            {
                var frame = Llsm.GetFrame(dstChunk, i);
                Llsm.SetFrameF0(frame, f0Array[i]);
            }
        }
        
        if (spikeCount > 0)
        {
            Console.WriteLine($"  [KalmanF0] Suppressed {spikeCount} F0 spikes ({length} frames, log-domain)");
        }
    }
    
    /// <summary>
    /// 2倍アップサンプリング（ゼロ挿入 + FIRローパスフィルタ）
    /// 分析時オーバーサンプリング用：倍音間のスペクトル分離を改善し、フォルマント推定精度を向上
    /// </summary>
    static float[] Upsample2x(float[] input)
    {
        int outputLength = input.Length * 2;
        
        // FIRローパスフィルタ（カットオフ = 0.5 = 元のナイキスト）
        int filterLength = 65;  // 十分なタップ数（対称FIR）
        float[] firCoeffs = CreateLowpassFIR(filterLength, 0.5f);
        int groupDelay = filterLength / 2;
        
        float[] output = new float[outputLength];
        
        for (int i = 0; i < outputLength; i++)
        {
            float sum = 0;
            for (int j = 0; j < filterLength; j++)
            {
                int srcIdx = i - groupDelay + j;
                
                // ゼロ挿入: 偶数インデックスのみ元信号値、奇数は0
                if (srcIdx >= 0 && srcIdx < outputLength)
                {
                    if (srcIdx % 2 == 0)
                    {
                        int origIdx = srcIdx / 2;
                        if (origIdx >= 0 && origIdx < input.Length)
                            sum += input[origIdx] * firCoeffs[j] * 2.0f;  // ゲイン2x（ゼロ挿入補正）
                    }
                    // 奇数インデックスは0なので加算不要
                }
            }
            output[i] = sum;
        }
        
        return output;
    }
    
    /// <summary>
    /// オーバーサンプリング分析後のチャンクを元のサンプルレートに合わせて調整
    /// VTMAGN を下半分にトリム、PSDRES/NM.psd を周波数マッピングに合わせてリサンプル
    /// conf の NSPEC, FNYQ を元の値に更新
    /// </summary>
    static void DownsampleChunkSpectrum(ChunkHandle chunk, int originalFs)
    {
        var conf = Llsm.GetConf(chunk);
        int analysisNspec = Llsm.GetConfInt(conf, NativeLLSM.LLSM_CONF_NSPEC);
        float analysisFnyq = Llsm.GetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ);
        int npsd = Llsm.GetConfInt(conf, NativeLLSM.LLSM_CONF_NPSD);
        int nfrm = Llsm.GetNumFrames(chunk);
        
        float originalFnyq = originalFs / 2.0f;
        // 元のnspec: 元のNFFT/2+1。analysisNspec = NFFT/2+1 で NFFT=16384 → analysisNspec=8193
        // 元のfs用のnspec = analysisNspec/2 + 1 ≈ (8193-1)/2 + 1 = 4097
        int originalNspec = (analysisNspec - 1) / 2 + 1;
        
        Console.WriteLine($"  [OversampledAnalysis] Downsampling chunk spectrum: nspec {analysisNspec} -> {originalNspec}, fnyq {analysisFnyq:F0} -> {originalFnyq:F0}");
        
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            float f0 = Llsm.GetFrameF0(frame);
            
            // VTMAGN トリム（下半分のみ保持 = 元のナイキスト以下）
            var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnPtr != IntPtr.Zero && f0 > 0)
            {
                float[] fullVtmagn = new float[analysisNspec];
                Marshal.Copy(vtmagnPtr, fullVtmagn, 0, analysisNspec);
                
                float[] trimmedVtmagn = new float[originalNspec];
                Array.Copy(fullVtmagn, trimmedVtmagn, originalNspec);
                
                Llsm.SetFrameVtMagn(frame, trimmedVtmagn);
            }
            
            // PSDRES リサンプル（下半分を全体にストレッチ）
            var psdresPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES);
            if (psdresPtr != IntPtr.Zero)
            {
                int psdresLen = NativeLLSM.llsm_fparray_length(psdresPtr);
                if (psdresLen > 0)
                {
                    float[] fullPsdres = new float[psdresLen];
                    Marshal.Copy(psdresPtr, fullPsdres, 0, psdresLen);
                    
                    // 下半分 (0-originalFnyq) を全体 (0-originalFnyq) にリサンプル
                    float[] resampledPsdres = ResamplePsdLowerHalf(fullPsdres, analysisFnyq, originalFnyq);
                    
                    var newArr = NativeLLSM.llsm_create_fparray(resampledPsdres.Length);
                    Marshal.Copy(resampledPsdres, 0, newArr, resampledPsdres.Length);
                    NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES,
                        newArr, Llsm.DeleteFpArrayPtr, Llsm.CopyFpArrayPtr);
                }
            }
            
            // NM.psd リサンプル（Layer0ノイズモデル）
            var nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
            if (nmPtr != IntPtr.Zero)
            {
                var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                if (nm.npsd > 0 && nm.psd != IntPtr.Zero)
                {
                    float[] fullNmPsd = new float[nm.npsd];
                    Marshal.Copy(nm.psd, fullNmPsd, 0, nm.npsd);
                    
                    float[] resampledNmPsd = ResamplePsdLowerHalf(fullNmPsd, analysisFnyq, originalFnyq);
                    Marshal.Copy(resampledNmPsd, 0, nm.psd, nm.npsd);
                }
            }
        }
        
        // conf 更新
        Llsm.SetConfInt(conf, NativeLLSM.LLSM_CONF_NSPEC, originalNspec);
        Llsm.SetConfFloat(conf, NativeLLSM.LLSM_CONF_FNYQ, originalFnyq);
    }
    
    /// <summary>
    /// PSD配列の下半分（0-originalFnyq）を全体にリサンプル
    /// 線形補間で元のfnyq範囲からoriginalFnyq範囲分だけを取り出し、元のbin数に引き伸ばす
    /// </summary>
    static float[] ResamplePsdLowerHalf(float[] psd, float analysisFnyq, float originalFnyq)
    {
        int n = psd.Length;
        float[] result = new float[n];
        float ratio = originalFnyq / analysisFnyq;  // 0.5 for 2x oversampling
        
        for (int j = 0; j < n; j++)
        {
            // 新しいbin j が表す周波数 = j * originalFnyq / (n-1)
            // 元のPSDでの対応bin = j * ratio
            float srcIdx = j * ratio;
            int idx0 = (int)srcIdx;
            int idx1 = Math.Min(idx0 + 1, n - 1);
            float frac = srcIdx - idx0;
            
            result[j] = psd[idx0] * (1 - frac) + psd[idx1] * frac;
        }
        
        return result;
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
    /// Spectral Flux を使用してトランジェント（子音など急激な変化）を検出
    /// </summary>
    /// <returns>各フレームがトランジェントかどうかの配列</returns>
    static bool[] DetectTransientFrames(List<ContainerRef> frames, int fs)
    {
        int nFrames = frames.Count;
        bool[] isTransient = new bool[nFrames];
        
        if (nFrames < 2)
            return isTransient;
        
        // 各フレームのスペクトルエネルギーを取得
        float[] spectralFlux = new float[nFrames];
        
        for (int i = 1; i < nFrames; i++)
        {
            var prevFrame = frames[i - 1];
            var currFrame = frames[i];
            
            // VTMAGN (スペクトル振幅) を取得
            var prevVtmagnPtr = NativeLLSM.llsm_container_get(prevFrame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            var currVtmagnPtr = NativeLLSM.llsm_container_get(currFrame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            
            if (prevVtmagnPtr != IntPtr.Zero && currVtmagnPtr != IntPtr.Zero)
            {
                int prevLen = NativeLLSM.llsm_fparray_length(prevVtmagnPtr);
                int currLen = NativeLLSM.llsm_fparray_length(currVtmagnPtr);
                int minLen = Math.Min(prevLen, currLen);
                
                if (minLen > 0)
                {
                    float[] prevMagn = new float[prevLen];
                    float[] currMagn = new float[currLen];
                    Marshal.Copy(prevVtmagnPtr, prevMagn, 0, prevLen);
                    Marshal.Copy(currVtmagnPtr, currMagn, 0, currLen);
                    
                    // Spectral Flux: 正の差分の合計 (エネルギー増加)
                    float flux = 0.0f;
                    for (int j = 0; j < minLen; j++)
                    {
                        float diff = currMagn[j] - prevMagn[j];
                        if (diff > 0)
                            flux += diff;
                    }
                    spectralFlux[i] = flux;
                }
            }
        }
        
        // 統計値を計算
        float mean = spectralFlux.Average();
        float variance = 0.0f;
        foreach (float flux in spectralFlux)
            variance += (flux - mean) * (flux - mean);
        variance /= nFrames;
        float stdDev = (float)Math.Sqrt(variance);
        
        // 閾値: 平均 + 2σ (標準的な外れ値検出)
        float threshold = mean + 2.0f * stdDev;
        
        // トランジェント判定
        for (int i = 0; i < nFrames; i++)
        {
            isTransient[i] = spectralFlux[i] > threshold;
        }
        
        return isTransient;
    }
    
    /// <summary>
    /// 4つのフレームをCatmull-Romスプライン補間
    /// </summary>
    static IntPtr InterpolateFrameCubic(ContainerRef frame0, ContainerRef frame1, ContainerRef frame2, ContainerRef frame3, float ratio, int outFrameIdx = -1)
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
            f0_interp = AkimaInterp(f0_0, f0_1, f0_2, f0_3, ratio);
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
        float rd_interp = AkimaInterp(rd0, rd1, rd2, rd3, ratio);
        rd_interp = Math.Max(0.1f, Math.Min(2.7f, rd_interp));
        
        // frame1/frame2のVoicing判定（V/UV fade用）
        bool voiced1 = f0_1 > 0;
        bool voiced2 = f0_2 > 0;
        float vtmagnFadeDb = 0;
        
        // VTMAGNを取得・補間
        var vtmagn0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        var vtmagn1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        var vtmagn2Ptr = NativeLLSM.llsm_container_get(frame2.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        var vtmagn3Ptr = NativeLLSM.llsm_container_get(frame3.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
        
        float[] vtmagn_interp = null;
        if (voiced1 && voiced2 &&
            vtmagn0Ptr != IntPtr.Zero && vtmagn1Ptr != IntPtr.Zero && 
            vtmagn2Ptr != IntPtr.Zero && vtmagn3Ptr != IntPtr.Zero)
        {
            int nspec0 = NativeLLSM.llsm_fparray_length(vtmagn0Ptr);
            int nspec1 = NativeLLSM.llsm_fparray_length(vtmagn1Ptr);
            int nspec2 = NativeLLSM.llsm_fparray_length(vtmagn2Ptr);
            int nspec3 = NativeLLSM.llsm_fparray_length(vtmagn3Ptr);
            int maxspec = Math.Max(Math.Max(nspec0, nspec1), Math.Max(nspec2, nspec3));
            
            if (maxspec > 0)
            {
                float[] vtmagn0 = new float[nspec0];
                float[] vtmagn1 = new float[nspec1];
                float[] vtmagn2 = new float[nspec2];
                float[] vtmagn3 = new float[nspec3];
                Marshal.Copy(vtmagn0Ptr, vtmagn0, 0, nspec0);
                Marshal.Copy(vtmagn1Ptr, vtmagn1, 0, nspec1);
                Marshal.Copy(vtmagn2Ptr, vtmagn2, 0, nspec2);
                Marshal.Copy(vtmagn3Ptr, vtmagn3, 0, nspec3);
                
                // ケプストラム領域で4点Akima補間（共通長部分）
                float[] cepResult = CepstralInterpolateVtmagnCubic(vtmagn0, vtmagn1, vtmagn2, vtmagn3, ratio);
                vtmagn_interp = new float[maxspec];
                Array.Copy(cepResult, vtmagn_interp, cepResult.Length);
                // 共通長を超える高周波成分: vtmagn1/vtmagn2を優先
                for (int i = cepResult.Length; i < maxspec; i++)
                {
                    float val = -80.0f;
                    if (i < nspec1) val = vtmagn1[i];
                    else if (i < nspec2) val = vtmagn2[i];
                    else if (i < nspec3) val = vtmagn3[i];
                    else if (i < nspec0) val = vtmagn0[i];
                    vtmagn_interp[i] = val;
                }
                for (int i = 0; i < maxspec; i++)
                    vtmagn_interp[i] = Math.Max(-80.0f, vtmagn_interp[i]);
            }
        }
        
        // VSPHSE: 4点円弧Akima補間（unwrap→Akima→rewrap）
        // 位相のラッピング問題を回避するため、4点をunwrapしてからAkimaを適用
        var vsphse0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
        var vsphse1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
        var vsphse2Ptr = NativeLLSM.llsm_container_get(frame2.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
        var vsphse3Ptr = NativeLLSM.llsm_container_get(frame3.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
        
        float[] vsphse_interp = null;
        if (vsphse1Ptr != IntPtr.Zero && vsphse2Ptr != IntPtr.Zero)
        {
            int nhar1 = NativeLLSM.llsm_fparray_length(vsphse1Ptr);
            int nhar2 = NativeLLSM.llsm_fparray_length(vsphse2Ptr);
            int minnhar = Math.Min(nhar1, nhar2);
            int maxnhar = Math.Max(nhar1, nhar2);
            
            // 4点Akima用データ取得
            bool usePhaseAkima = false;
            int nhar0 = 0, nhar3 = 0;
            float[] vsphse0 = null, vsphse3 = null;
            if (vsphse0Ptr != IntPtr.Zero && vsphse3Ptr != IntPtr.Zero)
            {
                nhar0 = NativeLLSM.llsm_fparray_length(vsphse0Ptr);
                nhar3 = NativeLLSM.llsm_fparray_length(vsphse3Ptr);
                if (nhar0 > 0 && nhar3 > 0)
                {
                    vsphse0 = new float[nhar0];
                    vsphse3 = new float[nhar3];
                    Marshal.Copy(vsphse0Ptr, vsphse0, 0, nhar0);
                    Marshal.Copy(vsphse3Ptr, vsphse3, 0, nhar3);
                    usePhaseAkima = true;
                }
            }
            int akimaNhar = usePhaseAkima
                ? Math.Min(Math.Min(nhar0, nhar1), Math.Min(nhar2, nhar3))
                : 0;
            
            if (maxnhar > 0)
            {
                float[] vsphse1 = new float[nhar1];
                float[] vsphse2 = new float[nhar2];
                Marshal.Copy(vsphse1Ptr, vsphse1, 0, nhar1);
                Marshal.Copy(vsphse2Ptr, vsphse2, 0, nhar2);
                
                vsphse_interp = new float[maxnhar];
                for (int i = 0; i < minnhar; i++)
                {
                    if (i < akimaNhar)
                    {
                        // 4点unwrap→Akima→rewrap
                        float p0 = vsphse0[i];
                        float p1 = vsphse1[i];
                        float p2 = vsphse2[i];
                        float p3 = vsphse3[i];
                        // Unwrap: 各点間の差を[-π,π]に正規化して累積
                        float d01 = p1 - p0;
                        d01 -= 2.0f * MathF.PI * MathF.Round(d01 / (2.0f * MathF.PI));
                        float u1 = p0 + d01;
                        float d12 = p2 - u1;
                        d12 -= 2.0f * MathF.PI * MathF.Round(d12 / (2.0f * MathF.PI));
                        float u2 = u1 + d12;
                        float d23 = p3 - u2;
                        d23 -= 2.0f * MathF.PI * MathF.Round(d23 / (2.0f * MathF.PI));
                        float u3 = u2 + d23;
                        // Akima on unwrapped values
                        float result = AkimaInterp(p0, u1, u2, u3, ratio);
                        if (float.IsNaN(result) || float.IsInfinity(result))
                        {
                            vsphse_interp[i] = CircularInterpolatePhase(vsphse1[i], vsphse2[i], ratio);
                        }
                        else
                        {
                            // Rewrap to [-π, π]
                            result -= 2.0f * MathF.PI * MathF.Floor((result + MathF.PI) / (2.0f * MathF.PI));
                            vsphse_interp[i] = result;
                        }
                    }
                    else
                    {
                        // フォールバック: 円弧線形補間
                        vsphse_interp[i] = CircularInterpolatePhase(vsphse1[i], vsphse2[i], ratio);
                    }
                }
                // 範囲外の高次倍音: 利用可能なデータを使用
                for (int i = minnhar; i < maxnhar; i++)
                {
                    if (i < nhar2)
                        vsphse_interp[i] = vsphse2[i];
                    else if (i < nhar1)
                        vsphse_interp[i] = vsphse1[i];
                }
            }
        }
        
        // NM（ノイズモデル）をAkima補間でスムーズに遷移（4点制御）
        // PSD, edc, eenv振幅にAkima補間を適用。eenv位相は円弧線形補間を維持。
        IntPtr nmInterpPtr = IntPtr.Zero;
        var nm0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_NM);
        var nm1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_NM);
        var nm2Ptr = NativeLLSM.llsm_container_get(frame2.Ptr, NativeLLSM.LLSM_FRAME_NM);
        var nm3Ptr = NativeLLSM.llsm_container_get(frame3.Ptr, NativeLLSM.LLSM_FRAME_NM);
        
        if (nm1Ptr != IntPtr.Zero && nm2Ptr != IntPtr.Zero)
        {
            var nm1 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm1Ptr);
            var nm2 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm2Ptr);
            
            // frame0/frame3が利用可能かチェック（npsd/nchannelが一致する場合のみAkima使用）
            NativeLLSM.llsm_nmframe? nm0 = null, nm3 = null;
            bool useAkima = false;
            if (nm0Ptr != IntPtr.Zero && nm3Ptr != IntPtr.Zero)
            {
                var nm0v = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm0Ptr);
                var nm3v = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm3Ptr);
                if (nm0v.npsd == nm1.npsd && nm3v.npsd == nm1.npsd &&
                    nm0v.nchannel == nm1.nchannel && nm3v.nchannel == nm1.nchannel)
                {
                    nm0 = nm0v;
                    nm3 = nm3v;
                    useAkima = true;
                }
            }
            
            if (nm1.npsd == nm2.npsd && nm1.nchannel == nm2.nchannel)
            {
                // nm1をベースにコピーしてから補間（ソースフレーム破壊を防止）
                nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm1Ptr);
                var nmInterp = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmInterpPtr);
                
                // PSDをAkima補間（フォールバック: 線形補間）
                float[] psd1 = new float[nm1.npsd];
                float[] psd2 = new float[nm2.npsd];
                Marshal.Copy(nm1.psd, psd1, 0, nm1.npsd);
                Marshal.Copy(nm2.psd, psd2, 0, nm2.npsd);
                
                float[] psd0 = null, psd3 = null;
                if (useAkima)
                {
                    psd0 = new float[nm1.npsd];
                    psd3 = new float[nm1.npsd];
                    Marshal.Copy(nm0.Value.psd, psd0, 0, nm1.npsd);
                    Marshal.Copy(nm3.Value.psd, psd3, 0, nm1.npsd);
                }
                
                float[] psd_interp = new float[nm1.npsd];
                for (int p = 0; p < nm1.npsd; p++)
                {
                    if (useAkima)
                    {
                        psd_interp[p] = AkimaInterp(psd0[p], psd1[p], psd2[p], psd3[p], ratio);
                        if (float.IsNaN(psd_interp[p]) || float.IsInfinity(psd_interp[p]))
                            psd_interp[p] = psd1[p] * (1 - ratio) + psd2[p] * ratio;
                    }
                    else
                    {
                        psd_interp[p] = psd1[p] * (1 - ratio) + psd2[p] * ratio;
                    }
                }
                Marshal.Copy(psd_interp, 0, nmInterp.psd, nm1.npsd);
                // NM PSD滑らかな微小変動（時間連続バリューノイズ）
                if (outFrameIdx >= 0)
                {
                    ApplySmoothNmPsdVariation(psd_interp, nm1.npsd, outFrameIdx);
                    Marshal.Copy(psd_interp, 0, nmInterp.psd, nm1.npsd);
                }
                
                // edcもAkima補間
                if (nm1.edc != IntPtr.Zero && nm2.edc != IntPtr.Zero && nm1.nchannel > 0)
                {
                    float[] edc1 = new float[nm1.nchannel];
                    float[] edc2 = new float[nm2.nchannel];
                    Marshal.Copy(nm1.edc, edc1, 0, nm1.nchannel);
                    Marshal.Copy(nm2.edc, edc2, 0, nm2.nchannel);
                    
                    float[] edc0 = null, edc3 = null;
                    if (useAkima && nm0.Value.edc != IntPtr.Zero && nm3.Value.edc != IntPtr.Zero)
                    {
                        edc0 = new float[nm1.nchannel];
                        edc3 = new float[nm1.nchannel];
                        Marshal.Copy(nm0.Value.edc, edc0, 0, nm1.nchannel);
                        Marshal.Copy(nm3.Value.edc, edc3, 0, nm1.nchannel);
                    }
                    
                    float[] edc_interp = new float[nm1.nchannel];
                    for (int c = 0; c < nm1.nchannel; c++)
                    {
                        if (edc0 != null)
                        {
                            edc_interp[c] = AkimaInterp(edc0[c], edc1[c], edc2[c], edc3[c], ratio);
                            if (float.IsNaN(edc_interp[c]) || float.IsInfinity(edc_interp[c]))
                                edc_interp[c] = edc1[c] * (1 - ratio) + edc2[c] * ratio;
                        }
                        else
                        {
                            edc_interp[c] = edc1[c] * (1 - ratio) + edc2[c] * ratio;
                        }
                    }
                    Marshal.Copy(edc_interp, 0, nmInterp.edc, nm1.nchannel);
                }
                
                // eenv（ノイズエンベロープ）補間: 振幅はAkima、位相は円弧線形
                if (nm1.eenv != IntPtr.Zero && nm2.eenv != IntPtr.Zero && nm1.nchannel > 0)
                {
                    IntPtr[] eenv1Ptrs = new IntPtr[nm1.nchannel];
                    IntPtr[] eenv2Ptrs = new IntPtr[nm2.nchannel];
                    Marshal.Copy(nm1.eenv, eenv1Ptrs, 0, nm1.nchannel);
                    Marshal.Copy(nm2.eenv, eenv2Ptrs, 0, nm2.nchannel);
                    
                    // frame0/frame3のeenvポインタ（Akima用）
                    IntPtr[] eenv0Ptrs = null, eenv3Ptrs = null;
                    bool eenvAkima = useAkima && nm0.Value.eenv != IntPtr.Zero && nm3.Value.eenv != IntPtr.Zero;
                    if (eenvAkima)
                    {
                        eenv0Ptrs = new IntPtr[nm1.nchannel];
                        eenv3Ptrs = new IntPtr[nm1.nchannel];
                        Marshal.Copy(nm0.Value.eenv, eenv0Ptrs, 0, nm1.nchannel);
                        Marshal.Copy(nm3.Value.eenv, eenv3Ptrs, 0, nm1.nchannel);
                    }
                    
                    // nmInterp（コピー済み）のeenvポインタを取得
                    IntPtr[] eenvInterpPtrs = new IntPtr[nmInterp.nchannel];
                    Marshal.Copy(nmInterp.eenv, eenvInterpPtrs, 0, nmInterp.nchannel);
                    
                    for (int ch = 0; ch < nm1.nchannel; ch++)
                    {
                        if (eenv1Ptrs[ch] != IntPtr.Zero && eenv2Ptrs[ch] != IntPtr.Zero)
                        {
                            var eenv1 = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenv1Ptrs[ch]);
                            var eenv2 = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenv2Ptrs[ch]);
                            var eenvInterp = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvInterpPtrs[ch]);
                            
                            // Akima用: frame0/frame3のeenv
                            NativeLLSM.llsm_hmframe? eenv0 = null, eenv3 = null;
                            bool chAkima = false;
                            if (eenvAkima && eenv0Ptrs[ch] != IntPtr.Zero && eenv3Ptrs[ch] != IntPtr.Zero)
                            {
                                eenv0 = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenv0Ptrs[ch]);
                                eenv3 = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenv3Ptrs[ch]);
                                chAkima = true;
                            }
                            
                            int minnhar_e = Math.Min(eenv1.nhar, eenv2.nhar);
                            int maxnhar_e = Math.Max(eenv1.nhar, eenv2.nhar);
                            // Akimaの共通最小nhar
                            int akimaNhar = chAkima 
                                ? Math.Min(Math.Min(eenv0.Value.nhar, eenv1.nhar), Math.Min(eenv2.nhar, eenv3.Value.nhar))
                                : 0;
                            
                            if (maxnhar_e > 0)
                            {
                                float[] ampl1_arr = new float[eenv1.nhar];
                                float[] ampl2_arr = new float[eenv2.nhar];
                                float[] phse1_arr = new float[eenv1.nhar];
                                float[] phse2_arr = new float[eenv2.nhar];
                                
                                Marshal.Copy(eenv1.ampl, ampl1_arr, 0, eenv1.nhar);
                                Marshal.Copy(eenv2.ampl, ampl2_arr, 0, eenv2.nhar);
                                Marshal.Copy(eenv1.phse, phse1_arr, 0, eenv1.nhar);
                                Marshal.Copy(eenv2.phse, phse2_arr, 0, eenv2.nhar);
                                
                                float[] ampl0_arr = null, ampl3_arr = null;
                                if (chAkima)
                                {
                                    ampl0_arr = new float[eenv0.Value.nhar];
                                    ampl3_arr = new float[eenv3.Value.nhar];
                                    Marshal.Copy(eenv0.Value.ampl, ampl0_arr, 0, eenv0.Value.nhar);
                                    Marshal.Copy(eenv3.Value.ampl, ampl3_arr, 0, eenv3.Value.nhar);
                                }
                                
                                float[] ampl_interp_e = new float[maxnhar_e];
                                float[] phse_interp_e = new float[maxnhar_e];
                                
                                for (int i = 0; i < minnhar_e; i++)
                                {
                                    // 振幅: Akima補間（4点共通範囲内）、それ以外は線形
                                    if (i < akimaNhar)
                                    {
                                        ampl_interp_e[i] = AkimaInterp(ampl0_arr[i], ampl1_arr[i], ampl2_arr[i], ampl3_arr[i], ratio);
                                        if (float.IsNaN(ampl_interp_e[i]) || float.IsInfinity(ampl_interp_e[i]))
                                            ampl_interp_e[i] = ampl1_arr[i] * (1 - ratio) + ampl2_arr[i] * ratio;
                                    }
                                    else
                                    {
                                        ampl_interp_e[i] = ampl1_arr[i] * (1 - ratio) + ampl2_arr[i] * ratio;
                                    }
                                    // 位相: 円弧線形補間を維持（Akimaは位相ラッピング問題を起こす）
                                    phse_interp_e[i] = CircularInterpolatePhase(phse1_arr[i], phse2_arr[i], ratio);
                                }
                                for (int i = minnhar_e; i < maxnhar_e; i++)
                                {
                                    ampl_interp_e[i] = (i < eenv1.nhar) ? ampl1_arr[i] : ampl2_arr[i];
                                    phse_interp_e[i] = (i < eenv1.nhar) ? phse1_arr[i] : phse2_arr[i];
                                }
                                
                                // eenvInterpのnharがmaxnhar_eと異なる場合、新しいhmframeを作成
                                if (eenvInterp.nhar != maxnhar_e)
                                {
                                    var newEenv = NativeLLSM.llsm_create_hmframe(maxnhar_e);
                                    var newEenvStruct = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(newEenv);
                                    Marshal.Copy(ampl_interp_e, 0, newEenvStruct.ampl, maxnhar_e);
                                    Marshal.Copy(phse_interp_e, 0, newEenvStruct.phse, maxnhar_e);
                                    // 古いeenvを解放してnmInterp.eenv[ch]を差し替え
                                    NativeLLSM.llsm_delete_hmframe(eenvInterpPtrs[ch]);
                                    eenvInterpPtrs[ch] = newEenv;
                                }
                                else
                                {
                                    Marshal.Copy(ampl_interp_e, 0, eenvInterp.ampl, maxnhar_e);
                                    Marshal.Copy(phse_interp_e, 0, eenvInterp.phse, maxnhar_e);
                                }
                            }
                        }
                    }
                    // eenvポインタ配列をnmInterpに書き戻す
                    Marshal.Copy(eenvInterpPtrs, 0, nmInterp.eenv, nmInterp.nchannel);
                }
            }
        }
        else if (nm1Ptr != IntPtr.Zero)
        {
            nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm1Ptr);
        }
        else if (nm2Ptr != IntPtr.Zero)
        {
            nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm2Ptr);
        }
        
        // ベースフレームをコピーしてから補間フィールドを上書き（demo-stretch.c準拠）
        // V/UV遷移時は有声フレームを優先選択
        ContainerRef baseFrame;
        if (!voiced1 && voiced2)
        {
            baseFrame = frame2;
            // UV→V: コサインフェードイン（ratio=0→最大減衰, ratio=1→0dB）
            float fadeCos = 0.5f * (1.0f - MathF.Cos(MathF.PI * ratio));
            float fadeFactor = MathF.Sqrt(MathF.Max(1e-8f, fadeCos));
            vtmagnFadeDb = Math.Max(-24.0f, 20.0f * MathF.Log10(fadeFactor));
        }
        else if (voiced1 && !voiced2)
        {
            baseFrame = frame1;
            // V→UV: コサインフェードアウト（ratio=0→0dB, ratio=1→最大減衰）
            float fadeCos = 0.5f * (1.0f - MathF.Cos(MathF.PI * (1.0f - ratio)));
            float fadeFactor = MathF.Sqrt(MathF.Max(1e-8f, fadeCos));
            vtmagnFadeDb = Math.Max(-24.0f, 20.0f * MathF.Log10(fadeFactor));
        }
        else
        {
            baseFrame = ratio < 0.5f ? frame1 : frame2;
        }
        var newFrame = NativeLLSM.llsm_copy_container(baseFrame.Ptr);
        
        // F0を補間値で上書き
        var f0Ptr = NativeLLSM.llsm_create_fp(f0_interp);
        NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_F0,
            f0Ptr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
        
        var rdPtr = NativeLLSM.llsm_create_fp(rd_interp);
        NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_RD,
            rdPtr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
        
        if (vtmagn_interp != null)
        {
            // 両方有声: cubic cepstral補間結果を使用
            var vtmagnPtr = NativeLLSM.llsm_create_fparray(vtmagn_interp.Length);
            Marshal.Copy(vtmagn_interp, 0, vtmagnPtr, vtmagn_interp.Length);
            NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_VTMAGN,
                vtmagnPtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
        }
        else if (vtmagnFadeDb != 0)
        {
            // V/UV遷移: ベースフレームのVTMAGNにdBフェード適用
            var vtmagnSrcPtr = NativeLLSM.llsm_container_get(newFrame, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnSrcPtr != IntPtr.Zero)
            {
                int nspec = NativeLLSM.llsm_fparray_length(vtmagnSrcPtr);
                float[] vtmagn = new float[nspec];
                Marshal.Copy(vtmagnSrcPtr, vtmagn, 0, nspec);
                for (int i = 0; i < nspec; i++)
                    vtmagn[i] = Math.Max(-80.0f, vtmagn[i] + vtmagnFadeDb);
                Marshal.Copy(vtmagn, 0, vtmagnSrcPtr, nspec);
            }
        }
        else if (!voiced1 && !voiced2)
        {
            // 両方無声: VTMAGNフロアクランプのみ
            var vtmagnSrcPtr = NativeLLSM.llsm_container_get(newFrame, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnSrcPtr != IntPtr.Zero)
            {
                int nspec = NativeLLSM.llsm_fparray_length(vtmagnSrcPtr);
                float[] vtmagn = new float[nspec];
                Marshal.Copy(vtmagnSrcPtr, vtmagn, 0, nspec);
                for (int i = 0; i < nspec; i++)
                    vtmagn[i] = Math.Max(-80.0f, vtmagn[i]);
                Marshal.Copy(vtmagn, 0, vtmagnSrcPtr, nspec);
            }
        }
        
        if (vsphse_interp != null)
        {
            var vsphsePtr = NativeLLSM.llsm_create_fparray(vsphse_interp.Length);
            Marshal.Copy(vsphse_interp, 0, vsphsePtr, vsphse_interp.Length);
            NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_VSPHSE,
                vsphsePtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
        }
        else
        {
            // 補間失敗時: frame1またはframe2からコピー（4点補間）
            var vsphseSrc = ratio < 0.5 ? frame1 : frame2;
            var vsphseSrcPtr = NativeLLSM.llsm_container_get(vsphseSrc.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
            if (vsphseSrcPtr != IntPtr.Zero)
            {
                var vsphsePtr = NativeLLSM.llsm_copy_fparray(vsphseSrcPtr);
                NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_VSPHSE,
                    vsphsePtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                    Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
            }
        }
        
        if (nmInterpPtr != IntPtr.Zero)
        {
            NativeLLSM.llsm_container_attach_(newFrame, NativeLLSM.LLSM_FRAME_NM,
                nmInterpPtr, Marshal.GetFunctionPointerForDelegate(_deleteNm), 
                Marshal.GetFunctionPointerForDelegate(_copyNm));
        }
        
        // PSDRESは補間しない — demo-stretch.c準拠で呼び出し元でランダム近傍フレームからコピーする
        // （補間すると確率的ノイズが周期化しジッタリングが聴こえる）
        
        return newFrame;
    }
    
    /// <summary>
    /// 決定論的ハッシュノイズ: (frameIdx, binIdx) → [-0.5, 0.5] の一様分布的な値
    /// </summary>
    static float HashNoise(int frameIdx, int binIdx)
    {
        uint h = (uint)(frameIdx * 73856093 ^ binIdx * 19349663);
        h *= 2654435761u;
        h ^= h >> 16;
        h *= 0x45d9f3bu;
        h ^= h >> 16;
        return (float)(h & 0xFFFF) / 65536f - 0.5f;
    }

    /// <summary>
    /// NM PSD に時間的に滑らかな微小変動を加える。
    /// 出力フレームインデックスから決定論的バリューノイズを生成し、
    /// ノットポイント間をコサイン補間することでフレーム間の連続性を保証する。
    /// </summary>
    static void ApplySmoothNmPsdVariation(float[] psd, int npsd, int outFrameIdx)
    {
        const int knotInterval = 8; // 8フレームおきにノット（200fps→40ms周期）
        const float amplitude = 0.5f; // ±0.5dB

        int knotIdx = outFrameIdx / knotInterval;
        float t = (float)(outFrameIdx % knotInterval) / knotInterval;
        // コサイン補間 (0→1 を滑らかに遷移)
        float blend = (1f - MathF.Cos(t * MathF.PI)) * 0.5f;

        for (int p = 0; p < npsd; p++)
        {
            float n0 = HashNoise(knotIdx, p);
            float n1 = HashNoise(knotIdx + 1, p);
            float noise = n0 * (1f - blend) + n1 * blend;
            psd[p] += noise * amplitude;
        }
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
    /// ソースチャンクのVTMAGNからスペクトル傾斜を推定する。
    /// 有声フレームのVTMAGNの周波数軸に対する線形回帰傾斜の中央値を返す（dB/normalized_freq）。
    /// 返り値は負（通常の声は高域が減衰するため）。
    /// </summary>
    static float MeasureAverageSpectralSlope(ChunkHandle chunk)
    {
        int nfrm = Llsm.GetNumFrames(chunk);
        var slopes = new List<float>();
        
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            float f0 = Llsm.GetFrameF0(frame);
            if (f0 < 50.0f) continue;  // 無声フレームをスキップ
            
            var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnPtr == IntPtr.Zero) continue;
            
            int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
            if (nspec < 10) continue;
            
            float[] vtmagn = new float[nspec];
            Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
            
            // 線形回帰: y = vtmagn[j], x = j/nspec
            // slope = (n*Σxy - Σx*Σy) / (n*Σx² - (Σx)²)
            // DC付近と-120dBクランプ域を除外（全体の5%～90%を使用）
            int lo = nspec / 20;
            int hi = (int)(nspec * 0.9f);
            int n = hi - lo;
            if (n < 5) continue;
            
            float sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int j = lo; j < hi; j++)
            {
                float x = (float)j / nspec;
                float y = vtmagn[j];
                if (y <= -119.0f) continue;  // クランプされたビンをスキップ
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
            }
            float denom = n * sumX2 - sumX * sumX;
            if (MathF.Abs(denom) < 1e-10f) continue;
            float slope = (n * sumXY - sumX * sumY) / denom;
            slopes.Add(slope);
        }
        
        if (slopes.Count == 0) return -20.0f;  // デフォルト: 典型的な声のスペクトル傾斜
        
        // 中央値を返す（外れ値に頑健）
        slopes.Sort();
        return slopes[slopes.Count / 2];
    }
    
    /// <summary>
    /// ソースのスペクトル傾斜からtiltCoeffを算出する。
    /// slopeはdB/normalized_freq（通常は負値）。
    /// 傾斜が急な声（男声・暗い声）→ 大きなtiltCoeff
    /// 傾斜が緩い声（女声・明るい声）→ 小さなtiltCoeff
    /// </summary>
    static float ComputeAdaptiveTiltCoeff(float spectralSlope)
    {
        // spectralSlope: 典型的に -10～-60 dB/normalized_freq
        // tiltCoeff目安: 0.5～4.0 dB/octave
        // 線形マッピング: slope=-10 → 0.5, slope=-60 → 4.0
        float coeff = Math.Clamp(-spectralSlope * 0.07f, 0.5f, 4.0f);
        return coeff;
    }
    
    /// <summary>
    /// 2つのLayer1フレームを線形補間して新しいフレームを生成（demo-stretch.c準拠）
    /// V/UV遷移を含む全ケースでNMとPSDRESを常に補間し、ノイズ特性の急変を防ぐ
    /// </summary>
    static IntPtr InterpolateFrame(ContainerRef frame0, ContainerRef frame1, float ratio, int outFrameIdx = -1)
    {
        // F0を取得
        float f0_0 = Llsm.GetFrameF0(frame0);
        float f0_1 = Llsm.GetFrameF0(frame1);
        
        const float minVoicedF0 = 50.0f;
        bool voiced0 = f0_0 >= minVoicedF0;
        bool voiced1 = f0_1 >= minVoicedF0;
        bool bothVoiced = voiced0 && voiced1;
        
        // === Phase 1: F0, Rd, VTMAGN, VSPHSE の処理（V/UV状態に応じて） ===
        // demo-stretch.c準拠: 有声フレームを優先、V/UV遷移時はVTMAGNをdBフェード
        float f0_interp;
        float rd_interp;
        float[] vtmagn_interp = null;
        float[] vsphse_interp = null;
        ContainerRef baseFrame;        // コピー元フレーム（PBPEFF等の非補間フィールド用）
        float vtmagnFadeDb = 0;        // V/UV遷移時のfade量
        
        if (bothVoiced)
        {
            // 両方有声: 完全線形補間
            baseFrame = ratio < 0.5f ? frame0 : frame1;
            f0_interp = f0_0 * (1 - ratio) + f0_1 * ratio;
            f0_interp = Math.Max(minVoicedF0, f0_interp);
            
            // Rd補間
            var rd0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_RD);
            var rd1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_RD);
            float rd0 = rd0Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd0Ptr) : 1.0f;
            float rd1 = rd1Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd1Ptr) : 1.0f;
            rd_interp = rd0 * (1 - ratio) + rd1 * ratio;
            rd_interp = Math.Max(0.1f, Math.Min(2.7f, rd_interp));
            
            // VTMAGNケプストラム分離補間: 低次(フォルマント)を線形補間、高次(テクスチャ)は最近傍
            var vtmagn0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            var vtmagn1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagn0Ptr != IntPtr.Zero && vtmagn1Ptr != IntPtr.Zero)
            {
                int nspec0 = NativeLLSM.llsm_fparray_length(vtmagn0Ptr);
                int nspec1 = NativeLLSM.llsm_fparray_length(vtmagn1Ptr);
                int maxspec = Math.Max(nspec0, nspec1);
                if (maxspec > 0)
                {
                    float[] vtmagn0 = new float[nspec0];
                    float[] vtmagn1 = new float[nspec1];
                    Marshal.Copy(vtmagn0Ptr, vtmagn0, 0, nspec0);
                    Marshal.Copy(vtmagn1Ptr, vtmagn1, 0, nspec1);
                    // ケプストラム領域で補間（共通長部分）
                    float[] cepResult = CepstralInterpolateVtmagn(vtmagn0, vtmagn1, ratio);
                    vtmagn_interp = new float[maxspec];
                    Array.Copy(cepResult, vtmagn_interp, cepResult.Length);
                    // 共通長を超える高周波成分: 長い方からコピー
                    for (int i = cepResult.Length; i < maxspec; i++)
                        vtmagn_interp[i] = (i < nspec1) ? vtmagn1[i] : vtmagn0[i];
                }
            }
            
            // VSPHSE円形補間
            var vsphse0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
            var vsphse1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_VSPHSE);
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
                    for (int i = 0; i < minnhar; i++)
                        vsphse_interp[i] = CircularInterpolatePhase(vsphse0[i], vsphse1[i], ratio);
                    for (int i = minnhar; i < maxnhar; i++)
                        vsphse_interp[i] = (i < nhar1) ? vsphse1[i] : vsphse0[i];
                }
            }
        }
        else if (!voiced0 && voiced1)
        {
            // frame1（有声）にフェードイン
            baseFrame = frame1;
            f0_interp = f0_1;
            var rd1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_RD);
            rd_interp = rd1Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd1Ptr) : 1.0f;
            // ★等パワーコサインフェードで滑らかにV/UV遷移
            // ratio=0→フェード最大, ratio=1→0dB(完全有声)
            // 下限-24dBで完全無音を防止（無声子音ch,s等のノイズ成分を保護）
            float fadeCos = 0.5f * (1.0f - MathF.Cos(MathF.PI * ratio));
            float fadeFactor = MathF.Sqrt(MathF.Max(1e-8f, fadeCos));
            vtmagnFadeDb = Math.Max(-24.0f, 20.0f * MathF.Log10(fadeFactor));
        }
        else if (voiced0 && !voiced1)
        {
            // frame0（有声）からフェードアウト
            baseFrame = frame0;
            f0_interp = f0_0;
            var rd0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_RD);
            rd_interp = rd0Ptr != IntPtr.Zero ? Marshal.PtrToStructure<float>(rd0Ptr) : 1.0f;
            // ★等パワーコサインフェードで滑らかにV/UV遷移
            // 下限-24dBで完全無音を防止
            float fadeCos2 = 0.5f * (1.0f - MathF.Cos(MathF.PI * (1.0f - ratio)));
            float fadeFactor2 = MathF.Sqrt(MathF.Max(1e-8f, fadeCos2));
            vtmagnFadeDb = Math.Max(-24.0f, 20.0f * MathF.Log10(fadeFactor2));
        }
        else
        {
            // 両方無声（demo-stretch.c: voiced == NULL）
            baseFrame = ratio < 0.5f ? frame0 : frame1;
            f0_interp = 0;
            rd_interp = 1.0f;
        }
        
        // === Phase 2: ベースフレームをコピーして出力フレーム構築 ===
        var frameInterpPtr = NativeLLSM.llsm_copy_container(baseFrame.Ptr);
        
        // F0設定
        var f0Ptr = NativeLLSM.llsm_create_fp(f0_interp);
        NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_F0,
            f0Ptr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
        
        // Rd設定
        var rdPtr = NativeLLSM.llsm_create_fp(rd_interp);
        NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_RD,
            rdPtr, Marshal.GetFunctionPointerForDelegate(_deleteFp), IntPtr.Zero);
        
        // VTMAGN設定（補間あり or フェード適用）
        if (vtmagn_interp != null)
        {
            // 両方有声: 補間済み配列を使用
            for (int i = 0; i < vtmagn_interp.Length; i++)
                vtmagn_interp[i] = Math.Max(-80.0f, vtmagn_interp[i]);
            var vtmagnPtr = NativeLLSM.llsm_create_fparray(vtmagn_interp.Length);
            Marshal.Copy(vtmagn_interp, 0, vtmagnPtr, vtmagn_interp.Length);
            NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_VTMAGN,
                vtmagnPtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
        }
        else if (vtmagnFadeDb != 0)
        {
            // V/UV遷移: ベースフレームのVTMAGNにdBフェード適用（demo-stretch.c準拠）
            var vtmagnSrcPtr = NativeLLSM.llsm_container_get(frameInterpPtr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnSrcPtr != IntPtr.Zero)
            {
                int nspec = NativeLLSM.llsm_fparray_length(vtmagnSrcPtr);
                float[] vtmagn = new float[nspec];
                Marshal.Copy(vtmagnSrcPtr, vtmagn, 0, nspec);
                for (int i = 0; i < nspec; i++)
                    vtmagn[i] = Math.Max(-80.0f, vtmagn[i] + vtmagnFadeDb);
                Marshal.Copy(vtmagn, 0, vtmagnSrcPtr, nspec);
            }
        }
        // 両方無声の場合: ベースフレームのVTMAGNをそのまま使用（-120dBクランプのみ）
        else if (!voiced0 && !voiced1)
        {
            var vtmagnSrcPtr = NativeLLSM.llsm_container_get(frameInterpPtr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnSrcPtr != IntPtr.Zero)
            {
                int nspec = NativeLLSM.llsm_fparray_length(vtmagnSrcPtr);
                float[] vtmagn = new float[nspec];
                Marshal.Copy(vtmagnSrcPtr, vtmagn, 0, nspec);
                for (int i = 0; i < nspec; i++)
                    vtmagn[i] = Math.Max(-80.0f, vtmagn[i]);
                Marshal.Copy(vtmagn, 0, vtmagnSrcPtr, nspec);
            }
        }
        
        // VSPHSE設定（両方有声の場合のみ補間）
        if (vsphse_interp != null)
        {
            var vsphsePtr = NativeLLSM.llsm_create_fparray(vsphse_interp.Length);
            Marshal.Copy(vsphse_interp, 0, vsphsePtr, vsphse_interp.Length);
            NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_VSPHSE,
                vsphsePtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray), 
                Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
        }
        
        // === Phase 3: NM は常に補間（demo-stretch.c準拠: V/UV状態に関係なく） ===
        // ノイズ特性は有声/無声に関係なく滑らかに遷移すべき
        {
            IntPtr nmInterpPtr = IntPtr.Zero;
            var nm0Ptr = NativeLLSM.llsm_container_get(frame0.Ptr, NativeLLSM.LLSM_FRAME_NM);
            var nm1Ptr = NativeLLSM.llsm_container_get(frame1.Ptr, NativeLLSM.LLSM_FRAME_NM);
            
            if (nm0Ptr != IntPtr.Zero && nm1Ptr != IntPtr.Zero)
            {
                var nm0 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm0Ptr);
                var nm1 = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nm1Ptr);
                
                if (nm0.npsd == nm1.npsd && nm0.nchannel == nm1.nchannel)
                {
                    nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm0Ptr);
                    var nmInterp = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmInterpPtr);
                    
                    // PSD線形補間（dB域 → 対数パワー補間と等価）
                    float[] psd0 = new float[nm0.npsd];
                    float[] psd1 = new float[nm1.npsd];
                    Marshal.Copy(nm0.psd, psd0, 0, nm0.npsd);
                    Marshal.Copy(nm1.psd, psd1, 0, nm1.npsd);
                    float[] psd_interp = new float[nm0.npsd];
                    for (int p = 0; p < nm0.npsd; p++)
                        psd_interp[p] = psd0[p] * (1 - ratio) + psd1[p] * ratio;
                    Marshal.Copy(psd_interp, 0, nmInterp.psd, nm0.npsd);
                    // NM PSD滑らかな微小変動（時間連続バリューノイズ）
                    if (outFrameIdx >= 0)
                    {
                        ApplySmoothNmPsdVariation(psd_interp, nm0.npsd, outFrameIdx);
                        Marshal.Copy(psd_interp, 0, nmInterp.psd, nm0.npsd);
                    }
                    
                    // EDC線形補間
                    if (nm0.edc != IntPtr.Zero && nm1.edc != IntPtr.Zero && nm0.nchannel > 0)
                    {
                        float[] edc0 = new float[nm0.nchannel];
                        float[] edc1 = new float[nm1.nchannel];
                        Marshal.Copy(nm0.edc, edc0, 0, nm0.nchannel);
                        Marshal.Copy(nm1.edc, edc1, 0, nm1.nchannel);
                        float[] edc_interp = new float[nm0.nchannel];
                        for (int c = 0; c < nm0.nchannel; c++)
                            edc_interp[c] = edc0[c] * (1 - ratio) + edc1[c] * ratio;
                        Marshal.Copy(edc_interp, 0, nmInterp.edc, nm0.nchannel);
                    }
                    
                    // eenv（ノイズエンベロープ）補間
                    if (nm0.eenv != IntPtr.Zero && nm1.eenv != IntPtr.Zero && nm0.nchannel > 0)
                    {
                        IntPtr[] eenv0Ptrs = new IntPtr[nm0.nchannel];
                        IntPtr[] eenv1Ptrs = new IntPtr[nm1.nchannel];
                        Marshal.Copy(nm0.eenv, eenv0Ptrs, 0, nm0.nchannel);
                        Marshal.Copy(nm1.eenv, eenv1Ptrs, 0, nm1.nchannel);
                        
                        IntPtr[] eenvInterpPtrs = new IntPtr[nmInterp.nchannel];
                        Marshal.Copy(nmInterp.eenv, eenvInterpPtrs, 0, nmInterp.nchannel);
                        
                        for (int ch = 0; ch < nm0.nchannel; ch++)
                        {
                            if (eenv0Ptrs[ch] != IntPtr.Zero && eenv1Ptrs[ch] != IntPtr.Zero)
                            {
                                var eenv0 = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenv0Ptrs[ch]);
                                var eenv1 = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenv1Ptrs[ch]);
                                var eenvInterp = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(eenvInterpPtrs[ch]);
                                
                                int minnhar_e = Math.Min(eenv0.nhar, eenv1.nhar);
                                int maxnhar_e = Math.Max(eenv0.nhar, eenv1.nhar);
                                
                                if (maxnhar_e > 0)
                                {
                                    float[] ampl0 = new float[eenv0.nhar];
                                    float[] ampl1 = new float[eenv1.nhar];
                                    float[] phse0 = new float[eenv0.nhar];
                                    float[] phse1 = new float[eenv1.nhar];
                                    Marshal.Copy(eenv0.ampl, ampl0, 0, eenv0.nhar);
                                    Marshal.Copy(eenv1.ampl, ampl1, 0, eenv1.nhar);
                                    Marshal.Copy(eenv0.phse, phse0, 0, eenv0.nhar);
                                    Marshal.Copy(eenv1.phse, phse1, 0, eenv1.nhar);
                                    
                                    float[] ampl_e = new float[maxnhar_e];
                                    float[] phse_e = new float[maxnhar_e];
                                    for (int i = 0; i < minnhar_e; i++)
                                    {
                                        ampl_e[i] = ampl0[i] * (1 - ratio) + ampl1[i] * ratio;
                                        phse_e[i] = CircularInterpolatePhase(phse0[i], phse1[i], ratio);
                                    }
                                    for (int i = minnhar_e; i < maxnhar_e; i++)
                                    {
                                        ampl_e[i] = (i < eenv0.nhar) ? ampl0[i] : ampl1[i];
                                        phse_e[i] = (i < eenv0.nhar) ? phse0[i] : phse1[i];
                                    }
                                    
                                    if (eenvInterp.nhar != maxnhar_e)
                                    {
                                        var newEenv = NativeLLSM.llsm_create_hmframe(maxnhar_e);
                                        var newStr = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(newEenv);
                                        Marshal.Copy(ampl_e, 0, newStr.ampl, maxnhar_e);
                                        Marshal.Copy(phse_e, 0, newStr.phse, maxnhar_e);
                                        NativeLLSM.llsm_delete_hmframe(eenvInterpPtrs[ch]);
                                        eenvInterpPtrs[ch] = newEenv;
                                    }
                                    else
                                    {
                                        Marshal.Copy(ampl_e, 0, eenvInterp.ampl, maxnhar_e);
                                        Marshal.Copy(phse_e, 0, eenvInterp.phse, maxnhar_e);
                                    }
                                }
                            }
                        }
                        Marshal.Copy(eenvInterpPtrs, 0, nmInterp.eenv, nmInterp.nchannel);
                    }
                }
            }
            else if (nm0Ptr != IntPtr.Zero)
            {
                nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm0Ptr);
            }
            else if (nm1Ptr != IntPtr.Zero)
            {
                nmInterpPtr = NativeLLSM.llsm_copy_nmframe(nm1Ptr);
            }
            
            if (nmInterpPtr != IntPtr.Zero)
            {
                NativeLLSM.llsm_container_attach_(frameInterpPtr, NativeLLSM.LLSM_FRAME_NM,
                    nmInterpPtr, Marshal.GetFunctionPointerForDelegate(_deleteNm), 
                    Marshal.GetFunctionPointerForDelegate(_copyNm));
            }
        }
        
        // === Phase 4: PSDRESは補間しない — 呼び出し元でランダム近傍コピー ===
        // （補間すると確率的ノイズが周期化しジッタリングが聴こえる）
        
        return frameInterpPtr;
    }
    
    /// <summary>
    /// ピッチ上昇時にVSPHSE（声門源位相）配列を拡張し、Layer0変換で倍音数が減少するのを防止する。
    /// llsm_frame_tolayer0 では nhar = min(nhar, fnyq/f0) で倍音数が決まるため、
    /// F0が上がると倍音の上限周波数を超える倍音が切り捨てられ、高域エネルギーが消失する。
    /// 対策: ピッチ比率に応じてVSPHSEを事前拡張し、十分なnharを確保する。
    /// 拡張された高次倍音の位相は最終倍音からの線形外挿で近似する。
    /// </summary>
    static void ExtendVsphseForPitchShift(IntPtr framePtr, float originalF0, float newF0, int fs)
    {
        var vsphsePtr = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_VSPHSE);
        if (vsphsePtr == IntPtr.Zero) return;
        
        int currentNhar = NativeLLSM.llsm_fparray_length(vsphsePtr);
        if (currentNhar < 4) return;
        
        float fnyq = fs / 2.0f;
        // 新しいF0で到達可能な最大倍音数
        int targetNhar = (int)(fnyq / newF0);
        // 元のF0から見て同じ周波数カバレッジに必要な倍音数
        int neededNhar = (int)(fnyq / originalF0);
        // 最終的に必要な倍音数: 元のカバレッジを維持するのに十分な数
        int extendedNhar = Math.Min(targetNhar, Math.Max(currentNhar, neededNhar));
        
        if (extendedNhar <= currentNhar) return;  // 拡張不要
        
        // 現在のVSPHSEを読み取り
        float[] vsphse = new float[currentNhar];
        Marshal.Copy(vsphsePtr, vsphse, 0, currentNhar);
        
        // 拡張された配列を準備
        float[] extendedVsphse = new float[extendedNhar];
        Array.Copy(vsphse, extendedVsphse, currentNhar);
        
        // 高次倍音の位相を線形外挿で推定
        // 最後の2倍音の位相差から傾きを推定し、外挿
        float phaseDiff1 = vsphse[currentNhar - 1] - vsphse[currentNhar - 2];
        phaseDiff1 -= 2.0f * MathF.PI * MathF.Round(phaseDiff1 / (2.0f * MathF.PI));  // unwrap
        float phaseDiff2 = vsphse[currentNhar - 2] - vsphse[currentNhar - 3];
        phaseDiff2 -= 2.0f * MathF.PI * MathF.Round(phaseDiff2 / (2.0f * MathF.PI));
        float avgDiff = (phaseDiff1 + phaseDiff2) * 0.5f;
        
        for (int k = currentNhar; k < extendedNhar; k++)
        {
            float extrapolated = vsphse[currentNhar - 1] + avgDiff * (k - currentNhar + 1);
            // Rewrap to [-π, π]
            extrapolated -= 2.0f * MathF.PI * MathF.Floor((extrapolated + MathF.PI) / (2.0f * MathF.PI));
            extendedVsphse[k] = extrapolated;
        }
        
        // 新しい拡張VSPHSEをフレームにアタッチ
        var newVsphsePtr = NativeLLSM.llsm_create_fparray(extendedNhar);
        Marshal.Copy(extendedVsphse, 0, newVsphsePtr, extendedNhar);
        NativeLLSM.llsm_container_attach_(framePtr, NativeLLSM.LLSM_FRAME_VSPHSE,
            newVsphsePtr, Marshal.GetFunctionPointerForDelegate(_deleteFpArray),
            Marshal.GetFunctionPointerForDelegate(_copyFpArrayFunc));
    }
    
    /// <summary>
    /// PSDRES（残差ノイズスペクトル）をランダム近傍フレームとブレンドして上書きする。
    /// demo-stretch.c準拠のランダム近傍選択だが、全置換ではなく加重ブレンドで
    /// スペクトル形状を保ちながら微細な変動を導入する。
    /// blendRatio = 0.2: 基底フレーム80% + ランダム近傍20%
    /// </summary>
    static void AttachRandomNearbyPsdres(IntPtr dstFramePtr, ChunkHandle srcChunk, int baseIdx, int srcNfrm)
    {
        // Voicing check: 有声フレームのみPSDRES置換（無声はPSDRESが安定しており不要）
        var f0Ptr = NativeLLSM.llsm_container_get(dstFramePtr, NativeLLSM.LLSM_FRAME_F0);
        if (f0Ptr != IntPtr.Zero)
        {
            float f0 = Marshal.PtrToStructure<float>(f0Ptr);
            if (f0 <= 0) return;  // 無声フレームはスキップ
        }
        
        // 現在のフレーム（補間済み or コピー済み）のPSDRESを取得
        var basePsdresPtr = NativeLLSM.llsm_container_get(dstFramePtr, NativeLLSM.LLSM_FRAME_PSDRES);
        if (basePsdresPtr == IntPtr.Zero) return;
        
        int psdLen = NativeLLSM.llsm_fparray_length(basePsdresPtr);
        if (psdLen <= 0) return;
        
        // ランダム近傍フレームを選択（±2フレーム）
        int offset = Random.Shared.Next(5) - 2;
        if (offset == 0) offset = (Random.Shared.Next(2) == 0) ? -1 : 1;  // 0回避で必ず異なるフレーム
        int residx = Math.Clamp(baseIdx + offset, 0, srcNfrm - 1);
        var resFrame = Llsm.GetFrame(srcChunk, residx);
        var resPsdresPtr = NativeLLSM.llsm_container_get(resFrame.Ptr, NativeLLSM.LLSM_FRAME_PSDRES);
        if (resPsdresPtr == IntPtr.Zero) return;
        
        int resLen = NativeLLSM.llsm_fparray_length(resPsdresPtr);
        int copyLen = Math.Min(psdLen, resLen);
        
        // ランダム近傍フレームのPSDRESで完全置換（フレーム間不連続を解消）
        float[] resPsdres = new float[resLen];
        Marshal.Copy(resPsdresPtr, resPsdres, 0, resLen);
        Marshal.Copy(resPsdres, 0, basePsdresPtr, copyLen);
    }
    
    /// <summary>
    /// フォルマントピーク検出
    /// スペクトル包絡からローカルピークを検出し、フォルマントと推定される位置を返す
    /// </summary>
    /// <param name="vtmagn">声道振幅スペクトル（対数dB）</param>
    /// <param name="fs">サンプリング周波数</param>
    /// <param name="nspec">スペクトルの長さ</param>
    /// <returns>フォルマントピークのリスト（インデックスと振幅）</returns>
    static List<(int index, float magnitude)> DetectFormantPeaks(float[] vtmagn, int fs, int nspec)
    {
        var peaks = new List<(int, float)>();
        
        // フォルマント検出範囲：200Hz～5000Hz（音声フォルマントの典型的範囲）
        int minBin = (int)(200.0f / (fs / 2.0f) * nspec);
        int maxBin = (int)(5000.0f / (fs / 2.0f) * nspec);
        minBin = Math.Max(1, minBin);
        maxBin = Math.Min(nspec - 2, maxBin);
        
        // ピーク検出：前後のビンより大きい点 + 最小閾値
        float threshold = -40.0f;  // -40dB以上のピークのみ検出
        for (int i = minBin; i <= maxBin; i++)
        {
            if (vtmagn[i] > threshold &&
                vtmagn[i] > vtmagn[i - 1] &&
                vtmagn[i] > vtmagn[i + 1])
            {
                // ローカルピーク発見
                peaks.Add((i, vtmagn[i]));
            }
        }
        
        // ピークが多すぎる場合、上位5つに制限（F1～F5相当）
        if (peaks.Count > 5)
        {
            peaks = peaks.OrderByDescending(p => p.Item2).Take(5).ToList();
        }
        
        return peaks;
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
    /// 秋間補間（Akima Spline）
    /// Catmull-Romのオーバーシュートを防ぎつつ、Monotonic Cubicより局所変化に敏感。
    /// スペクトル包絡のフォルマント形状を自然に保持し、平坦部では直線的に振る舞う。
    /// 参考: H. Akima, "A new method of interpolation and smooth curve fitting
    ///       based on local procedures", JACM 17(4), 1970.
    /// </summary>
    static float AkimaInterp(float p0, float p1, float p2, float p3, float t)
    {
        // 隣接点間の差分（等間隔前提: h=1）
        float d0 = p1 - p0;  // 区間[0,1]の傾き
        float d1 = p2 - p1;  // 区間[1,2]の傾き（補間対象）
        float d2 = p3 - p2;  // 区間[2,3]の傾き
        
        // 境界外の差分を外挿（Akimaの原論文方式）
        float dm1 = 2.0f * d0 - d1;  // 区間[-1,0]の仮想傾き
        float d3  = 2.0f * d2 - d1;  // 区間[3,4]の仮想傾き
        
        // p1での接線（秋間の重み付き平均）
        float w1_L = MathF.Abs(d2 - d1);   // |m_{i+1} - m_i|
        float w1_R = MathF.Abs(d0 - dm1);  // |m_{i-1} - m_{i-2}|
        float s1;
        if (w1_L + w1_R > 1e-10f)
            s1 = (w1_L * d0 + w1_R * d1) / (w1_L + w1_R);
        else
            s1 = (d0 + d1) * 0.5f;  // 等傾斜時は算術平均
        
        // p2での接線
        float w2_L = MathF.Abs(d3 - d2);   // |m_{i+1} - m_i|
        float w2_R = MathF.Abs(d1 - d0);   // |m_{i-1} - m_{i-2}|
        float s2;
        if (w2_L + w2_R > 1e-10f)
            s2 = (w2_L * d1 + w2_R * d2) / (w2_L + w2_R);
        else
            s2 = (d1 + d2) * 0.5f;
        
        // Hermite基底関数で補間
        float t2 = t * t;
        float t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        
        return h00 * p1 + h10 * s1 + h01 * p2 + h11 * s2;
    }
    
    /// <summary>
    /// 部分DCT-II: 最初のK個のケプストラム係数のみ計算 (O(K*N), K≪Nで高速)
    /// 全N²の計算を避け、フォルマント包絡に必要な低次成分のみ求める。
    /// </summary>
    static float[] PartialDctII(float[] x, int K)
    {
        int N = x.Length;
        float[] c = new float[K];
        for (int k = 0; k < K; k++)
        {
            double sum = 0;
            double freq = Math.PI / N * k;
            for (int n = 0; n < N; n++)
                sum += x[n] * Math.Cos(freq * (n + 0.5));
            c[k] = (float)sum;
        }
        return c;
    }
    
    /// <summary>
    /// 部分IDCT-II: K個のケプストラム係数からN点スペクトルを復元 (O(K*N))
    /// </summary>
    static float[] PartialIdctII(float[] c, int N)
    {
        int K = c.Length;
        float[] x = new float[N];
        double invN = 1.0 / N;
        double twoInvN = 2.0 / N;
        for (int n = 0; n < N; n++)
        {
            double sum = c[0] * invN;
            double base_angle = Math.PI / N * (n + 0.5);
            for (int k = 1; k < K; k++)
                sum += c[k] * twoInvN * Math.Cos(base_angle * k);
            x[n] = (float)sum;
        }
        return x;
    }
    
    /// <summary>
    /// ケプストラム分離補間 (高速版): 最近傍スペクトル + 低次フォルマント差分
    /// 計算量: O(3*K*N) ≈ O(75*N), 旧版O(3*N²)の約20分の1。
    /// 低次ケプストラムの補間差分のみIDCTし、最近傍フレームに加算する方式。
    /// </summary>
    static float[] CepstralInterpolateVtmagn(float[] vtmagn0, float[] vtmagn1, float ratio, int cepOrder = -1)
    {
        int N = Math.Min(vtmagn0.Length, vtmagn1.Length);
        float[] v0 = vtmagn0.Length == N ? vtmagn0 : vtmagn0[..N];
        float[] v1 = vtmagn1.Length == N ? vtmagn1 : vtmagn1[..N];
        
        // Adaptive K: N/10をベースに15～35の範囲でスペクトル解像度に応じて調整
        if (cepOrder <= 0) cepOrder = Math.Clamp(N / 10, 15, 35);
        int K = Math.Min(cepOrder, N);
        float[] cep0 = PartialDctII(v0, K);
        float[] cep1 = PartialDctII(v1, K);
        
        // 補間済み低次ケプストラムと最近傍の差分（ケプストラム領域で計算）
        bool useFrame0 = ratio < 0.5f;
        float[] delta = new float[K];
        if (useFrame0)
            for (int k = 0; k < K; k++) delta[k] = ratio * (cep1[k] - cep0[k]);
        else
            for (int k = 0; k < K; k++) delta[k] = (1 - ratio) * (cep0[k] - cep1[k]);
        
        // 差分をスペクトル領域に変換し、最近傍スペクトルに加算
        float[] deltaSpec = PartialIdctII(delta, N);
        float[] vNearest = useFrame0 ? v0 : v1;
        float[] result = new float[N];
        for (int i = 0; i < N; i++)
            result[i] = vNearest[i] + deltaSpec[i];
        
        return result;
    }
    
    /// <summary>
    /// ケプストラム分離4点Akima補間 (高速版): 計算量 O(5*K*N) ≈ O(125*N)
    /// </summary>
    static float[] CepstralInterpolateVtmagnCubic(float[] vtmagn0, float[] vtmagn1, 
        float[] vtmagn2, float[] vtmagn3, float ratio, int cepOrder = -1)
    {
        int N = Math.Min(Math.Min(vtmagn0.Length, vtmagn1.Length), 
                         Math.Min(vtmagn2.Length, vtmagn3.Length));
        float[] v0 = vtmagn0.Length == N ? vtmagn0 : vtmagn0[..N];
        float[] v1 = vtmagn1.Length == N ? vtmagn1 : vtmagn1[..N];
        float[] v2 = vtmagn2.Length == N ? vtmagn2 : vtmagn2[..N];
        float[] v3 = vtmagn3.Length == N ? vtmagn3 : vtmagn3[..N];
        
        // Adaptive K: N/10をベースに15～35の範囲でスペクトル解像度に応じて調整
        if (cepOrder <= 0) cepOrder = Math.Clamp(N / 10, 15, 35);
        int K = Math.Min(cepOrder, N);
        float[] cep0 = PartialDctII(v0, K);
        float[] cep1 = PartialDctII(v1, K);
        float[] cep2 = PartialDctII(v2, K);
        float[] cep3 = PartialDctII(v3, K);
        
        // Akima補間済み低次ケプストラムと最近傍の差分
        bool useFrame1 = ratio < 0.5f;
        float[] cepNearest = useFrame1 ? cep1 : cep2;
        float[] delta = new float[K];
        for (int k = 0; k < K; k++)
            delta[k] = AkimaInterp(cep0[k], cep1[k], cep2[k], cep3[k], ratio) - cepNearest[k];
        
        float[] deltaSpec = PartialIdctII(delta, N);
        float[] vNearest = useFrame1 ? v1 : v2;
        float[] result = new float[N];
        for (int i = 0; i < N; i++)
            result[i] = vNearest[i] + deltaSpec[i];
        
        return result;
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
                    magnitudes[i] = Math.Max(-120f, magnitudes[i]);  // 下限-120dB
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
    /// スペクトル傾斜を適用（Tフラグ）
    /// フォルマントを保持しながらスペクトル全体の傾きを変更
    /// </summary>
    static void ApplySpectralTilt(ChunkHandle chunk, int spectralTilt, int fs)
    {
        int nfrm = Llsm.GetNumFrames(chunk);
        
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            float f0 = Llsm.GetFrameF0(frame);
            if (f0 <= 0) continue;  // 無声音はスキップ
            
            // VTMAGNを取得（対数振幅スペクトル）
            var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (vtmagnPtr == IntPtr.Zero) continue;
            
            int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
            float[] vtmagn = new float[nspec];
            Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
            
            // Step 1: フォルマントピーク検出（元のスペクトルから）
            List<(int index, float magnitude)> formantPeaks = DetectFormantPeaks(vtmagn, fs, nspec);
            
            // Step 2: スペクトル傾斜を適用
            for (int j = 0; j < nspec; j++)
            {
                float freqKhz = (float)j / nspec * (fs / 2000.0f);
                float tiltDb = spectralTilt * MathF.Log2(MathF.Max(freqKhz, 0.1f));
                vtmagn[j] += tiltDb;
            }
            
            // Step 3: フォルマントピーク周辺の振幅を元のレベルに復元（適応的強度）
            foreach (var (peakIdx, origMagnitude) in formantPeaks)
            {
                float currentMagnitude = vtmagn[peakIdx];
                float correction = origMagnitude - currentMagnitude;
                
                // 復元強度を方向に応じて調整
                float strength = spectralTilt < 0 ? 0.5f : 0.7f;
                correction *= strength;
                
                // ガウシアンウィンドウで周辺に復元を適用
                int bandwidthBins = Math.Max(3, nspec / 100);
                for (int k = -bandwidthBins; k <= bandwidthBins; k++)
                {
                    int idx = peakIdx + k;
                    if (idx >= 0 && idx < nspec)
                    {
                        float sigma = bandwidthBins / 2.0f;
                        float weight = MathF.Exp(-(k * k) / (2 * sigma * sigma));
                        vtmagn[idx] += correction * weight;
                    }
                }
            }
            
            // VTMAGNフロアクランプ
            for (int j = 0; j < nspec; j++)
                vtmagn[j] = Math.Max(vtmagn[j], -80.0f);
            Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
        }
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
        // formantFollow=0 → 完全固定（1.0倍、声質保持）
        // formantFollow=50 → 50%追従（自然なバランス）
        // formantFollow=100 → 完全追従（処理スキップ、デフォルト動作）
        float followRatio = formantFollow / 100.0f;
        
        // 目標のフォルマント比率（1.0 = 元の周波数）
        // followRatio=0 → 1.0（固定）
        // followRatio=0.5 → 1.0 + (pitchRatio-1.0)*0.5（50%追従）
        // followRatio=1.0 → pitchRatio（完全追従、でもここには来ない）
        float targetFormantRatio = 1.0f + (pitchRatio - 1.0f) * followRatio;
        
        // ピッチがpitchRatio倍になると、F0が上がることでフォルマントもpitchRatio倍に聞こえる
        // これを相殺してtargetFormantRatioにするため、VTMAGNを逆方向にシフト
        // 
        // formantShiftRatio = pitchRatio / targetFormantRatio
        // 
        // 例: pitchRatio=1.5（F0が1.5倍）、F=0（固定）の場合
        //   targetFormantRatio = 1.0
        //   formantShiftRatio = 1.5 / 1.0 = 1.5
        //   VTMAGNを1.5倍に引き伸ばす
        float formantShiftRatio = pitchRatio / targetFormantRatio;
        
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
    /// グロウル効果のPBPコールバック（Pulse-by-Pulse synthesis）
    /// 声帯の不規則な振動をシミュレート：サブハーモニクス、ジッター、声門波形変調
    /// </summary>
    class GrowlEffectState
    {
        public int PeriodCount;
        public float Oscillator1;  // F0/3 のサブハーモニクス
        public float Oscillator2;  // F0/5 のサブハーモニクス
        public Random Random;
        public float Strength;  // 0.0-1.0
        
        public GrowlEffectState(float strength)
        {
            PeriodCount = 0;
            Oscillator1 = 0;
            Oscillator2 = 0;
            Random = new Random();
            Strength = strength / 100.0f;  // 1-100 → 0.01-1.0
        }
    }
    
    static void GrowlEffectCallback(ref NativeLLSM.llsm_gfm gfm, ref float delta_t, IntPtr info, IntPtr src_frame)
    {
        // infoポインタからGrowlEffectStateを取得
        GCHandle handle = GCHandle.FromIntPtr(info);
        GrowlEffectState state = (GrowlEffectState)handle.Target;
        
        state.PeriodCount++;
        
        // 複数のLFOを組み合わせて自然な揺らぎを生成（素数周期で規則性を回避）
        float lfo1 = MathF.Sin(state.PeriodCount * 2 * MathF.PI / 47.3f);
        float lfo2 = MathF.Sin(state.PeriodCount * 2 * MathF.PI / 31.7f);
        float lfo = (lfo1 + lfo2 * 0.6f) / 1.6f;
        
        // 複数のサブハーモニクスを混合（F0/3 と F0/5）
        // 周波数をLFOで微妙に揺らす
        float subfreq1 = 3.0f + lfo * 0.3f;  // F0/3 ± 10%
        float subfreq2 = 5.0f + lfo * 0.5f;  // F0/5 ± 10%
        
        state.Oscillator1 += 2 * MathF.PI / subfreq1;
        state.Oscillator2 += 2 * MathF.PI / subfreq2;
        
        float osc1 = MathF.Sin(state.Oscillator1);
        float osc2 = MathF.Sin(state.Oscillator2);
        
        // 2つのサブハーモニクスを混合（F0/3を主、F0/5を副）
        float osc = osc1 * 0.7f + osc2 * 0.3f;
        
        // ガウス分布的なジッター（Box-Muller近似）
        float jitter1 = (float)state.Random.NextDouble() - 0.5f;
        float jitter2 = (float)state.Random.NextDouble() - 0.5f;
        float jitter = (jitter1 + jitter2) * 1.4142f;
        delta_t = gfm.T0 * 0.01f * jitter * state.Strength;
        
        // 元の変調量を維持（クランプなし）
        gfm.Fa *= 1.0f - osc * 0.5f * state.Strength;
        gfm.Rk *= 1.0f + osc * 0.3f * state.Strength;
        gfm.Ee *= 1.0f - osc * 0.5f * state.Strength;
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
        
        int n = utauT.Length;
        float span = utauT[1] - utauT[0];
        
        // === Akima補間: 各データ点での傾きを事前計算 ===
        // 差分 m[i] = (y[i+1] - y[i]) / (x[i+1] - x[i])
        float[] m = new float[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            m[i] = (utauPb[i + 1] - utauPb[i]) / span;
        }
        
        // 各点での傾き s[i] を計算（Akimaの重み付け方式）
        // 隣接する差分の差の絶対値で重み付けし、オーバーシュートを抑制
        float[] s = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (i == 0)
            {
                // 左端: 外挿で仮想的な差分を追加
                s[i] = m[0];
            }
            else if (i == 1)
            {
                // 2番目: 3点で計算（前方の仮想点を使用）
                float m_minus1 = 2f * m[0] - m[1];  // 仮想差分
                float w1 = MathF.Abs(m[1] - m[0]);
                float w2 = MathF.Abs(m[0] - m_minus1);
                if (w1 + w2 > 1e-10f)
                    s[i] = (w1 * m[0] + w2 * m[1]) / (w1 + w2);  // 修正: i=1のとき m[i-1]=m[0], m[i]=m[1]
                else
                    s[i] = (m[0] + m[1]) * 0.5f;
            }
            else if (i >= n - 2)
            {
                if (i == n - 1)
                {
                    // 右端
                    s[i] = m[n - 2];
                }
                else
                {
                    // n-2番目
                    float m_plus1 = 2f * m[n - 2] - m[n - 3];  // 仮想差分
                    float w1 = MathF.Abs(m_plus1 - m[i]);
                    float w2 = MathF.Abs(m[i] - m[i - 1]);
                    if (w1 + w2 > 1e-10f)
                        s[i] = (w1 * m[i - 1] + w2 * m[i]) / (w1 + w2);
                    else
                        s[i] = (m[i - 1] + m[i]) * 0.5f;
                }
            }
            else
            {
                // 内部点: Akimaの標準公式
                // s[i] = (w1 * m[i-1] + w2 * m[i]) / (w1 + w2)
                // w1 = |m[i+1] - m[i]|, w2 = |m[i-1] - m[i-2]|
                float w1 = MathF.Abs(m[i + 1] - m[i]);
                float w2 = MathF.Abs(m[i - 1] - m[i - 2]);
                if (w1 + w2 > 1e-10f)
                    s[i] = (w1 * m[i - 1] + w2 * m[i]) / (w1 + w2);
                else
                    s[i] = (m[i - 1] + m[i]) * 0.5f;
            }
        }
        
        // === 各出力点を補間 ===
        float[] result = new float[outputT.Length];
        
        for (int i = 0; i < outputT.Length; i++)
        {
            float t = outputT[i];
            
            // double精度で区間インデックスを計算（長ノートでの累積誤差防止）
            int index = (int)((double)(t - utauT[0]) / (double)span);
            if (index < 0) index = 0;
            if (index >= n - 1) index = n - 2;
            
            // Hermite補間（Akimaの傾きを使用）
            float t0 = utauT[index];
            float t1 = utauT[index + 1];
            float h = t1 - t0;
            float u = (t - t0) / h;  // 0～1の正規化パラメータ
            float u2 = u * u;
            float u3 = u2 * u;
            
            // Hermite基底関数
            float h00 = 2f * u3 - 3f * u2 + 1f;
            float h10 = u3 - 2f * u2 + u;
            float h01 = -2f * u3 + 3f * u2;
            float h11 = u3 - u2;
            
            result[i] = h00 * utauPb[index] + h10 * h * s[index]
                       + h01 * utauPb[index + 1] + h11 * h * s[index + 1];
        }
        
        return result;
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
                                                                  consonantFrames, 1.0f, 2.0f, new List<int>(), 120, 50, 0, 100, 0.005f, false, false, 100, false, 0, 0, 0, false, 0, 50, false, false);
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
        
        // Élastique スタイル比較テスト
        RunElasticComparisonTest();
    }
    
    /// <summary>
    /// Élastique スタイルアルゴリズムの比較テスト
    /// </summary>
    static void RunElasticComparisonTest()
    {
        Console.WriteLine("\n=== Test 10: Elastic Stretch Comparison ===");
        
        string wavPath = @"G:\UTAU_voices\ARUARU\あ.wav";
        if (!File.Exists(wavPath))
        {
            Console.WriteLine($"  Test wav file not found: {wavPath}");
            return;
        }
        
        var (segment, wavFs) = Wav.ReadMono(wavPath);
        
        Console.WriteLine($"  Loaded: {wavPath} ({segment.Length} samples, {wavFs}Hz)");
        
        // ピッチ検出
        int nhop = (int)(Thop * wavFs);
        var f0 = Pyin.Analyze(segment, wavFs, nhop, 60, 800);
        Console.WriteLine($"  F0 estimation: {f0.Length} frames");
        
        float avgF0 = f0.Where(x => x > 0).DefaultIfEmpty(150).Average();
        Console.WriteLine($"  Average F0: {avgF0:F1} Hz");
        
        // 解析
        using var aopt = Llsm.CreateAnalysisOptions();
        using var chunk = Llsm.Analyze(aopt, segment, wavFs, f0, f0.Length);
        int nframes = Llsm.GetNumFrames(chunk);
        Console.WriteLine($"  Analysis: {nframes} frames");
        
        // Test parameters: slow consonant (velocity=50)
        int consonantFrames = Math.Min(10, nframes / 4);
        float velocity = 50.0f;  // 50% = consonant will be slow and potentially blurred
        float consonantStretch = 100.0f / velocity;
        float lengthRatio = 1.0f;  // no stretch in vowel region
        
        Console.WriteLine($"  Test: consonant={consonantFrames} frames, velocity={velocity}% (stretch={consonantStretch:F2}x)");
        
        // Test 1: Current algorithm (uniform interpolation)
        Console.WriteLine("\n  [1] Current algorithm (uniform cubic interpolation)...");
        using (var chunkCopy1 = Llsm.CopyChunk(chunk))
        {
            var result1 = SynthesizeWithConsonantAndStretch(
                chunkCopy1, wavFs, avgF0, avgF0,
                consonantFrames, consonantStretch, lengthRatio,
                new List<int>(), 120, 50, 0, 100, Thop,
                false, false, 100, false, 0, 0, 0, false, 0, 50
            );
            
            Wav.WriteMono16("test_stretch_current.wav", result1, wavFs);
            Console.WriteLine($"      Wrote: test_stretch_current.wav ({result1.Length} samples)");
        }
        
        // Test 2: Elastic algorithm (transient-preserving)
        Console.WriteLine("\n  [2] Elastic algorithm (transient-preserving)...");
        using (var chunkCopy2 = Llsm.CopyChunk(chunk))
        {
            var result2 = SynthesizeWithElasticStretch(
                chunkCopy2, wavFs, avgF0, avgF0,
                consonantFrames, consonantStretch, lengthRatio,
                new List<int>(), 120, 50, 0, 100, Thop,
                false, false, 100, false, 0, 0, 0, false, 0, 50
            );
            
            Wav.WriteMono16("test_stretch_elastic.wav", result2, wavFs);
            Console.WriteLine($"      Wrote: test_stretch_elastic.wav ({result2.Length} samples)");
        }
        
        Console.WriteLine("\n  Compare the two files:");
        Console.WriteLine("    - test_stretch_current.wav: uniform interpolation (may blur consonants)");
        Console.WriteLine("    - test_stretch_elastic.wav: transient-preserving (clearer consonants)");
        Console.WriteLine("  Expected: Elastic version should have sharper consonant attacks");
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
        
        // Layer1 に変換（NFFT=16384: 2xオーバーサンプリング分析対応）
        Llsm.ChunkToLayer1(srcChunk, 16384);
        DownsampleChunkSpectrum(srcChunk, fs);
        
        // 適応的フォルマント処理（Fフラグ）をsrcChunkに適用（Layer1状態、逆位相伝播前）
        // 注意：この処理は現在無効化されています（VTMAGNアクセスに問題があるため）
        // 代わりに、F0シフトのみを行い、フォルマント処理は後で行います
        
        // 逆位相伝播
        Llsm.ChunkPhasePropagate(srcChunk, -1);
        
        int nfrm = Llsm.GetNumFrames(srcChunk);
        
        float pitchRatio = targetF0 / srcF0;
        float amplitudeCompensation = -20.0f * MathF.Log10(pitchRatio);
        
        // ソースのスペクトル傾斜から適応的なtiltCoeffを算出
        float srcSpectralSlope = MeasureAverageSpectralSlope(srcChunk);
        float adaptiveTiltCoeff = ComputeAdaptiveTiltCoeff(srcSpectralSlope);
        
        // 各フレームの F0 を変更 + VTMAGN補正 + HM削除（test-layer1-anasynth.c準拠）
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
                
                float newF0 = adjustedSourceF0 * pitchRatio;  // 揺らぎを保持してシフト
                
                // P1: ピッチ上昇時にVSPHSEを外挿して高調波欠落を防止
                if (newF0 > originalF0 * 1.1f)
                {
                    ExtendVsphseForPitchShift(frame.Ptr, originalF0, newF0, fs);
                }
                
                Llsm.SetFrameF0(frame, newF0);
                
                // ピッチシフトによる振幅補正 + 周波数依存傾斜補正
                float tiltCoeff = adaptiveTiltCoeff;
                float spectralTiltCompensation = -tiltCoeff * MathF.Log2(pitchRatio);
                var vtmagnPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr != IntPtr.Zero)
                {
                    int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                    float[] vtmagn = new float[nspec];
                    Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                    for (int j = 0; j < nspec; j++)
                    {
                        float normalizedFreq = (float)j / nspec;
                        vtmagn[j] = Math.Max(vtmagn[j] + amplitudeCompensation + spectralTiltCompensation * normalizedFreq, -80.0f);
                    }
                    Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
                }
                
                // Fix #9: HMを削除（Layer1パラメータからtolayer0で再生成させる）
                // test-layer1-anasynth.c: llsm_container_attach(chunk->frames[i], LLSM_FRAME_HM, NULL, NULL, NULL)
                NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_HM,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                
                // デバッグ: 最初と最後のフレームを出力
                if (i == 0 || i == nfrm - 1)
                {
                    Console.WriteLine($"    [Frame {i}] srcF0={originalF0:F1} (dev={deviation:+0.0;-0.0}, mod={dynamicMod:F0}%) -> newF0={newF0:F1}Hz (vtmagn{amplitudeCompensation:+0.0;-0.0}dB)");
                }
            }
        }
        
        // Layer0 に変換（HM削除済みフレームはLayer1パラメータから再生成される）
        Llsm.ChunkToLayer0(srcChunk);
        
        // Chunk-level RPS（位相連続性の確保）
        NativeLLSM.llsm_chunk_phasesync_rps(srcChunk.DangerousGetHandle(), 0);
        
        // 順方向位相伝播
        Llsm.ChunkPhasePropagate(srcChunk, +1);
        
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, srcChunk);
        if (_needDecomposedOutput)
        {
            var (y, ySin, yNoise) = Llsm.ReadOutputDecomposed(output);
            _lastSynthSin = ySin;
            _lastSynthNoise = yNoise;
            return y;
        }
        return Llsm.ReadOutput(output);
    }
    
    /// <summary>
    /// タイムストレッチして合成（フレームコピー方式）
    /// </summary>
    static float[] SynthesizeWithStretch(ChunkHandle srcChunk, int fs, float stretchRatio)
    {
        // Layer1 に変換（NFFT=16384: 2xオーバーサンプリング分析対応）
        Llsm.ChunkToLayer1(srcChunk, 16384);
        DownsampleChunkSpectrum(srcChunk, fs);
        
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
        
        // Chunk-level RPS（位相連続性の確保）
        NativeLLSM.llsm_chunk_phasesync_rps(dstChunk.DangerousGetHandle(), 0);
        
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
        
        // Layer1 に変換（NFFT=16384: 2xオーバーサンプリング分析対応）
        Llsm.ChunkToLayer1(srcChunk, 16384);
        DownsampleChunkSpectrum(srcChunk, fs);
        
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
                
                // Fix #4: ピッチシフトによる振幅補正（test-layer1-anasynth.c準拠）
                float amplComp = -20.0f * MathF.Log10(pitchRatio);
                var vtmagnPtr = NativeLLSM.llsm_container_get(newFramePtr, NativeLLSM.LLSM_FRAME_VTMAGN);
                if (vtmagnPtr != IntPtr.Zero)
                {
                    int nspec = NativeLLSM.llsm_fparray_length(vtmagnPtr);
                    float[] vtmagn = new float[nspec];
                    Marshal.Copy(vtmagnPtr, vtmagn, 0, nspec);
                    for (int j = 0; j < nspec; j++)
                    {
                        vtmagn[j] = Math.Max(vtmagn[j] + amplComp, -80.0f);
                    }
                    Marshal.Copy(vtmagn, 0, vtmagnPtr, nspec);
                }
                
                // Fix #9: HMを削除（Layer1パラメータからtolayer0で再生成させる）
                NativeLLSM.llsm_container_attach_(newFramePtr, NativeLLSM.LLSM_FRAME_HM,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            
            Llsm.SetFrame(dstChunk, i, newFramePtr);
        }
        
        // Layer0 に変換（HM削除済みフレームはLayer1パラメータから再生成される）
        Llsm.ChunkToLayer0(dstChunk);
        
        // Chunk-level RPS（位相連続性の確保）
        NativeLLSM.llsm_chunk_phasesync_rps(dstChunk.DangerousGetHandle(), 0);
        
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
