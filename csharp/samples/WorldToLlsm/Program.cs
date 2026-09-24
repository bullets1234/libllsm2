using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LlsmBindings;

/// <summary>
/// WORLD sp/ap/f0 (dump_features.py の出力) から LLSM Layer0 を構築して合成するサンプル。
/// NNSVS/ENUNU の音響特徴量を LLSM で発声させるための実験プロジェクト。
///
/// 使い方:
///   WorldToLlsm features.bin out.wav [--gain dB] [--noise-gain dB] [--no-noise]
///                                    [--layer1] [--rd val] [--ref ref.wav]
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: WorldToLlsm <features.bin> <out.wav> [--gain dB] [--noise-gain dB] [--no-noise] [--no-layer1] [--rd val] [--ref ref.wav]");
            return 1;
        }
        string featPath = args[0];
        string outPath = args[1];
        float gainDb = 3.0f, noiseGainDb = 0, rdOverride = 0; // +3dB: pyworld 参照とのレベル較正済み
        bool noNoise = false, useLayer1 = true;
        string? refPath = null;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--gain": gainDb = float.Parse(args[++i]); break;
                case "--noise-gain": noiseGainDb = float.Parse(args[++i]); break;
                case "--no-noise": noNoise = true; break;
                case "--layer1": useLayer1 = true; break;
                case "--no-layer1": useLayer1 = false; gainDb = 0; break;
                case "--rd": rdOverride = float.Parse(args[++i]); break;
                case "--ref": refPath = args[++i]; break;
                default: Console.WriteLine($"Unknown option: {args[i]}"); return 1;
            }
        }

        var d = FeatureDump.Load(featPath);
        float fs = d.Fs;
        float fnyq = fs / 2;
        float thop = d.PeriodMs / 1000f;
        Console.WriteLine($"features: nfrm={d.NFrm}, fs={d.Fs}, nbin={d.NBin}, period={d.PeriodMs}ms");

        // conf をゼロから構築 (他の解析結果は借用しない)
        using var aopt = Llsm.CreateAnalysisOptions();
        {
            // maxnhar を引き上げ (低い f0 でも fnyq までカバー)
            var ao = Marshal.PtrToStructure<NativeLLSM.llsm_aoptions>(aopt.DangerousGetHandle());
            ao.thop = thop;
            ao.maxnhar = 400;
            Marshal.StructureToPtr(ao, aopt.DangerousGetHandle(), false);
        }
        var conf = Llsm.AOptionsToConf(aopt, fnyq);
        Llsm.SetConfInt(conf, NativeLLSM.LLSM_CONF_NFRM, d.NFrm);

        var chunk = Llsm.CreateChunk(conf, 0); // conf はコピーされる
        NativeLLSM.llsm_delete_container(conf.Ptr);

        var cconf = Llsm.GetConf(chunk);
        int maxnhar = Llsm.GetConfInt(cconf, NativeLLSM.LLSM_CONF_MAXNHAR);
        int maxnharE = Llsm.GetConfInt(cconf, NativeLLSM.LLSM_CONF_MAXNHAR_E);
        int npsd = Llsm.GetConfInt(cconf, NativeLLSM.LLSM_CONF_NPSD);
        int nchannel = Llsm.GetConfInt(cconf, NativeLLSM.LLSM_CONF_NCHANNEL);
        Console.WriteLine($"conf: MAXNHAR={maxnhar}, MAXNHAR_E={maxnharE}, NPSD={npsd}, NCHANNEL={nchannel}, THOP={Llsm.GetConfFloat(cconf, NativeLLSM.LLSM_CONF_THOP)}");

        float gainLin = MathF.Pow(10f, gainDb / 20f);
        int voiced = 0;
        float[] psdBuf = new float[npsd];
        float[] edcBuf = new float[nchannel];
        for (int c = 0; c < nchannel; c++) edcBuf[c] = 1.0f; // 平坦なノイズ包絡 (絶対レベルは psd フィルタ側で正規化される)

        for (int i = 0; i < d.NFrm; i++)
        {
            float f0 = d.F0[i];
            int nhar = f0 > 0 ? Math.Min(maxnhar, (int)(fnyq * 0.95f / f0)) : 0;
            if (f0 > 0) voiced++;

            IntPtr framePtr = NativeLLSM.llsm_create_frame(nhar, nchannel, f0 > 0 ? maxnharE : 0, npsd);
            var frame = new ContainerRef(framePtr);
            Llsm.SetFrameF0(frame, f0);

            if (nhar > 0)
            {
                // 調波振幅: a_k = sqrt(sp(k f0) * (1 - ap^2) * 2 * f0 / fs) * gain
                var hmPtr = Llsm.GetFrameHM(frame);
                var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
                float[] ampl = new float[nhar];
                for (int k = 0; k < nhar; k++)
                {
                    float freq = f0 * (k + 1);
                    float spv = d.InterpSp(i, freq, fnyq);
                    float apv = d.InterpAp(i, freq, fnyq);
                    float pw = spv * MathF.Max(0f, 1f - apv * apv);
                    ampl[k] = MathF.Sqrt(MathF.Max(pw, 0f) * 2f * f0 / fs) * gainLin;
                }
                Marshal.Copy(ampl, 0, hm.ampl, nhar);
                // phse はゼロのまま (後段の phasepropagate で時間発展を与える)
            }

            // NM: WORLD ap からノイズ PSD を構築
            var nmPtr = NativeLLSM.llsm_container_get(framePtr, NativeLLSM.LLSM_FRAME_NM);
            var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
            if (noNoise)
            {
                for (int j = 0; j < npsd; j++) psdBuf[j] = -120f;
            }
            else
            {
                for (int j = 0; j < npsd; j++)
                {
                    float freq = (float)j / (npsd - 1) * fnyq;
                    float spv = d.InterpSp(i, freq, fnyq);
                    float apv = f0 > 0 ? d.InterpAp(i, freq, fnyq) : 1.0f;
                    float noisePow = spv * apv * apv;
                    // LLSM psd 規約: psd_dB = 10*log10(PSD_linear * 44100/fs)
                    psdBuf[j] = 10f * MathF.Log10(noisePow * 44100f / fs + 1e-12f) + noiseGainDb;
                }
            }
            Marshal.Copy(psdBuf, 0, nm.psd, npsd);
            Marshal.Copy(edcBuf, 0, nm.edc, nchannel);

            Llsm.SetFrame(chunk, i, framePtr);
        }
        Console.WriteLine($"frames built: voiced={voiced}/{d.NFrm}");

        if (useLayer1)
        {
            // Layer1 経由: LF 声門モデルで再構成 (Rd/K フラグ系の効果を利用可能にする経路)
            Console.WriteLine("Layer0 -> Layer1 -> Layer0 roundtrip...");
            Llsm.ChunkToLayer1(chunk, 2048);
            if (rdOverride > 0)
            {
                for (int i = 0; i < d.NFrm; i++)
                {
                    var frame = Llsm.GetFrame(chunk, i);
                    if (Llsm.GetFrameF0(frame) > 0) Llsm.SetFrameRd(frame, rdOverride);
                }
                Console.WriteLine($"Rd override: {rdOverride}");
            }
            Llsm.ChunkToLayer0(chunk);
            NativeLLSM.llsm_chunk_phasesync_rps(chunk.DangerousGetHandle(), 1);
        }

        Llsm.ChunkPhasePropagate(chunk, +1);

        Console.WriteLine("Synthesizing...");
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, chunk);
        float[] y = Llsm.ReadOutput(output);

        float peak = 0, sumSq = 0;
        foreach (var v in y) { peak = MathF.Max(peak, MathF.Abs(v)); sumSq += v * v; }
        float rms = MathF.Sqrt(sumSq / y.Length);
        Console.WriteLine($"output: {y.Length / fs:F2}s, peak={peak:F3}, rms={rms:F5} ({20 * MathF.Log10(rms + 1e-10f):F1} dBFS)");

        if (refPath != null && File.Exists(refPath))
        {
            var (refY, refFs) = Wav.ReadMono16(refPath);
            float refSumSq = 0;
            foreach (var v in refY) refSumSq += v * v;
            float refRms = MathF.Sqrt(refSumSq / refY.Length);
            float diffDb = 20 * MathF.Log10((rms + 1e-10f) / (refRms + 1e-10f));
            Console.WriteLine($"reference: rms={refRms:F5}, diff={diffDb:+0.0;-0.0} dB -> suggested --gain {gainDb - diffDb:F1}");
        }

        if (peak > 0.99f)
        {
            float s = 0.99f / peak;
            for (int i = 0; i < y.Length; i++) y[i] *= s;
            Console.WriteLine($"normalized by {20 * MathF.Log10(s):F1} dB (clipping prevention)");
        }
        Wav.WriteMono16(outPath, y, (int)fs);
        Console.WriteLine($"wrote {outPath}");

        chunk.Dispose();
        return 0;
    }
}

