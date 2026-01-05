using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LlsmBindings;

class Program
{
    // 使用方法: dotnet run -- <semitones> <stretchFactor> [spectral]
    // 例: +4半音 & 1.25倍時間伸長（スペクトルシフト有効） -> dotnet run -- 4 1.25 1
    static void Main(string[] args)
    {
        Console.WriteLine("Pitch/Time Demo: loading 0.wav (WORLD-synthesized)");
        string inPath = Path.Combine(Environment.CurrentDirectory, "0.wav");
        if (!File.Exists(inPath))
        {
            Console.WriteLine("0.wav が見つかりません。サンプルフォルダに配置してください。");
            return;
        }

        // パラメータ: ピッチシフト半音数 / タイムストレッチ倍率
        int semitones = 4; // デフォルト +4半音
        float stretch = 1.0f; // デフォルト 時間変更なし
        if (args.Length > 0 && int.TryParse(args[0], out var st)) semitones = st;
        if (args.Length > 1 && float.TryParse(args[1], out var sf)) stretch = sf;
        bool spectral = false;
        if (args.Length > 2) spectral = (args[2] == "1" || args[2].Equals("true", StringComparison.OrdinalIgnoreCase));
        
        // ENUNU F0 を使うかどうか (第4引数)
        bool useEnunuF0 = args.Length > 3 && (args[3] == "1" || args[3].Equals("true", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"semitones={semitones}, stretch={stretch:F2}, spectral={(spectral?"on":"off")}, useEnunuF0={useEnunuF0}");

        // WAV 読み込み
        var x = Wav.ReadMonoFloat(inPath, out int fs);
        Console.WriteLine($"Input: {inPath}, fs={fs}, samples={x.Length}");

        // まず PYIN で F0 推定して解析（スペクトル情報を正しく抽出するため）
        int nhop = (int)(0.005f * fs); // 5ms hop (LLSM デフォルト)
        float[] f0_pyin = Pyin.Analyze(x, fs, nhop: nhop, fmin: 60, fmax: 800);
        Console.WriteLine($"PYIN F0 frames: {f0_pyin.Length}, nhop={nhop} ({nhop*1000.0f/fs:F2}ms)");
        var voicedF0 = f0_pyin.Where(v => v > 0).ToArray();
        if (voicedF0.Length > 0)
        {
            Console.WriteLine($"  Voiced frames: {voicedF0.Length}, min={voicedF0.Min():F1}Hz, max={voicedF0.Max():F1}Hz, avg={voicedF0.Average():F1}Hz");
        }

        // 解析（PYIN の F0 を使用）
        using var aopt = Llsm.CreateAnalysisOptions();
        using var chunk = Llsm.Analyze(aopt, x, fs, f0_pyin, nfrm: f0_pyin.Length);
        int nfrm = Llsm.GetNumFrames(chunk);
        Console.WriteLine($"Chunk frames: {nfrm}");

        // ENUNU F0 を使う場合は、解析後に F0 を置き換える
        float[] f0 = f0_pyin; // デフォルトは PYIN の F0
        if (useEnunuF0)
        {
            Console.WriteLine("Replacing F0 with ENUNU F0...");
            string enunuF0Path = @"G:\libllsm2\csharp\samples\TestCoder\enunu_format\0_acoustic_f0.csv";
            string vuvPath = @"G:\libllsm2\csharp\samples\TestCoder\enunu_format\0_acoustic_vuv.csv";
            
            var lf0Lines = File.ReadAllLines(enunuF0Path);
            var vuvLines = File.ReadAllLines(vuvPath);
            
            // ENUNU は 5ms hop, LLSM も 5ms hop なので 1:1 マッピング
            float enunuHopMs = 5.0f;
            float llsmHopMs = 5.0f; // nhop / fs * 1000
            
            f0 = new float[nfrm];
            int enunuVoiced = 0;
            for (int j = 0; j < nfrm; j++)
            {
                float timeMs = j * llsmHopMs;
                int enunuIdx = (int)(timeMs / enunuHopMs);
                if (enunuIdx < lf0Lines.Length && enunuIdx < vuvLines.Length)
                {
                    float vuv = float.Parse(vuvLines[enunuIdx]);
                    if (vuv > 0.5f)
                    {
                        float f0Val = float.Parse(lf0Lines[enunuIdx]);
                        // 注: このファイルは直接 F0 値（Hz）なので exp() は不要
                        f0[j] = f0Val;
                        enunuVoiced++;
                    }
                }
                
                // フレームの F0 を更新
                var frame = Llsm.GetFrame(chunk, j);
                Llsm.SetFrameF0(frame, f0[j]);
            }
            Console.WriteLine($"  ENUNU F0 applied: {enunuVoiced} voiced frames out of {nfrm}");
            
            // ENUNU F0 の統計
            var enunuVoicedF0 = f0.Where(v => v > 0).ToArray();
            if (enunuVoicedF0.Length > 0)
            {
                Console.WriteLine($"  ENUNU F0 stats: min={enunuVoicedF0.Min():F1}Hz, max={enunuVoicedF0.Max():F1}Hz, avg={enunuVoicedF0.Average():F1}Hz");
            }
        }
        
        // conf のパラメータを確認
        var conf = Llsm.GetConf(chunk);
        float thop = Llsm.GetThopSeconds(conf);
        Console.WriteLine($"thop={thop:F6}s ({thop*1000:F3}ms), expected={128.0/fs:F6}s ({128.0/fs*1000:F3}ms)");
        Console.WriteLine($"Expected output samples from thop: {(int)(nfrm * thop * fs)}");

        // ピッチシフト: F0 を半音数分スケール (2^(semitones/12))
        float pitchMul = (float)Math.Pow(2.0, semitones / 12.0);
        bool doPitchShift = Math.Abs(pitchMul - 1.0) > 1e-6;
        
        if (doPitchShift)
        {
            // Layer1 に変換して位相情報を準備
            Llsm.ChunkToLayer1(chunk, 2048);
            Llsm.ChunkPhasePropagate(chunk, -1);
            
            for (int i = 0; i < nfrm; i++)
            {
                var frame = Llsm.GetFrame(chunk, i);
                float f0orig = f0[i];
                if (f0orig > 0) Llsm.SetFrameF0(frame, f0orig * pitchMul);
            }
            
            // オプション: スペクトルもピッチ方向にスケーリング（Layer1）
            if (spectral)
            {
                // ログ軸 + Envelope 平滑化でのスペクトルシフト
                Llsm.ApplySpectralShiftLog(chunk, pitchMul, nfft: 2048, smoothWin: 7);
                // 無声での残留ハーモニクス抑制（-9 dB）
                Llsm.AttenuateUnvoiced(chunk, uvDb: -9f);
            }
            
            // Layer0 に戻して位相伝播
            Llsm.ChunkToLayer0(chunk);
            Llsm.ChunkPhasePropagate(chunk, +1);
        }

        // タイムストレッチ: フレーム補間方式（thopは変えない）
        ChunkHandle chunkToSynth = chunk;
        if (Math.Abs(stretch - 1.0f) > 0.01f)
        {
            Console.WriteLine($"Time stretch {stretch:F2}x using frame interpolation...");
            
            // Layer1 に変換
            Llsm.ChunkToLayer1(chunk, 2048);
            // 逆方向位相伝播
            Llsm.ChunkPhasePropagate(chunk, -1);
            
            int nfrmNew = (int)(nfrm * stretch);
            Console.WriteLine($"  Original frames: {nfrm}, New frames: {nfrmNew}");
            
            // 新しい chunk を作成
            var confCopy = Llsm.CopyContainer(conf);
            // NFRM を更新
            var nfrmPtr = NativeLLSM.llsm_container_get(confCopy.Ptr, NativeLLSM.LLSM_CONF_NFRM);
            Marshal.WriteInt32(nfrmPtr, nfrmNew);
            var chunkNew = Llsm.CreateChunk(confCopy, 0);
            
            // フレーム補間
            for (int i = 0; i < nfrmNew; i++)
            {
                float mapped = (float)i * nfrm / nfrmNew;
                int baseIdx = (int)mapped;
                float ratio = mapped - baseIdx;
                baseIdx = Math.Min(baseIdx, nfrm - 1);
                
                // 単純にベースフレームをコピー（補間は省略）
                var srcFrame = Llsm.GetFrame(chunk, baseIdx);
                var dstFrame = Llsm.CopyContainer(srcFrame);
                Llsm.SetFrame(chunkNew, i, dstFrame.Ptr);
            }
            
            // Layer0 に戻す
            Llsm.ChunkToLayer0(chunkNew);
            // 順方向位相伝播
            Llsm.ChunkPhasePropagate(chunkNew, +1);
            
            chunkToSynth = chunkNew;
            nfrm = nfrmNew;
        }

        // 合成
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, chunkToSynth);
        var y = Llsm.ReadOutput(output);
        Console.WriteLine($"Output samples: {y.Length}, expected ~{(int)(x.Length * stretch)}");

        // 出力ファイル名生成
        string tag = $"ps{semitones:+#;-#;0}_ts{stretch:F2}{(spectral?"_sp1":"_sp0")}".Replace('.', '_');
        string outPath = Path.Combine(Environment.CurrentDirectory, $"out_{tag}.wav");
        Wav.WriteMono16(outPath, y, fs);
        Console.WriteLine($"Wrote {outPath}");
    }
}
