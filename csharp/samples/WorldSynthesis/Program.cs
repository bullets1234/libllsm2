using System;
using System.IO;
using LlsmBindings;

namespace LlsmBindings.Samples.WorldSynthesis
{
    class Program
    {
    // 使用方法: dotnet run -- <worldFeatureDir> <fs> [csv]
    // worldFeatureDir 内に DIO/WORLD などで出力した F0, spectrogram, aperiodicity を想定して読み込み、LLSM で音声合成します。
    //
    // 入力フォーマットは 2 通りをサポートします:
    //  (1) RAW float32 (.raw32f)
    //      - f0.raw32f        : [nfrm]           フレームごとの F0 [Hz] (無声は 0)
    //      - sp.raw32f        : [nfrm * nspec]   対数振幅スペクトル (自然対数)
    //      - ap.raw32f (未使用): [nfrm * nspec] 非周期性 (ここでは利用せず)
    //  (2) CSV
    //      - f0.csv           : nfrm 行 1 列 (F0[Hz])
    //      - sp.csv           : nfrm 行 nspec 列 (列方向が周波数)
    //      ※ 区切り文字はカンマ想定。空行はスキップされます。
    //
    // LLSM 側では、WORLD の log-spectrum をそのまま VTMAGN (dB) として各フレームにセットし、F0 も登録して合成します。

    static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: dotnet run -c Release -- <worldFeatureDir> <fs> [csv]");
            return;
        }

        string featDir = Path.GetFullPath(args[0]);
        if (!Directory.Exists(featDir))
        {
            Console.WriteLine($"Directory not found: {featDir}");
            return;
        }

        if (!int.TryParse(args[1], out int fs))
        {
            Console.WriteLine("fs must be integer (e.g., 48000)");
            return;
        }

        bool useCsv = args.Length >= 3 &&
                      (args[2].Equals("csv", StringComparison.OrdinalIgnoreCase) ||
                       args[2].Equals("1") ||
                       args[2].Equals("true", StringComparison.OrdinalIgnoreCase));

        float[] f0;
        float[] sp;

        if (useCsv)
        {
            string f0Csv = Path.Combine(featDir, "f0.csv");
            string spCsv = Path.Combine(featDir, "sp.csv");
            if (!File.Exists(f0Csv) || !File.Exists(spCsv))
            {
                Console.WriteLine("CSV モードですが f0.csv または sp.csv が見つかりません。");
                return;
            }

            Console.WriteLine($"Loading WORLD CSV features from {featDir}");
            f0 = ReadCsvVector(f0Csv);
            float[][] sp2d = ReadCsvMatrix(spCsv);
            int nfrmCsv = sp2d.Length;
            if (f0.Length != nfrmCsv)
            {
                Console.WriteLine($"CSV 行数が一致しません: f0={f0.Length}, spRows={nfrmCsv}");
                return;
            }
            if (nfrmCsv == 0)
            {
                Console.WriteLine("CSV が空です。");
                return;
            }
            int nspecCsv = sp2d[0].Length;
            // 2D -> 1D フラット配列へ詰め替え
            sp = new float[nfrmCsv * nspecCsv];
            for (int i = 0; i < nfrmCsv; i++)
            {
                if (sp2d[i].Length != nspecCsv)
                {
                    Console.WriteLine($"sp.csv の列数が行ごとに異なります (row={i})");
                    return;
                }
                Array.Copy(sp2d[i], 0, sp, i * nspecCsv, nspecCsv);
            }
        }
        else
        {
            string f0Path = Path.Combine(featDir, "f0.raw32f");
            string spPath = Path.Combine(featDir, "sp.raw32f");
            string apPath = Path.Combine(featDir, "ap.raw32f"); // 未使用だが存在チェックのみ

            if (!File.Exists(f0Path) || !File.Exists(spPath))
            {
                Console.WriteLine("f0.raw32f または sp.raw32f が見つかりません。");
                return;
            }

            Console.WriteLine($"Loading WORLD RAW features from {featDir}");
            f0 = ReadFloatRaw(f0Path);
            sp = ReadFloatRaw(spPath);
        }
        int nfrm = f0.Length;
        if (sp.Length % nfrm != 0)
        {
            Console.WriteLine($"sp size mismatch: spLen={sp.Length}, nfrm={nfrm}");
            return;
        }
        int nspec = sp.Length / nfrm;
        Console.WriteLine($"nfrm={nfrm}, nspec={nspec}, fs={fs}");