/// <summary>dump_features.py が出力するバイナリ (W2L1) のリーダ</summary>
class FeatureDump
{
    public int Fs, NFrm, NBin;
    public float PeriodMs;
    public float[] F0 = Array.Empty<float>();
    public float[] Sp = Array.Empty<float>(); // [nfrm * nbin] linear power
    public float[] Ap = Array.Empty<float>(); // [nfrm * nbin] linear 0..1

    public static FeatureDump Load(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        var magic = br.ReadBytes(4);
        if (Encoding.ASCII.GetString(magic) != "W2L1")
            throw new InvalidDataException("bad magic (expected W2L1)");
        var d = new FeatureDump
        {
            Fs = br.ReadInt32(),
            NFrm = br.ReadInt32(),
            NBin = br.ReadInt32(),
            PeriodMs = br.ReadSingle()
        };
        d.F0 = ReadFloats(br, d.NFrm);
        d.Sp = ReadFloats(br, d.NFrm * d.NBin);
        d.Ap = ReadFloats(br, d.NFrm * d.NBin);
        return d;
    }

    static float[] ReadFloats(BinaryReader br, int n)
    {
        var bytes = br.ReadBytes(n * 4);
        if (bytes.Length != n * 4) throw new InvalidDataException("truncated file");
        var a = new float[n];
        Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
        return a;
    }

