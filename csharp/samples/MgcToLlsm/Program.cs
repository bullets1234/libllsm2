using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LlsmBindings;

/// <summary>
/// ENUNU の mgc/bap/f0/vuv から LLSM Layer0 に直接設定して合成するサンプル
/// coder を経由せず、harmonic model に直接スペクトルを設定
/// </summary>
class Program
{
    // WORLD/ENUNU の標準パラメータ
    const float MgcAlpha = 0.55f;  // mel-generalized cepstrum の alpha
    const int FftSize = 2048;      // FFT サイズ
    const float FrameShift = 5.0f; // ms
    
    static void Main(string[] args)
    {
        string baseDir = @"G:\libllsm2\csharp\samples\TestCoder\enunu_format";
        string mgcPath = Path.Combine(baseDir, "0_acoustic_mgc.csv");
        string bapPath = Path.Combine(baseDir, "0_acoustic_bap.csv");
        string f0Path = Path.Combine(baseDir, "0_acoustic_f0.csv");
        string vuvPath = Path.Combine(baseDir, "0_acoustic_vuv.csv");
        
        Console.WriteLine("=== ENUNU mgc/bap → LLSM Layer0 直接合成 ===");
        
        // 特徴量読み込み
        var mgcData = ReadCsv2D(mgcPath);
        var bapData = ReadCsv2D(bapPath);
        var f0Data = ReadCsv1D(f0Path);
        var vuvData = ReadCsv1D(vuvPath);
        
        int nfrm = Math.Min(Math.Min(mgcData.Length, bapData.Length), 
                           Math.Min(f0Data.Length, vuvData.Length));
        Console.WriteLine($"Frames: {nfrm} (mgc={mgcData.Length}, bap={bapData.Length}, f0={f0Data.Length}, vuv={vuvData.Length})");
        Console.WriteLine($"MGC order: {mgcData[0].Length}, BAP bands: {bapData[0].Length}");
        
        int fs = 48000;
        float fnyq = fs / 2.0f;
        float thop = 0.005f;  // 5ms
        
        // ソース音声から解析して conf を作成（パラメータを取得するため）
        string sourceWav = @"G:\libllsm2\csharp\samples\SmokeTest\0.wav";
        Console.WriteLine($"Loading source wav for conf: {sourceWav}");
        
        var (wavSamples, wavFs) = Wav.ReadMono(sourceWav);
        int nhop = (int)(0.005f * wavFs);
        float[] f0Pyin = Pyin.Analyze(wavSamples, wavFs, nhop, 60, 800);
        
        using var aopt = Llsm.CreateAnalysisOptions();
        using var srcChunk = Llsm.Analyze(aopt, wavSamples, wavFs, f0Pyin, f0Pyin.Length);
        
        // Layer1 に変換して conf パラメータを設定
        Llsm.ChunkToLayer1(srcChunk, 2048);
        
        var conf = Llsm.GetConf(srcChunk);
        
        // conf の内容を確認
        var nspecPtr = NativeLLSM.llsm_container_get(conf.Ptr, NativeLLSM.LLSM_CONF_NSPEC);
        var maxnharPtr = NativeLLSM.llsm_container_get(conf.Ptr, NativeLLSM.LLSM_CONF_MAXNHAR);
        var maxnharEPtr = NativeLLSM.llsm_container_get(conf.Ptr, NativeLLSM.LLSM_CONF_MAXNHAR_E);
        var npsdPtr = NativeLLSM.llsm_container_get(conf.Ptr, NativeLLSM.LLSM_CONF_NPSD);
        var nchannelPtr = NativeLLSM.llsm_container_get(conf.Ptr, NativeLLSM.LLSM_CONF_NCHANNEL);
        
        int nspec = Marshal.ReadInt32(nspecPtr);
        int maxnhar = Marshal.ReadInt32(maxnharPtr);
        int maxnhar_e = Marshal.ReadInt32(maxnharEPtr);
        int npsd = Marshal.ReadInt32(npsdPtr);
        int nchannel = Marshal.ReadInt32(nchannelPtr);
        
        Console.WriteLine($"conf: NSPEC={nspec}, MAXNHAR={maxnhar}, MAXNHAR_E={maxnhar_e}, NPSD={npsd}, NCHANNEL={nchannel}");
        
        // ソースから有声フレームを取得して、LLSM が期待するスペクトルを確認
        Console.WriteLine("\n=== LLSM Layer0 の確認（ソース音声）===");
        int srcNfrm = Llsm.GetNumFrames(srcChunk);
        
        // Layer0 に戻す
        Llsm.ChunkToLayer0(srcChunk);
        
        for (int i = 0; i < srcNfrm; i += srcNfrm / 5)
        {
            var frame = Llsm.GetFrame(srcChunk, i);
            float f0Val = Llsm.GetFrameF0(frame);
            if (f0Val > 0)
            {
                // HM (harmonic model) を取得
                var hmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_HM);
                if (hmPtr != IntPtr.Zero)
                {
                    var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
                    Console.WriteLine($"Frame {i}: f0={f0Val:F1}, nhar={hm.nhar}");
                    
                    // 振幅を読み取り
                    float[] ampl = new float[Math.Min(hm.nhar, 10)];
                    Marshal.Copy(hm.ampl, ampl, 0, ampl.Length);
                    Console.Write($"  ampl[0..{ampl.Length-1}]: ");
                    for (int j = 0; j < ampl.Length; j++) Console.Write($"{ampl[j]:F4} ");
                    Console.WriteLine();
                    
                    // 対数振幅で確認
                    Console.Write($"  log(ampl)[0..4]: ");
                    for (int j = 0; j < Math.Min(5, ampl.Length); j++) 
                        Console.Write($"{(ampl[j] > 0 ? Math.Log(ampl[j]) : -100):F2} ");
                    Console.WriteLine();
                }
                
                // NM (noise model) を取得
                var nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
                if (nmPtr != IntPtr.Zero)
                {
                    var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                    Console.WriteLine($"  NM: npsd={nm.npsd}, nchannel={nm.nchannel}");
                    
                    // PSD を読み取り
                    if (nm.psd != IntPtr.Zero && nm.npsd > 0)
                    {
                        float[] psd = new float[Math.Min(nm.npsd, 5)];
                        Marshal.Copy(nm.psd, psd, 0, psd.Length);
                        Console.Write($"  psd[0..{psd.Length-1}]: ");
                        for (int j = 0; j < psd.Length; j++) Console.Write($"{psd[j]:F2} ");
                        Console.WriteLine();
                    }
                }
                break;
            }
        }
        