        // 解析オプションを作り、まずはダミー無音を解析して土台となる chunk を作成
        using var aopt = Llsm.CreateAnalysisOptions();
        int nhop = 128; // 仮のホップ長 [サンプル]（PYIN と合わせてもよい）
        int nx = nfrm * nhop;
        var dummy = new float[nx]; // 全て 0 の無音
        using var chunk = Llsm.Analyze(aopt, dummy, fs, f0, nfrm);

        // conf には触らず、WORLD 側の nspec をそのまま使う
        int confNspec = nspec;
        Console.WriteLine($"Use WORLD nspec={confNspec}");

        // WORLD の nspec をそのまま VTMAGN として各フレームに割り当てる
        float[][] vtMagnPerFrame = new float[nfrm][];
        for (int fi = 0; fi < nfrm; fi++)
        {
            var src = new float[nspec];
            Array.Copy(sp, fi * nspec, src, 0, nspec);
            vtMagnPerFrame[fi] = (float[])src.Clone();
        }

        int nfrmChunk = Llsm.GetNumFrames(chunk);
        if (nfrmChunk != nfrm)
        {
            Console.WriteLine($"chunk nfrm mismatch: chunk={nfrmChunk}, world={nfrm}");
            return;
        }

        // 各フレームに F0 と VTMAGN をセット
        for (int i = 0; i < nfrm; i++)
        {
            var frame = Llsm.GetFrame(chunk, i);
            Llsm.SetFrameF0(frame, f0[i]);
            Llsm.SetFrameVtMagn(frame, vtMagnPerFrame[i]);
        }

        // 位相を前方向に伝播させ、滑らかにする
        Llsm.ChunkPhasePropagate(chunk, +1);

        // 合成
        using var sopt = Llsm.CreateSynthesisOptions(fs);
        using var output = Llsm.Synthesize(sopt, chunk);
        float[] y = Llsm.ReadOutput(output);

        string outWav = Path.Combine(featDir, "world_synth.wav");
        Wav.WriteMono16(outWav, y, fs);
        Console.WriteLine($"Wrote {outWav}");
    }

    static float[] ReadFloatRaw(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int n = bytes.Length / 4;
        float[] data = new float[n];
        Buffer.BlockCopy(bytes, 0, data, 0, n * 4);
        return data;
    }

    static float[] ResampleLinear(float[] src, int dstLen)
    {
        if (src.Length == dstLen) return (float[])src.Clone();
        float[] dst = new float[dstLen];
        int n = src.Length;
        for (int i = 0; i < dstLen; i++)
        {
            float pos = (float)i * (n - 1) / Math.Max(1, dstLen - 1);
            int i0 = (int)MathF.Floor(pos);
            int i1 = Math.Min(n - 1, i0 + 1);
            float w = pos - i0;
            float a = src[i0];
            float b = src[i1];
            dst[i] = a + (b - a) * w;
        }
        return dst;
    }

    static float[] ReadCsvVector(string path)
    {
        var lines = File.ReadAllLines(path);
        var list = new System.Collections.Generic.List<float>();
        foreach (var l in lines)
        {
            var s = l.Trim();
            if (string.IsNullOrEmpty(s)) continue;
            var cols = s.Split(',');
            if (cols.Length == 0) continue;
            if (float.TryParse(cols[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                list.Add(v);
        }
        return list.ToArray();
    }

    static float[][] ReadCsvMatrix(string path)
    {
        var lines = File.ReadAllLines(path);
        var rows = new System.Collections.Generic.List<float[]>();
        foreach (var l in lines)
        {
            var s = l.Trim();
            if (string.IsNullOrEmpty(s)) continue;
            var cols = s.Split(',');
            var row = new float[cols.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i].Trim();
                if (string.IsNullOrEmpty(c)) { row[i] = 0f; continue; }
                if (!float.TryParse(c, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out row[i]))
                    row[i] = 0f;
            }
            rows.Add(row);
        }
        return rows.ToArray();
    }
    }
}
