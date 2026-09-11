using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NnsvsOnnx;

internal static class Program
{
    private const string TestDataDir = @"g:\libllsm2\csharp\samples\NnsvsOnnx\testdata";
    private const string OnnxDir = @"g:\libllsm2\csharp\samples\WorldToLlsm\onnx";
    private const string ModelDir = @"G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN";
    private const string SmokeOutDir = @"g:\libllsm2\csharp\samples\NnsvsOnnx\smoke_out";

    private static bool s_allOk = true;

    private static float[] ReadBin(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestDataDir, name));
        var arr = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    private static long[] ReadBinInt64(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestDataDir, name));
        var arr = new long[bytes.Length / 8];
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    private static float[] ToFlat(float[,] feats)
    {
        var flat = new float[feats.Length];
        Buffer.BlockCopy(feats, 0, flat, 0, flat.Length * 4);
        return flat;
    }

    /// <summary>Mirrors predict_timelag/predict_duration's force_clip_input_features:
    /// after MinMaxScaler.transform, clip all non-pitch columns to [lo, hi].</summary>
    private static void ClipNonPitch(float[] x, int t, int dim, IReadOnlyList<int> pitchIndices, double lo, double hi)
    {
        var pitchSet = new HashSet<int>(pitchIndices);
        for (int i = 0; i < t; i++)
        {
            for (int d = 0; d < dim; d++)
            {
                if (pitchSet.Contains(d)) continue;
                int idx = i * dim + d;
                double v = x[idx];
                if (v < lo) v = lo;
                if (v > hi) v = hi;
                x[idx] = (float)v;
            }
        }
    }

    private static double MaxAbsDiff(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new InvalidOperationException($"length mismatch: {a.Length} vs {b.Length}");
        double max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = Math.Abs((double)a[i] - b[i]);
            if (d > max) max = d;
        }
        return max;
    }

    private static void Report(string label, int t, double maxDiff, double threshold)
    {
        bool ok = maxDiff < threshold;
        s_allOk &= ok;
        Console.WriteLine($"  [{label}] T={t} max|diff|={maxDiff:E3} (thr {threshold:E1}) {(ok ? "OK" : "NG!")}");
    }

    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--pipeline"))
        {
            Console.WriteLine("=== full pipeline smoke test (Stage C-5) ===");
            FullPipeline.RunCase("0");
            FullPipeline.RunCase("1");
            return;
        }

        if (args.Contains("--pipeline-dump"))
        {
            Console.WriteLine("=== full pipeline debug dump (for postfilter gold comparison) ===");
            FullPipeline.RunCase("0", dumpDebug: true);
            FullPipeline.RunCase("1", dumpDebug: true);
            return;
        }

        if (args.Contains("--pipeline-fulldiff"))
        {
            // A/B: full K_step DDPM sampler vs the production PLMS(10) fast sampler,
            // to check whether the fast sampler is degrading mgc/bap quality
            // (root-cause investigation for reported "weak/breathy" timbre).
            // noiseSeedTag: "0" shares the exact same initial noise as `--pipeline`'s
            // case "0" run, so the only difference between the two outputs is the
            // sampler itself (PLMS(10) vs full 100-step DDPM), not the random seed.
            Console.WriteLine("=== full pipeline, full-step DDPM sampler (A/B vs PLMS) ===");
            FullPipeline.RunCase("0fulldiff", usePlms: false, inputTag: "0", noiseSeedTag: "0");
            return;
        }

        if (args.Contains("--ust2lab"))
        {
            // Usage: --ust2lab <ust_path> <out_lab> [table_path]
            int j = Array.IndexOf(args, "--ust2lab");
            string up = args[j + 1];
            string outLab = args[j + 2];
            string tp = (j + 3 < args.Length && !args[j + 3].StartsWith("--"))
                ? args[j + 3]
                : Path.Combine(ModelDir, "kana2phonemes.table");
            File.WriteAllLines(outLab, UstToHtsConverter.Convert(up, tp));
            Console.WriteLine($"wrote {outLab}");
            return;
        }

        if (args.Contains("--fromust"))
        {
            // Usage: --fromust <ust_path> <out_tag> [table_path]
            int i = Array.IndexOf(args, "--fromust");
            if (i + 2 >= args.Length)
            {
                Console.WriteLine("Usage: --fromust <ust_path> <out_tag> [table_path]");
                Environment.Exit(2);
                return;
            }
            string ustPath = args[i + 1];
            string outTag = args[i + 2];
            string tablePath = (i + 3 < args.Length && !args[i + 3].StartsWith("--"))
                ? args[i + 3]
                : Path.Combine(ModelDir, "kana2phonemes.table");
            bool useUstPitch = !args.Contains("--no-ust-pitch");
            bool usePlms = !args.Contains("--ddpm");
            int plmsSpeedup = 10;
            int si = Array.IndexOf(args, "--plms-speedup");
            if (si >= 0 && si + 1 < args.Length) plmsSpeedup = int.Parse(args[si + 1]);
            bool useStrided = false;
            int sdi = Array.IndexOf(args, "--sddim");
            if (sdi >= 0)
            {
                useStrided = true;
                usePlms = false;
                if (sdi + 1 < args.Length && !args[sdi + 1].StartsWith("--")) plmsSpeedup = int.Parse(args[sdi + 1]);
            }
            bool useInt8 = args.Contains("--int8");
            bool useGpu = args.Contains("--gpu");
            bool noSegment = args.Contains("--noseg");
            int segParallel = 1;
            int pi = Array.IndexOf(args, "--par");
            if (pi >= 0)
            {
                segParallel = (pi + 1 < args.Length && !args[pi + 1].StartsWith("--"))
                    ? int.Parse(args[pi + 1])
                    : Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
            }

            Console.WriteLine($"=== real UST -> HTS labels -> acoustic -> WORLD decode ({ustPath}) ===");
            if (useStrided) Console.WriteLine($"    strided stochastic sampler (DDIM eta=1), interval={plmsSpeedup} ({100 / plmsSpeedup} steps)");
            else if (usePlms && plmsSpeedup != 10) Console.WriteLine($"    PLMS speedup={plmsSpeedup} ({100 / plmsSpeedup} steps)");
            if (useInt8) Console.WriteLine("    INT8-quantized diffusion models");
            if (useGpu) Console.WriteLine("    DirectML GPU execution (diffusion sessions)");
            if (noSegment) Console.WriteLine("    EXPERIMENT: phrase segmentation DISABLED (single-pass, expect tremolo)");
            if (segParallel > 1) Console.WriteLine($"    segment parallelism = {segParallel}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            FullPipeline.RunFromUst(ustPath, tablePath, outTag, usePlms: usePlms, useUstPitch: useUstPitch, plmsSpeedup: plmsSpeedup, useStrided: useStrided, useInt8: useInt8, segParallel: segParallel, useGpu: useGpu, noSegment: noSegment);
            Console.WriteLine($"    total {sw.Elapsed.TotalSeconds:F1}s");
            return;
        }

        if (args.Contains("--fft-selftest"))
        {
            RunFftSelfTest();
            return;
        }

        if (args.Contains("--postfilter"))
        {
            Console.WriteLine("=== acoustic postfilter (GV + trajectory smoothing) vs Python gold ===");
            RunPostfilter();
            Console.WriteLine();
            Console.WriteLine(s_allOk ? "ALL OK" : "SOME CHECKS FAILED");
            return;
        }

        if (args.Contains("--worldcodec"))
        {
            Console.WriteLine("=== WORLD codec (DecodeSpectralEnvelope / DecodeAperiodicity) vs real pyworld ===");
            RunWorldCodec();
            Console.WriteLine();
            Console.WriteLine(s_allOk ? "ALL OK" : "SOME CHECKS FAILED");
            return;
        }

        if (args.Contains("--ustlabel"))
        {
            Console.WriteLine("=== UST -> HTS full-context-label (utaupy port) vs real ENUNU/utaupy output ===");
            RunUstLabel();
            Console.WriteLine();
            Console.WriteLine(s_allOk ? "ALL OK" : "SOME CHECKS FAILED");
            return;
        }

        var manifestJson = File.ReadAllText(Path.Combine(TestDataDir, "manifest.json"));
        using var manifest = JsonDocument.Parse(manifestJson);
        var root = manifest.RootElement;

        Console.WriteLine("=== timelag / duration (MDN) ===");
        RunMdn(root, "timelag");
        RunMdn(root, "duration");

        Console.WriteLine("=== scalers ===");
        RunScalers(root);

        Console.WriteLine("=== acoustic cascade ===");
        RunAcoustic(root);

        Console.WriteLine("=== linguistic features (HTS labels + qst.hed) ===");
        RunLinguistic();

        Console.WriteLine("=== timing labels ===");
        RunTimingLabels();

        Console.WriteLine();
        Console.WriteLine(s_allOk ? "ALL OK" : "SOME CHECKS FAILED");
    }

    private static void RunLinguistic()
    {
        var manJson = File.ReadAllText(Path.Combine(TestDataDir, "linguistic_manifest.json"));
        using var man = JsonDocument.Parse(manJson);
        var root = man.RootElement;
        long frameShift = root.GetProperty("hts_frame_shift").GetInt64();

        var sw = Stopwatch.StartNew();
        var qs = QuestionSet.Load(Path.Combine(TestDataDir, "linguistic_qst.hed"));
        var pitchIndices = qs.GetPitchIndices();
        Console.WriteLine($"  qst.hed: binary={qs.BinaryDict.Count} numeric={qs.NumericDict.Count} " +
                          $"pitch_indices=[{string.Join(",", pitchIndices)}] (load {sw.ElapsedMilliseconds} ms)");

        bool dimOk = qs.BinaryDict.Count == root.GetProperty("binary_dict_size").GetInt32()
                  && qs.NumericDict.Count == root.GetProperty("numeric_dict_size").GetInt32();
        s_allOk &= dimOk;
        if (!dimOk) Console.WriteLine("  [qst dims] NG!");

        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            string tag = c.GetProperty("tag").GetString()!;
            sw.Restart();

            // timelag: score labels -> round -> note subset -> score-level feats
            var scoreLabels = HtsLabelFile.Load(Path.Combine(TestDataDir, c.GetProperty("score_lab").GetString()!));
            scoreLabels.FrameShift = frameShift;
            scoreLabels.Round();
            var noteLabels = scoreLabels.Select(scoreLabels.GetNoteIndices());
            var timelag = LinguisticFeatures.ScoreLevel(noteLabels, qs);
            LinguisticFeatures.ApplyLogF0Conditioning(timelag, pitchIndices);

            // duration: full phone-level score labels
            var duration = LinguisticFeatures.ScoreLevel(scoreLabels, qs);
            LinguisticFeatures.ApplyLogF0Conditioning(duration, pitchIndices);

            // acoustic: timing labels -> frame-level + coarse_coding
            var timingLabels = HtsLabelFile.Load(Path.Combine(TestDataDir, c.GetProperty("timing_lab").GetString()!));
            var acoustic = LinguisticFeatures.FrameLevel(timingLabels, qs, frameShift);
            LinguisticFeatures.ApplyLogF0Conditioning(acoustic, pitchIndices);
            long elapsed = sw.ElapsedMilliseconds;

            CompareFeat($"linguistic timelag [{tag}]", timelag, c.GetProperty("timelag"));
            CompareFeat($"linguistic duration [{tag}]", duration, c.GetProperty("duration"));
            CompareFeat($"linguistic acoustic [{tag}]", acoustic, c.GetProperty("acoustic"));
            Console.WriteLine($"    (case {tag} elapsed={elapsed} ms)");
        }
    }

    private static void CompareFeat(string label, float[,] feats, JsonElement meta)
    {
        var shape = meta.GetProperty("shape");
        int rows = shape[0].GetInt32(), cols = shape[1].GetInt32();
        var gold = ReadBin(meta.GetProperty("file").GetString()!);
        if (feats.GetLength(0) != rows || feats.GetLength(1) != cols)
        {
            Console.WriteLine($"  [{label}] shape mismatch: got {feats.GetLength(0)}x{feats.GetLength(1)}, want {rows}x{cols} NG!");
            s_allOk = false;
            return;
        }
        var flat = new float[rows * cols];
        Buffer.BlockCopy(feats, 0, flat, 0, flat.Length * 4);
        Report(label, rows, MaxAbsDiff(flat, gold), 1e-4);
    }

    private static void RunPostfilter()
    {
        string dbgDir = Path.Combine(TestDataDir, "postfilter_debug");
        const int mgcDim = 60, bapDim = 5, outDim = mgcDim + 2 + bapDim;
        var gvFull = Npy.Load(Path.Combine(ModelDir, "out_acoustic_scaler_var.npy")).Data;
        var gv = gvFull[..mgcDim];

        foreach (var tag in new[] { "0", "1" })
        {
            string manifestPath = Path.Combine(dbgDir, $"case{tag}_manifest.json");
            if (!File.Exists(manifestPath))
            {
                Console.WriteLine($"  [case {tag}] missing {manifestPath} -- run with --pipeline-dump first. Skipping.");
                s_allOk = false;
                continue;
            }
            using var man = JsonDocument.Parse(File.ReadAllText(manifestPath));
            int t = man.RootElement.GetProperty("T").GetInt32();

            var physical = ReadBin(Path.Combine("postfilter_debug", $"case{tag}_acoustic_physical.bin"));
            var rawPitch = ReadBin(Path.Combine("postfilter_debug", $"case{tag}_score_pitch_raw.bin"));

            var pf = AcousticPostfilter.Apply(physical, t, outDim, mgcDim, bapDim, gv, rawPitch);

            var mgcGvFlat = new float[t * mgcDim];
            Buffer.BlockCopy(pf.MgcGv, 0, mgcGvFlat, 0, mgcGvFlat.Length * 4);
            var mgcFinalFlat = new float[t * mgcDim];
            Buffer.BlockCopy(pf.Mgc, 0, mgcFinalFlat, 0, mgcFinalFlat.Length * 4);
            var bapClippedFlat = new float[t * bapDim];
            Buffer.BlockCopy(pf.Bap, 0, bapClippedFlat, 0, bapClippedFlat.Length * 4);
            var lf0PrefilterFlat = pf.Lf0Prefilter.Select(v => (float)v).ToArray();
            var lf0SmoothedFlat = pf.Lf0Smoothed.Select(v => (float)v).ToArray();

            string goldMgcGv = Path.Combine(dbgDir, $"case{tag}_gold_mgc_gv.bin");
            if (!File.Exists(goldMgcGv))
            {
                Console.WriteLine($"  [case {tag}] missing gold files in {dbgDir} -- run dump_postfilter_gold.py first. Skipping.");
                s_allOk = false;
                continue;
            }

            var goldMgcGvArr = ReadBin(Path.Combine("postfilter_debug", $"case{tag}_gold_mgc_gv.bin"));
            var goldLf0Pre = ReadBin(Path.Combine("postfilter_debug", $"case{tag}_gold_lf0_prefilter.bin"));
            var goldLf0Smoothed = ReadBin(Path.Combine("postfilter_debug", $"case{tag}_gold_lf0_smoothed.bin"));
            var goldMgcFinal = ReadBin(Path.Combine("postfilter_debug", $"case{tag}_gold_mgc_final.bin"));
            var goldBapClipped = ReadBin(Path.Combine("postfilter_debug", $"case{tag}_gold_bap_clipped.bin"));
            var goldF0Final = ReadBin(Path.Combine("postfilter_debug", $"case{tag}_gold_f0_final.bin"));

            Console.WriteLine($"  [case {tag}] T={t} note_frame_indices={pf.NoteFrameIndices.Length}/{t} " +
                              $"({100.0 * pf.NoteFrameIndices.Length / t:F1}%)");

            Report($"postfilter mgc_gv [{tag}]", t, MaxAbsDiff(mgcGvFlat, goldMgcGvArr), 1e-3);
            Report($"postfilter lf0_prefilter [{tag}]", t, MaxAbsDiff(lf0PrefilterFlat, goldLf0Pre), 1e-4);
            Report($"postfilter lf0_smoothed [{tag}]", t, MaxAbsDiff(lf0SmoothedFlat, goldLf0Smoothed), 1e-3);
            Report($"postfilter mgc_final [{tag}]", t, MaxAbsDiff(mgcFinalFlat, goldMgcFinal), 1e-3);
            Report($"postfilter bap_clipped [{tag}]", t, MaxAbsDiff(bapClippedFlat, goldBapClipped), 1e-3);
            Report($"postfilter f0_final [{tag}]", t, MaxAbsDiff(pf.F0Final, goldF0Final), 1e-2);
        }
    }

    /// <summary>Stage D: verifies the C# <see cref="WorldCodec"/> port of WORLD's
    /// DecodeSpectralEnvelope/DecodeAperiodicity against real pyworld output
    /// (see dump_worldcodec_gold.py) on the actual smoke-test CSVs.</summary>
    private static void RunWorldCodec()
    {
        CheckInterp1SelfTest();

        const int fs = 48000;
        const int mgcDim = 60;
        int fftSize = WorldCodec.GetFFTSizeForCheapTrick(fs);
        int numAp = WorldCodec.GetNumberOfAperiodicities(fs);
        bool constOk = fftSize == 2048 && numAp == 5;
        s_allOk &= constOk;
        Console.WriteLine($"  GetFFTSizeForCheapTrick({fs})={fftSize} (expect 2048), " +
                          $"GetNumberOfAperiodicities({fs})={numAp} (expect 5) {(constOk ? "OK" : "NG!")}");

        foreach (var tag in new[] { "0", "1" })
        {
            string caseDir = Path.Combine(SmokeOutDir, $"case{tag}");
            string manifestPath = Path.Combine(caseDir, $"case{tag}_worldcodec_manifest.json");
            if (!File.Exists(manifestPath))
            {
                Console.WriteLine($"  [case {tag}] missing {manifestPath} -- run dump_worldcodec_gold.py first. Skipping.");
                s_allOk = false;
                continue;
            }
            using var man = JsonDocument.Parse(File.ReadAllText(manifestPath));
            int t = man.RootElement.GetProperty("T").GetInt32();
            int nbin = man.RootElement.GetProperty("nbin").GetInt32();

            var mgc = LoadCsvDouble(Path.Combine(caseDir, $"case{tag}_mgc.csv"));
            var bap = LoadCsvDouble(Path.Combine(caseDir, $"case{tag}_bap.csv"));

            var sw = Stopwatch.StartNew();
            var sp = WorldCodec.DecodeSpectralEnvelope(mgc, t, fs, fftSize, mgcDim);
            var ap = WorldCodec.DecodeAperiodicity(bap, t, fs, fftSize);
            long elapsed = sw.ElapsedMilliseconds;

            var spFlat = Flatten(sp);
            var apFlat = Flatten(ap);
            var spGold = ReadBinAbs(Path.Combine(caseDir, $"case{tag}_sp_gold.bin"));
            var apGold = ReadBinAbs(Path.Combine(caseDir, $"case{tag}_ap_gold.bin"));

            if (Environment.GetEnvironmentVariable("WORLDCODEC_DEBUG_DUMP") == "1")
            {
                var bytes = new byte[spFlat.Length * 4];
                Buffer.BlockCopy(spFlat, 0, bytes, 0, bytes.Length);
                File.WriteAllBytes(Path.Combine(caseDir, $"case{tag}_sp_csharp_debug.bin"), bytes);
            }

            Console.WriteLine($"  [case {tag}] T={t} nbin={nbin} (decode {elapsed} ms)");
            ReportWorldCodecDiff($"sp [{tag}]", spFlat, spGold);
            ReportWorldCodecDiff($"ap [{tag}]", apFlat, apGold);
        }
    }

    private static void CheckInterp1SelfTest()
    {
        var yi = WorldCodec.Interp1(new double[] { 0, 1, 2 }, new double[] { 0, 10, 20 }, new double[] { 0.5, 1.5 });
        bool ok = Math.Abs(yi[0] - 5) < 1e-9 && Math.Abs(yi[1] - 15) < 1e-9;
        s_allOk &= ok;
        Console.WriteLine($"  [Interp1 self-check] Interp1([0,1,2],[0,10,20],[0.5,1.5])=[{yi[0]:F3},{yi[1]:F3}] expect=[5,15] {(ok ? "OK" : "NG!")}");
    }

    /// <summary>Verifies the C# UstToHtsConverter port against real ENUNU-produced
    /// full-context-label gold files (0_enutemp/0_score.full and _enutemp/_score.full,
    /// generated by a real run of enulib.utauplugin2score + utaupy on 0_temp.ust /
    /// _temp.ust with strict_sinsy_style=False). Requires exact (line-for-line,
    /// character-for-character) equality since this is text, not floating point.</summary>
    private static void RunUstLabel()
    {
        string tablePath = Path.Combine(ModelDir, "kana2phonemes.table");
        var cases = new (string Ust, string Gold)[]
        {
            (@"G:\0_enutemp\0_temp.ust", @"G:\0_enutemp\0_score.full"),
            (@"G:\_enutemp\_temp.ust", @"G:\_enutemp\_score.full"),
        };

        foreach (var (ustPath, goldPath) in cases)
        {
            if (!File.Exists(ustPath) || !File.Exists(goldPath))
            {
                Console.WriteLine($"  [skip] missing {ustPath} or {goldPath}");
                s_allOk = false;
                continue;
            }

            var sw = Stopwatch.StartNew();
            var lines = UstToHtsConverter.Convert(ustPath, tablePath);
            long elapsed = sw.ElapsedMilliseconds;

            var gold = File.ReadAllLines(goldPath);
            bool ok = lines.Count == gold.Length;
            int firstMismatch = -1;
            int nMismatch = 0;
            int n = Math.Min(lines.Count, gold.Length);
            for (int i = 0; i < n; i++)
            {
                if (lines[i] != gold[i])
                {
                    nMismatch++;
                    if (firstMismatch < 0) firstMismatch = i;
                }
            }
            ok &= nMismatch == 0;
            s_allOk &= ok;

            Console.WriteLine($"  [{Path.GetFileName(ustPath)}] lines={lines.Count} gold_lines={gold.Length} " +
                              $"mismatches={nMismatch} (convert {elapsed} ms) {(ok ? "OK (exact match)" : "NG!")}");
            if (!ok && firstMismatch >= 0)
            {
                Console.WriteLine($"    first mismatch at line {firstMismatch}:");
                Console.WriteLine($"    got:  {(firstMismatch < lines.Count ? lines[firstMismatch] : "<missing>")}");
                Console.WriteLine($"    gold: {(firstMismatch < gold.Length ? gold[firstMismatch] : "<missing>")}");
            }
        }
    }

    private static double[,] LoadCsvDouble(string path)
    {
        var lines = File.ReadAllLines(path);
        int rows = lines.Length;
        int cols = lines[0].Split(',').Length;
        var data = new double[rows, cols];
        for (int i = 0; i < rows; i++)
        {
            var parts = lines[i].Split(',');
            for (int j = 0; j < cols; j++)
                data[i, j] = double.Parse(parts[j], CultureInfo.InvariantCulture);
        }
        return data;
    }

    private static float[] ReadBinAbs(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var arr = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    private static float[] Flatten(double[,] a)
    {
        int rows = a.GetLength(0), cols = a.GetLength(1);
        var flat = new float[rows * cols];
        int idx = 0;
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
                flat[idx++] = (float)a[i, j];
        return flat;
    }

    /// <summary>Reports max/mean absolute diff and max/mean relative diff (relative
    /// error computed against max(|gold|, 1e-9) to avoid div-by-zero near silent
    /// bins). Pass gate: mean relative error &lt; 1e-3 ("typical" case per Stage D
    /// spec) AND max absolute error &lt; 1e-2 -- a few outlier bins (DC/Nyquist)
    /// are tolerated since we gate on MEAN relative error, not max.</summary>
    private static void ReportWorldCodecDiff(string label, float[] a, float[] gold)
    {
        if (a.Length != gold.Length)
            throw new InvalidOperationException($"length mismatch: {a.Length} vs {gold.Length}");
        int n = a.Length;
        double maxAbs = 0, sumAbs = 0, sumAbsSq = 0, maxRel = 0, sumRel = 0;
        for (int i = 0; i < n; i++)
        {
            double diff = Math.Abs((double)a[i] - gold[i]);
            sumAbs += diff;
            sumAbsSq += diff * diff;
            if (diff > maxAbs) maxAbs = diff;
            double rel = diff / Math.Max(Math.Abs((double)gold[i]), 1e-9);
            sumRel += rel;
            if (rel > maxRel) maxRel = rel;
        }
        double meanAbs = sumAbs / n;
        double stdAbs = Math.Sqrt(Math.Max(0, sumAbsSq / n - meanAbs * meanAbs));
        double meanRel = sumRel / n;
        bool ok = meanRel < 1e-3 && maxAbs < 1e-2;
        s_allOk &= ok;
        Console.WriteLine($"  [worldcodec {label}] n={n} maxAbs={maxAbs:E3} meanAbs={meanAbs:E3} stdAbs={stdAbs:E3} " +
                          $"maxRel={maxRel:E3} meanRel={meanRel:E3} {(ok ? "OK" : "NG!")}");
    }

    private static void RunFftSelfTest()
    {
        var rng = new Random(42);
        int n = 16;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++) { re[i] = rng.NextDouble() * 2 - 1; im[i] = rng.NextDouble() * 2 - 1; }
        var reRef = (double[])re.Clone();
        var imRef = (double[])im.Clone();

        // direct O(n^2) unnormalized inverse DFT sum: out[k] = sum_j in[j]*exp(+i 2pi j k /n)
        var outRe = new double[n];
        var outIm = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int j = 0; j < n; j++)
            {
                double ang = 2 * Math.PI * j * k / n;
                double c = Math.Cos(ang), s = Math.Sin(ang);
                sr += reRef[j] * c - imRef[j] * s;
                si += reRef[j] * s + imRef[j] * c;
            }
            outRe[k] = sr; outIm[k] = si;
        }

        WorldCodec.DebugIfft(re, im);
        double maxDiff = 0;
        for (int i = 0; i < n; i++)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(re[i] - outRe[i]));
            maxDiff = Math.Max(maxDiff, Math.Abs(im[i] - outIm[i]));
        }
        Console.WriteLine($"radix2 vs direct O(n^2) unnormalized IDFT: maxDiff={maxDiff:E3}");

        // known-signal test: IFFT of [N,0,...,0] (real) should give constant N in every bin
        int n2 = 8;
        var re2 = new double[n2]; var im2 = new double[n2];
        re2[0] = n2;
        WorldCodec.DebugIfft(re2, im2);
        Console.WriteLine("IFFT([N,0,...,0]) = [" + string.Join(",", re2.Select(v => v.ToString("F3"))) + "] (expect all " + n2 + ")");
    }

    private static void RunMdn(JsonElement root, string name)
    {
        var section = root.GetProperty(name);
        int inDim = section.GetProperty("in_dim").GetInt32();
        using var model = new MdnModel(Path.Combine(OnnxDir, name + ".onnx"));

        foreach (var c in section.GetProperty("cases").EnumerateArray())
        {
            int t = c.GetProperty("T").GetInt32();
            var x = ReadBin(c.GetProperty("x").GetString()!);
            var muGold = ReadBin(c.GetProperty("mu").GetString()!);
            var sigmaGold = ReadBin(c.GetProperty("sigma").GetString()!);

            var (mu, sigma) = model.Run(x, t, inDim);
            Report($"{name} mu", t, MaxAbsDiff(mu, muGold), 1e-4);
            Report($"{name} sigma", t, MaxAbsDiff(sigma, sigmaGold), 1e-4);
        }
    }

    private static void RunScalers(JsonElement root)
    {
        foreach (var e in root.GetProperty("scalers").EnumerateArray())
        {
            string name = e.GetProperty("name").GetString()!;
            string kind = e.GetProperty("kind").GetString()!;
            int rows = e.GetProperty("rows").GetInt32();
            var raw = ReadBin(e.TryGetProperty("raw", out var rawEl) ? rawEl.GetString()! : e.GetProperty("normed").GetString()!);
            var expected = ReadBin(e.GetProperty("expected").GetString()!);

            float[] actual;
            if (kind == "minmax")
            {
                var min = ReadBin(e.GetProperty("min").GetString()!);
                var scale = ReadBin(e.GetProperty("scale").GetString()!);
                var scaler = new MinMaxScaler(min.Select(x => (double)x).ToArray(), scale.Select(x => (double)x).ToArray());
                actual = scaler.Transform(raw, rows);
            }
            else
            {
                var mean = ReadBin(e.GetProperty("mean").GetString()!);
                var scale = ReadBin(e.GetProperty("scale").GetString()!);
                var scaler = new StandardScaler(mean.Select(x => (double)x).ToArray(), scale.Select(x => (double)x).ToArray());
                actual = scaler.InverseTransform(raw, rows);
            }
            Report($"scaler {name} ({kind})", rows, MaxAbsDiff(actual, expected), 1e-3);
        }
    }

    private static void RunTimingLabels()
    {
        var manJson = File.ReadAllText(Path.Combine(TestDataDir, "timing_manifest.json"));
        using var man = JsonDocument.Parse(manJson);
        var root = man.RootElement;
        long frameShift = root.GetProperty("hts_frame_shift").GetInt64();
        var arEl = root.GetProperty("allowed_range");
        var arrEl = root.GetProperty("allowed_range_rest");
        double[] allowedRange = { arEl[0].GetDouble(), arEl[1].GetDouble() };
        double[] allowedRangeRest = { arrEl[0].GetDouble(), arrEl[1].GetDouble() };

        var qs = QuestionSet.Load(Path.Combine(TestDataDir, "linguistic_qst.hed"));
        var pitchIndices = qs.GetPitchIndices();

        var timelagInScaler = MinMaxScaler.FromNpy(
            Path.Combine(ModelDir, "in_timelag_scaler_min.npy"), Path.Combine(ModelDir, "in_timelag_scaler_scale.npy"));
        var timelagOutScaler = StandardScaler.FromNpy(
            Path.Combine(ModelDir, "out_timelag_scaler_mean.npy"), Path.Combine(ModelDir, "out_timelag_scaler_scale.npy"));
        var durationInScaler = MinMaxScaler.FromNpy(
            Path.Combine(ModelDir, "in_duration_scaler_min.npy"), Path.Combine(ModelDir, "in_duration_scaler_scale.npy"));
        var durationOutScaler = StandardScaler.FromNpy(
            Path.Combine(ModelDir, "out_duration_scaler_mean.npy"), Path.Combine(ModelDir, "out_duration_scaler_scale.npy"));
        var durationOutVar = Npy.Load(Path.Combine(ModelDir, "out_duration_scaler_var.npy")).Data;

        using var timelagModel = new MdnModel(Path.Combine(OnnxDir, "timelag.onnx"));
        using var durationModel = new MdnModel(Path.Combine(OnnxDir, "duration.onnx"));

        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            string tag = c.GetProperty("tag").GetString()!;
            int nNotes = c.GetProperty("num_notes").GetInt32();
            int nPhones = c.GetProperty("num_phones").GetInt32();

            var scoreLabels = HtsLabelFile.Load(Path.Combine(TestDataDir, c.GetProperty("score_lab").GetString()!));
            scoreLabels.FrameShift = frameShift;
            scoreLabels.Round();
            var noteLabels = scoreLabels.Select(scoreLabels.GetNoteIndices());

            var goldLag = ReadBin(c.GetProperty("timelag_lag").GetString()!).Select(x => (double)x).ToArray();
            var goldDurMu = ReadBin(c.GetProperty("duration_mu").GetString()!).Select(x => (double)x).ToArray();
            var goldDurSigmaSq = ReadBin(c.GetProperty("duration_sigma_sq").GetString()!).Select(x => (double)x).ToArray();
            var goldStart = ReadBinInt64(c.GetProperty("timing_start").GetString()!);
            var goldEnd = ReadBinInt64(c.GetProperty("timing_end").GetString()!);

            // --- Path A: isolation -- feed gold mu/sigma/lag directly into PostprocessDuration ---
            var timingA = TimingLabels.PostprocessDuration(scoreLabels, goldDurMu, goldDurSigmaSq, goldLag);
            CompareTimingExact($"timing isolation [{tag}]", timingA, goldStart, goldEnd);

            // --- Path B: end-to-end -- C# linguistic features + MdnModel + scalers + postprocessing ---
            var timelagFeats = LinguisticFeatures.ScoreLevel(noteLabels, qs);
            LinguisticFeatures.ApplyLogF0Conditioning(timelagFeats, pitchIndices);
            var timelagX = timelagInScaler.Transform(ToFlat(timelagFeats), nNotes);
            ClipNonPitch(timelagX, nNotes, timelagInScaler.Dim, pitchIndices, 0.0, 1.0);
            var (timelagMuRaw, timelagSigmaRaw) = timelagModel.Run(timelagX, nNotes, timelagInScaler.Dim);
            Report($"timelag raw mu [{tag}]", nNotes, MaxAbsDiff(timelagMuRaw, ReadBin(c.GetProperty("timelag_raw_mu").GetString()!)), 1e-4);
            Report($"timelag raw sigma [{tag}]", nNotes, MaxAbsDiff(timelagSigmaRaw, ReadBin(c.GetProperty("timelag_raw_sigma").GetString()!)), 1e-4);

            var timelagMuDenorm = timelagOutScaler.InverseTransform(timelagMuRaw, nNotes);
            var lagB = TimingLabels.PostprocessTimelag(timelagMuDenorm, noteLabels, allowedRange, allowedRangeRest);
            CompareExactArray($"timelag lag [{tag}]", lagB, goldLag);

            var durationFeats = LinguisticFeatures.ScoreLevel(scoreLabels, qs);
            LinguisticFeatures.ApplyLogF0Conditioning(durationFeats, pitchIndices);
            var durationX = durationInScaler.Transform(ToFlat(durationFeats), nPhones);
            ClipNonPitch(durationX, nPhones, durationInScaler.Dim, pitchIndices, 0.0, 1.0);
            var (durMuRaw, durSigmaRaw) = durationModel.Run(durationX, nPhones, durationInScaler.Dim);
            Report($"duration raw mu [{tag}]", nPhones, MaxAbsDiff(durMuRaw, ReadBin(c.GetProperty("duration_raw_mu").GetString()!)), 1e-4);
            Report($"duration raw sigma [{tag}]", nPhones, MaxAbsDiff(durSigmaRaw, ReadBin(c.GetProperty("duration_raw_sigma").GetString()!)), 1e-4);

            var durMuDenorm = durationOutScaler.InverseTransform(durMuRaw, nPhones).Select(x => (double)x).ToArray();
            var durSigmaSqDenorm = new double[nPhones];
            for (int i = 0; i < nPhones; i++)
                durSigmaSqDenorm[i] = Math.Max((double)durSigmaRaw[i] * durSigmaRaw[i] * durationOutVar[0], 1e-14);

            var timingB = TimingLabels.PostprocessDuration(scoreLabels, durMuDenorm, durSigmaSqDenorm, lagB);
            CompareTimingExact($"timing end-to-end [{tag}]", timingB, goldStart, goldEnd);

            Console.WriteLine($"    (case {tag}: notes={nNotes} phones={nPhones})");
        }
    }

    private static void CompareExactArray(string label, double[] actual, double[] gold)
    {
        bool lenOk = actual.Length == gold.Length;
        int mismatches = 0;
        if (lenOk)
        {
            for (int i = 0; i < gold.Length; i++)
                if (actual[i] != gold[i]) mismatches++;
        }
        bool ok = lenOk && mismatches == 0;
        s_allOk &= ok;
        Console.WriteLine($"  [{label}] N={gold.Length} mismatched={(lenOk ? mismatches.ToString() : "len mismatch")} {(ok ? "OK" : "NG!")}");
    }

    private static void CompareTimingExact(string label, HtsLabelFile timing, long[] goldStart, long[] goldEnd)
    {
        bool lenOk = timing.Count == goldStart.Length && timing.Count == goldEnd.Length;
        int mismatches = 0;
        if (lenOk)
        {
            for (int i = 0; i < timing.Count; i++)
                if (timing.StartTimes[i] != goldStart[i] || timing.EndTimes[i] != goldEnd[i]) mismatches++;
        }
        bool ok = lenOk && mismatches == 0;
        s_allOk &= ok;
        Console.WriteLine($"  [{label}] count={timing.Count}/{goldStart.Length} mismatched_rows={(lenOk ? mismatches.ToString() : "len mismatch")} {(ok ? "OK" : "NG!")}");
    }

    private static float[][] SplitSteps(float[] flat, int kStep)
    {
        int chunk = flat.Length / kStep;
        var steps = new float[kStep][];
        for (int i = 0; i < kStep; i++)
        {
            steps[i] = new float[chunk];
            Array.Copy(flat, i * chunk, steps[i], 0, chunk);
        }
        return steps;
    }

    private static void RunAcoustic(JsonElement root)
    {
        var section = root.GetProperty("acoustic");
        using var model = new AcousticModel(OnnxDir, Path.Combine(OnnxDir, "acoustic_diffusion_params.json"));

        int mgcKStep = section.GetProperty("mgc_k_step").GetInt32();
        int bapKStep = section.GetProperty("bap_k_step").GetInt32();

        foreach (var c in section.GetProperty("cases").EnumerateArray())
        {
            int t = c.GetProperty("T").GetInt32();
            var x = ReadBin(c.GetProperty("x").GetString()!);
            var lf0Gold = ReadBin(c.GetProperty("lf0_t1").GetString()!);
            var mgcGold = ReadBin(c.GetProperty("mgc_t1").GetString()!);
            var bapGold = ReadBin(c.GetProperty("bap_t1").GetString()!);
            var vuvGold = ReadBin(c.GetProperty("vuv_t1").GetString()!);
            var outGold = ReadBin(c.GetProperty("out_final").GetString()!);

            var mgcInitNoise = ReadBin(c.GetProperty("mgc_init_noise").GetString()!);
            var mgcStepNoisesFlat = ReadBin(c.GetProperty("mgc_step_noises").GetString()!);
            var bapInitNoise = ReadBin(c.GetProperty("bap_init_noise").GetString()!);
            var bapStepNoisesFlat = ReadBin(c.GetProperty("bap_step_noises").GetString()!);

            var mgcSteps = SplitSteps(mgcStepNoisesFlat, mgcKStep);
            var bapSteps = SplitSteps(bapStepNoisesFlat, bapKStep);

            var sw = Stopwatch.StartNew();
            var result = model.Infer(x, t, mgcInitNoise, mgcSteps, bapInitNoise, bapSteps);
            sw.Stop();

            Report($"acoustic lf0_t1", t, MaxAbsDiff(result.Lf0T1, lf0Gold), 1e-4);
            Report($"acoustic mgc_t1", t, MaxAbsDiff(result.MgcT1, mgcGold), 1e-3);
            Report($"acoustic bap_t1", t, MaxAbsDiff(result.BapT1, bapGold), 1e-3);
            Report($"acoustic vuv_t1", t, MaxAbsDiff(result.VuvT1, vuvGold), 1e-4);
            Report($"acoustic out_final", t, MaxAbsDiff(result.OutFinal, outGold), 1e-3);
            Console.WriteLine($"    (T={t}, T1={result.T1}) elapsed={sw.ElapsedMilliseconds} ms");
        }
    }
}