        // ENUNU mgc → スペクトル変換のテスト
        Console.WriteLine("\n=== ENUNU mgc → スペクトル変換 ===");
        for (int i = 0; i < nfrm; i += nfrm / 5)
        {
            if (vuvData[i] > 0.5f)
            {
                float[] mgc = mgcData[i];
                float f0 = f0Data[i];
                
                // mgc → 振幅スペクトル
                float[] spectrum = MgcToSpectrum(mgc, nspec, fs);
                
                Console.WriteLine($"Frame {i}: f0={f0:F1}");
                Console.Write($"  mgc[0..4]: ");
                for (int j = 0; j < 5; j++) Console.Write($"{mgc[j]:F2} ");
                Console.Write($"\n  spectrum[0..4]: ");
                for (int j = 0; j < 5; j++) Console.Write($"{spectrum[j]:F4} ");
                Console.Write($"\n  log(spectrum)[0..4]: ");
                for (int j = 0; j < 5; j++) Console.Write($"{(spectrum[j] > 0 ? Math.Log(spectrum[j]) : -100):F2} ");
                Console.WriteLine();
                
                // 調波成分に分解
                int nhar = (int)(fnyq / f0);
                float[] harAmpl = new float[Math.Min(nhar, 10)];
                for (int h = 0; h < harAmpl.Length; h++)
                {
                    float freq = f0 * (h + 1);
                    int bin = (int)(freq / fnyq * (nspec - 1));
                    if (bin < nspec) harAmpl[h] = spectrum[bin];
                }
                Console.Write($"  harAmpl[0..{harAmpl.Length-1}]: ");
                for (int j = 0; j < harAmpl.Length; j++) Console.Write($"{harAmpl[j]:F4} ");
                Console.WriteLine();
                break;
            }
        }
        
        // 新しいチャンクを作成
        Console.WriteLine($"\nCreating new chunk with {nfrm} frames...");
        
        // conf をコピーして nfrm を設定
        var confCopy = Llsm.CopyContainer(conf);
        var nfrmPtr = NativeLLSM.llsm_container_get(confCopy.Ptr, NativeLLSM.LLSM_CONF_NFRM);
        Marshal.WriteInt32(nfrmPtr, nfrm);
        
        var newChunk = Llsm.CreateChunk(confCopy, 0);
        
        Console.WriteLine("Converting mgc/bap to LLSM frames...");
        int voicedCount = 0;
        