    // WORLD の周波数軸: bin b = b * fnyq / (nbin-1)。線形補間でサンプル。
    public float InterpSp(int frame, float freq, float fnyq) => Interp(Sp, frame, freq, fnyq);
    public float InterpAp(int frame, float freq, float fnyq) => Interp(Ap, frame, freq, fnyq);

    float Interp(float[] a, int frame, float freq, float fnyq)
    {
        float fidx = freq / fnyq * (NBin - 1);
        if (fidx <= 0) return a[frame * NBin];
        if (fidx >= NBin - 1) return a[frame * NBin + NBin - 1];
        int i0 = (int)fidx;
        float frac = fidx - i0;
        int b = frame * NBin + i0;
        return a[b] * (1 - frac) + a[b + 1] * frac;
    }
}

static class Wav
{
    public static void WriteMono16(string path, float[] samples, int sampleRate)
    {
        using var bw = new BinaryWriter(File.Create(path));
        int dataLen = samples.Length * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2);
        bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        foreach (var v in samples)
            bw.Write((short)(Math.Clamp(v, -1f, 1f) * 32767));
    }

    public static (float[] samples, int fs) ReadMono16(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(12); // RIFF + size + WAVE
        int fs = 0; short channels = 1, bits = 16;
        while (br.BaseStream.Position < br.BaseStream.Length - 8)
        {
            string id = Encoding.ASCII.GetString(br.ReadBytes(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16(); channels = br.ReadInt16();
                fs = br.ReadInt32(); br.ReadInt32(); br.ReadInt16();
                bits = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new NotSupportedException("16-bit only");
                int n = size / 2 / channels;
                var y = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float acc = 0;
                    for (int c = 0; c < channels; c++) acc += br.ReadInt16();
                    y[i] = acc / channels / 32768f;
                }
                return (y, fs);
            }
            else br.ReadBytes(size);
        }
        throw new InvalidDataException("no data chunk");
    }
}