        for (int i = 0; i < nfrm; i++)
        {
            float vuv = vuvData[i];
            float f0 = vuv > 0.5f ? f0Data[i] : 0;
            
            if (vuv > 0.5f) voicedCount++;
            
            // フレームを作成
            int nhar = f0 > 0 ? Math.Min((int)(fnyq / f0), maxnhar) : 0;
            IntPtr framePtr = NativeLLSM.llsm_create_frame(nhar, nchannel, maxnhar_e, npsd);
            
            // F0 を設定
            var f0Ptr = NativeLLSM.llsm_create_fp(f0);
            NativeLLSM.llsm_container_attach_(framePtr, NativeLLSM.LLSM_FRAME_F0, 
                f0Ptr, Marshal.GetFunctionPointerForDelegate(DeleteFp), IntPtr.Zero);
            
            if (f0 > 0 && nhar > 0)
            {
                // mgc → スペクトル
                float[] mgc = mgcData[i];
                float[] spectrum = MgcToSpectrum(mgc, nspec, fs);
                
                // HM (harmonic model) を取得して振幅を設定
                var hmPtr = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_HM);
                if (hmPtr != IntPtr.Zero)
                {
                    var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
                    
                    // 調波振幅を計算（線形補間を使用）
                    float[] harAmpl = new float[nhar];
                    float[] harPhse = new float[nhar];
                    
                    for (int h = 0; h < nhar; h++)
                    {
                        float freq = f0 * (h + 1);
                        float fidx = freq / fnyq * (nspec - 1);
                        int idx0 = (int)fidx;
                        int idx1 = Math.Min(idx0 + 1, nspec - 1);
                        float frac = fidx - idx0;
                        
                        if (idx0 >= 0 && idx0 < nspec)
                        {
                            // 線形補間でスムーズなサンプリング
                            harAmpl[h] = spectrum[idx0] * (1 - frac) + spectrum[idx1] * frac;
                        }
                        harPhse[h] = 0; // 位相は後で伝播
                    }
                    
                    // ampl と phse を設定
                    Marshal.Copy(harAmpl, 0, hm.ampl, nhar);
                    Marshal.Copy(harPhse, 0, hm.phse, nhar);
                }
                
                // NM (noise model) - bap から設定
                var nmPtr = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_NM);
                if (nmPtr != IntPtr.Zero)
                {
                    var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
                    
                    // bap → noise PSD (簡易実装)
                    float[] bap = bapData[i];
                    float[] noisePsd = new float[nm.npsd];
                    
                    // bap から noise level を推定（dB スケール）
                    float avgBap = 0;
                    for (int j = 0; j < bap.Length; j++)
                    {
                        // ENUNU bap は dB、負の値
                        avgBap += bap[j];
                    }
                    avgBap /= bap.Length;
                    
                    // noise PSD を設定（bap に基づく）
                    for (int k = 0; k < nm.npsd; k++)
                    {
                        // 周波数に応じた bap を使用
                        float freq = (float)k / nm.npsd * fnyq;
                        int bandIdx = Math.Min((int)(freq / fnyq * bap.Length), bap.Length - 1);
                        float bapVal = bap[bandIdx];
                        
                        // bap (dB) → noise power
                        // ENUNU bap: -2 前後が典型的、0 に近いほどノイズが多い
                        // LLSM psd: dB スケール、スペクトルから導出
                        noisePsd[k] = bapVal * 10.0f - 60.0f;  // 調整
                    }
                    
                    Marshal.Copy(noisePsd, 0, nm.psd, nm.npsd);
                }
            }
            
            Llsm.SetFrame(newChunk, i, framePtr);
        }
        
        Console.WriteLine($"  Voiced frames: {voicedCount}/{nfrm}");
        
        // 位相伝播
        Console.WriteLine("Propagating phase...");
        Llsm.ChunkPhasePropagate(newChunk, +1);
        
        // 合成
        Console.WriteLine("Synthesizing...");
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, newChunk);
        var y = Llsm.ReadOutput(output);
        
        Console.WriteLine($"Output samples: {y.Length}");
        
        // 出力
        string outPath = Path.Combine(Environment.CurrentDirectory, "mgc_to_llsm.wav");
        Wav.WriteMono16(outPath, y, fs);
        Console.WriteLine($"Wrote {outPath}");
    }
    
    // DeleteFp デリゲート
    delegate void DeleteFpDelegate(IntPtr ptr);
    static readonly DeleteFpDelegate DeleteFp = NativeLLSM.llsm_delete_fp;
    
    /// <summary>
    /// mgc (mel-generalized cepstrum) → 振幅スペクトル
    /// 
    /// WORLD/ENUNU の mgc は α=0.55 の bilinear transform による warped cepstrum
    /// warped 周波数軸から線形周波数軸への変換が必要
    /// </summary>
    static float[] MgcToSpectrum(float[] mgc, int nspec, int fs)
    {
        float alpha = MgcAlpha;  // 0.55
        float fnyq = fs / 2.0f;
        
        // Step 1: mgc から warped-log スペクトルを計算 (DCT の逆変換相当)
        // warped-cepstrum → warped-log-spectrum
        int nwarp = 1024;  // warped 周波数軸のビン数
        float[] warpedLogSpec = new float[nwarp];
        
        for (int k = 0; k < nwarp; k++)
        {
            // warped 周波数軸上の位置 (0 ~ π)
            float warpedOmega = (float)Math.PI * k / (nwarp - 1);
            
            float sum = mgc[0];  // DC 成分
            for (int n = 1; n < mgc.Length; n++)
            {
                // ケプストラム → log スペクトル: cos 変換
                sum += 2.0f * mgc[n] * (float)Math.Cos(n * warpedOmega);
            }
            warpedLogSpec[k] = sum;
        }
        
        // Step 2: warped 周波数軸 → 線形周波数軸 への変換
        // bilinear transform: z' = (z - α) / (1 - α*z)
        // 周波数軸での関係: tan(ω'/2) = (1+α)/(1-α) * tan(ω/2)
        // つまり: ω = 2 * atan((1-α)/(1+α) * tan(ω'/2))
        // 
        // ここでは線形 ω から warped ω' を求める（逆方向）
        float[] linearLogSpec = new float[nspec];
        
        for (int k = 0; k < nspec; k++)
        {
            // 線形周波数軸上の位置 (0 ~ π)
            float omega = (float)Math.PI * k / (nspec - 1);
            
            // 線形 ω から warped ω' を計算
            // tan(ω'/2) = (1+α)/(1-α) * tan(ω/2)
            float tanHalfOmega = (float)Math.Tan(omega / 2.0);
            float tanHalfWarpedOmega = (1 + alpha) / (1 - alpha) * tanHalfOmega;
            float warpedOmega = 2.0f * (float)Math.Atan(tanHalfWarpedOmega);
            
            // warpedOmega を [0, π] にクランプ
            warpedOmega = Math.Clamp(warpedOmega, 0, (float)Math.PI);
            
            // warped スペクトルのインデックス
            float warpedIdx = warpedOmega / (float)Math.PI * (nwarp - 1);
            
            // 線形補間
            int idx0 = (int)warpedIdx;
            int idx1 = Math.Min(idx0 + 1, nwarp - 1);
            float frac = warpedIdx - idx0;
            
            if (idx0 >= 0 && idx0 < nwarp)
                linearLogSpec[k] = warpedLogSpec[idx0] * (1 - frac) + warpedLogSpec[idx1] * frac;
        }
        
        // Step 3: スケーリング調整
        // LLSM が期待する値域に合わせる
        float offset = 8.0f;
        
        // Step 4: exp で振幅スペクトルに変換
        float[] spectrum = new float[nspec];
        for (int k = 0; k < nspec; k++)
        {
            spectrum[k] = (float)Math.Exp(linearLogSpec[k] + offset);
        }
        
        return spectrum;
    }
    
    static float HzToMel(float hz)
    {
        return 2595.0f * (float)Math.Log10(1.0f + hz / 700.0f);
    }
    
    static float MelToHz(float mel)
    {
        return 700.0f * ((float)Math.Pow(10.0, mel / 2595.0) - 1.0f);
    }
    
    /// <summary>
    /// 周波数変換 (freqt): α-warped cepstrum を線形 cepstrum に変換
    /// SPTK の freqt 相当
    /// 
    /// Reference: Tokuda, K. et al., "Mel-generalized cepstral analysis"
    /// </summary>
    static float[] FrequencyTransform(float[] c, int order, float alpha)
    {
        int m1 = c.Length;
        int m2 = order;
        float[] g = new float[m2 + 1];
        float[] d = new float[m2 + 1];
        
        for (int i = -m1 + 1; i <= 0; i++)
        {
            int idx = -i;
            if (idx < c.Length)
            {
                d[0] = g[0];
                g[0] = c[idx];
                
                if (m2 >= 1)
                    g[1] = (1 - alpha * alpha) * d[0] + alpha * g[1];
                
                for (int j = 2; j <= m2; j++)
                    g[j] = g[j - 1] + alpha * (d[j - 1] - g[j]);
                
                // d を更新
                Array.Copy(g, d, m2 + 1);
            }
        }
        
        return g;
    }
    
    static float[][] ReadCsv2D(string path)
    {
        var lines = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        return lines.Select(l => l.Split(',').Select(float.Parse).ToArray()).ToArray();
    }
    
    static float[] ReadCsv1D(string path)
    {
        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(float.Parse)
            .ToArray();
    }
}

// Wav クラス（SmokeTest からコピー）
public static class Wav
{
    public static void WriteMono16(string path, float[] samples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
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
