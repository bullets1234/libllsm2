using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NnsvsOnnx;

/// <summary>
/// Stage C-5: end-to-end smoke test wiring HTS score label -> predicted timing
/// -> linguistic features -> ONNX acoustic cascade -> denormalized + GV-postfiltered
/// + trajectory-smoothed mgc/f0/vuv/bap CSVs (for external WORLD decode + LLSM
/// synthesis). Reuses the exact scaler/model/postprocessing sequence already
/// verified in Program.cs's RunTimingLabels()/RunAcoustic(), plus the GV
/// post-filter and Butterworth trajectory smoothing from
/// nnsvs.gen.postprocess_acoustic (see <see cref="AcousticPostfilter"/>).
/// </summary>
public static class FullPipeline
{
    private const string TestDataDir = @"g:\libllsm2\csharp\samples\NnsvsOnnx\testdata";
    private const string OnnxDir = @"g:\libllsm2\csharp\samples\WorldToLlsm\onnx";
    private const string ModelDir = @"G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN";
    private const string OutDir = @"g:\libllsm2\csharp\samples\NnsvsOnnx\smoke_out";
    private const long FrameShift = 50000;
    private const double VuvThreshold = 0.5;

    private static readonly double[] AllowedRange = { -20, 20 };
    private static readonly double[] AllowedRangeRest = { -40, 40 };
    private static int DumpSegCounter;

    private static readonly Lazy<QuestionSet> LazyQs = new(() =>
        QuestionSet.Load(Path.Combine(TestDataDir, "linguistic_qst.hed")));

    public static void RunCase(string tag, bool dumpDebug = false, bool usePlms = true, string? inputTag = null, string? noiseSeedTag = null)
    {
        var scoreLabels = HtsLabelFile.Load(Path.Combine(TestDataDir, $"linguistic_{inputTag ?? tag}_score_lab.lab"));
        RunCore(tag, scoreLabels, dumpDebug, usePlms: usePlms, noiseSeedTag: noiseSeedTag ?? tag);
    }

    /// <summary>Real end-to-end entry point: converts a .ust file to HTS full-context
    /// score labels via <see cref="UstToHtsConverter"/> (Stage E, no Python/testdata
    /// files involved), then runs the exact same timing/acoustic/postfilter/WORLD-decode
    /// pipeline as <see cref="RunCase"/>. Output is written under smoke_out/case{outTag}/.</summary>
    public static void RunFromUst(string ustPath, string tablePath, string outTag, bool dumpDebug = false, bool usePlms = true, bool useUstPitch = true, int plmsSpeedup = 10, bool useStrided = false, bool useInt8 = false, int segParallel = 1, bool useGpu = false, bool noSegment = false)
    {
        var lines = UstToHtsConverter.Convert(ustPath, tablePath);
        var scoreLabels = HtsLabelFile.LoadFromLines(lines);
        var ust = UstFile.Load(ustPath);
        RunCore(outTag, scoreLabels, dumpDebug, ust, usePlms: usePlms, noiseSeedTag: outTag, useUstPitch: useUstPitch, plmsSpeedup: plmsSpeedup, useStrided: useStrided, useInt8: useInt8, segParallel: segParallel, useGpu: useGpu, noSegment: noSegment);
    }

    /// <summary>
    /// ustForPitch: when provided (real .ust entry point only), the acoustic
    /// model's own F0 prediction is discarded and replaced wholesale by a
    /// UST-derived portamento pitch curve (see <see cref="UstPitchCurve"/>),
    /// gated by the model's own V/UV decision -- replicating SimpleEnunu's
    /// read_lf0 substitution (simple_enunu.py) exactly.
    /// </summary>
    private static void RunCore(string tag, HtsLabelFile scoreLabels, bool dumpDebug, UstFile? ustForPitch = null, bool usePlms = true, string? noiseSeedTag = null, bool useUstPitch = true, int plmsSpeedup = 10, bool useStrided = false, bool useInt8 = false, int segParallel = 1, bool useGpu = false, bool noSegment = false)
    {
        var qs = LazyQs.Value;
        var pitchIndices = qs.GetPitchIndices();
        var pitchIndex = qs.GetPitchIndex();

        // a. score labels (already loaded by the caller, from testdata or from a real .ust)
        scoreLabels.FrameShift = FrameShift;
        scoreLabels.Round();
        var noteLabels = scoreLabels.Select(scoreLabels.GetNoteIndices());
        int nNotes = noteLabels.Count;
        int nPhones = scoreLabels.Count;

        // b. timelag + duration -> timing labels (exact pattern from Program.RunTimingLabels)
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

        var timelagFeats = LinguisticFeatures.ScoreLevel(noteLabels, qs);
        LinguisticFeatures.ApplyLogF0Conditioning(timelagFeats, pitchIndices);
        var timelagX = timelagInScaler.Transform(ToFlat(timelagFeats), nNotes);
        ClipNonPitch(timelagX, nNotes, timelagInScaler.Dim, pitchIndices, 0.0, 1.0);
        var (timelagMuRaw, _) = timelagModel.Run(timelagX, nNotes, timelagInScaler.Dim);
        var timelagMuDenorm = timelagOutScaler.InverseTransform(timelagMuRaw, nNotes);
        var lag = TimingLabels.PostprocessTimelag(timelagMuDenorm, noteLabels, AllowedRange, AllowedRangeRest);

        var durationFeats = LinguisticFeatures.ScoreLevel(scoreLabels, qs);
        LinguisticFeatures.ApplyLogF0Conditioning(durationFeats, pitchIndices);
        var durationX = durationInScaler.Transform(ToFlat(durationFeats), nPhones);
        ClipNonPitch(durationX, nPhones, durationInScaler.Dim, pitchIndices, 0.0, 1.0);
        var (durMuRaw, durSigmaRaw) = durationModel.Run(durationX, nPhones, durationInScaler.Dim);
        var durMuDenorm = durationOutScaler.InverseTransform(durMuRaw, nPhones).Select(x => (double)x).ToArray();
        var durSigmaSqDenorm = new double[nPhones];
        for (int i = 0; i < nPhones; i++)
            durSigmaSqDenorm[i] = Math.Max((double)durSigmaRaw[i] * durSigmaRaw[i] * durationOutVar[0], 1e-14);

        var timingLabels = TimingLabels.PostprocessDuration(scoreLabels, durMuDenorm, durSigmaSqDenorm, lag);

        // b''-0. Model extension "timing_auto_correct" (mono-label stage):
        // re-anchors predicted phoneme boundaries against the score (consonants
        // end at note-on with their predicted length, vowels after rests start
        // 5 ms early, etc.). See TimingAutoCorrect for the literal port.
        TimingAutoCorrect.Apply(scoreLabels, timingLabels, Path.Combine(ModelDir, "timing_auto_correct"));

        // b''. Model extension "flag_separator" + "timing_auto_correct"
        // (timing_editor ustparam stage): phonemes of flagged (non-rest) notes
        // receive the singing-style context p16=s3s0r0b0 in the TIMING labels
        // only (score labels keep p16=xx). This matches the reference-generated
        // testdata timing labels and feeds the hed's standard/sweet/rock/breathy
        // style questions of the acoustic model.
        for (int i = 0; i < timingLabels.Count; i++)
        {
            var ctx = timingLabels.Contexts[i];
            int aIdx = ctx.IndexOf("/A:", StringComparison.Ordinal);
            if (aIdx < 0) continue;
            var pPart = ctx[..aIdx];
            var m = System.Text.RegularExpressions.Regex.Match(pPart, "%[^^]*\\^([^_]*)_");
            if (!m.Success || m.Groups[1].Value == "xx") continue; // p9 flag unset (rest note)
            if (pPart.EndsWith("]xx", StringComparison.Ordinal))
                timingLabels.Contexts[i] = pPart[..^2] + "s3s0r0b0" + ctx[aIdx..];
        }

        // b'. phrase segmentation (real UST path only). The reference renderer runs
        // segmented_synthesis=True: simple_enunu.py splits the duration-modified
        // labels at sil/pau boundaries via nnsvs.io.hts.segment_labels
        // (silence_threshold=0.1, min_duration=1, force_split_threshold=1) and runs
        // the acoustic model + postfilter PER PHRASE. The model (BiLSTM encoders,
        // per-utterance GV statistics) was trained on such segments; feeding an
        // entire multi-minute song in one pass produces frame-level energy
        // instability (audible tremolo) and skewed GV scaling. The testdata
        // cases (case0/1, ~2 s, no UST) keep the verified single-segment path.
        List<(HtsLabelFile Seg, long GlobalStartTime)> segments = (ustForPitch != null && !noSegment)
            ? timingLabels.SegmentLabels(silenceThreshold: 0.1, minDuration: 1.0, forceSplitThreshold: 1.0)
            : new List<(HtsLabelFile, long)> { (timingLabels, 0L) };

        // Shared resources across segments.
        var inAcousticScaler = MinMaxScaler.FromNpy(
            Path.Combine(ModelDir, "in_acoustic_scaler_min.npy"), Path.Combine(ModelDir, "in_acoustic_scaler_scale.npy"));
        var outAcousticScaler = StandardScaler.FromNpy(
            Path.Combine(ModelDir, "out_acoustic_scaler_mean.npy"), Path.Combine(ModelDir, "out_acoustic_scaler_scale.npy"));
        // Segment-level parallelism (reference: joblib n_jobs=6 in simple_enunu.py).
        // ONNX Runtime sessions are thread-safe for concurrent Run(); when several
        // segments run Infer() at once, shrink each diffusion session's intra-op
        // pool so the total thread count stays ~ProcessorCount (each Infer runs
        // mgc+bap as 2 concurrent tasks).
        int par = Math.Clamp(segParallel, 1, Math.Max(1, segments.Count));
        int? diffThreads = par > 1 ? Math.Max(1, Environment.ProcessorCount / (2 * par)) : null;
        using var acousticModel = new AcousticModel(OnnxDir, Path.Combine(OnnxDir, "acoustic_diffusion_params.json"), useInt8: useInt8, diffusionIntraOpThreads: diffThreads, useGpu: useGpu);
        const int mgcDim = 60, bapDim = 5;
        var gvFull = Npy.Load(Path.Combine(ModelDir, "out_acoustic_scaler_var.npy")).Data;
        var gv = gvFull[..mgcDim];
        int baseSeed = unchecked((int)(0x9E3779B9 ^ StableHash(noiseSeedTag ?? tag)));
        var rng = new Random(baseSeed);

        // UST-derived pitch curve (SimpleEnunu read_lf0 equivalent), built once for
        // the whole song at 5 ms/frame in native UST note timing. Per segment it is
        // consumed by PLAIN CONSECUTIVE SLICING at the segment's absolute frame
        // offset (simple_enunu.py:474's read_lf0 slicing) -- never time-stretched.
        var ustF0 = (ustForPitch != null && useUstPitch) ? UstPitchCurve.BuildF0Hz(ustForPitch) : null;

        var segResults = new (float[,] Mgc, float[,] Bap, float[] F0, int[] Vuv)[segments.Count];

        void RunOneSegment(int segIdx, Random segRng)
        {
            var (segLabels, globalStart) = segments[segIdx];
            var (mgcSeg, bapSeg, f0Seg, vuvSeg) = ProcessSegment(
                segLabels, qs, pitchIndices, pitchIndex, inAcousticScaler, outAcousticScaler,
                acousticModel, gv, mgcDim, bapDim, segRng, usePlms,
                dumpDebug && segments.Count == 1, tag, plmsSpeedup, useStrided);

            // UST pitch substitution, gated by the model's own V/UV decision.
            if (ustF0 != null)
            {
                long segFrameOffset = globalStart / FrameShift;
                for (int i = 0; i < f0Seg.Length; i++)
                {
                    long gi = segFrameOffset + i;
                    double hz = ustF0.Length == 0 ? 0.0 : ustF0[(int)Math.Min(gi, ustF0.Length - 1)];
                    f0Seg[i] = vuvSeg[i] == 1 ? (float)hz : 0f;
                }
            }

            segResults[segIdx] = (mgcSeg, bapSeg, f0Seg, vuvSeg);
        }

        if (par > 1)
        {
            // Per-segment deterministic RNG (the shared-stream sequential draw
            // order can't be preserved under concurrency).
            Parallel.For(0, segments.Count, new ParallelOptions { MaxDegreeOfParallelism = par },
                segIdx => RunOneSegment(segIdx, new Random(unchecked(baseSeed ^ (segIdx * 0x51ED2701)))));
        }
        else
        {
            // Sequential path: identical rng stream to previous builds (reproducible).
            for (int segIdx = 0; segIdx < segments.Count; segIdx++)
                RunOneSegment(segIdx, rng);
        }

        var mgcParts = new List<float[,]>();
        var bapParts = new List<float[,]>();
        var f0Parts = new List<float[]>();
        var vuvParts = new List<int[]>();
        foreach (var r in segResults)
        {
            mgcParts.Add(r.Mgc);
            bapParts.Add(r.Bap);
            f0Parts.Add(r.F0);
            vuvParts.Add(r.Vuv);
        }

        var mgc = Concat2D(mgcParts);
        var bap = Concat2D(bapParts);
        var f0 = f0Parts.SelectMany(a => a).ToArray();
        var vuv = vuvParts.SelectMany(a => a).ToArray();
        int t = f0.Length;

        // i. sanity: no NaN/Inf anywhere (real correctness check, not soft)
        CheckFinite2D(mgc, "mgc", tag);
        CheckFinite2D(bap, "bap", tag);
        CheckFinite1D(f0, "f0", tag);


        // h. write CSVs
        string caseDir = Path.Combine(OutDir, $"case{tag}");
        Directory.CreateDirectory(caseDir);
        WriteCsv2D(Path.Combine(caseDir, $"case{tag}_mgc.csv"), mgc);
        WriteCsv2D(Path.Combine(caseDir, $"case{tag}_bap.csv"), bap);
        WriteCsv1D(Path.Combine(caseDir, $"case{tag}_f0.csv"), f0);
        WriteCsv1DInt(Path.Combine(caseDir, $"case{tag}_vuv.csv"), vuv);

        // h'. WORLD decode (mgc/bap -> sp/ap) entirely in C# (WorldCodec, Stage D) and
        // write the same "W2L1" binary format that WorldToLlsm/FeatureDump.Load reads,
        // eliminating the dump_features.py (pyworld) dependency for this step.
        WriteWorldCodecBinary(Path.Combine(caseDir, $"case{tag}_csharp.bin"), mgc, bap, f0, vuv, t);

        // i. summary
        int voiced = 0;
        double f0Min = double.PositiveInfinity, f0Max = double.NegativeInfinity, f0Sum = 0;
        for (int i = 0; i < t; i++)
        {
            if (vuv[i] != 1) continue;
            voiced++;
            if (f0[i] < f0Min) f0Min = f0[i];
            if (f0[i] > f0Max) f0Max = f0[i];
            f0Sum += f0[i];
        }
        double f0Mean = voiced > 0 ? f0Sum / voiced : 0;

        Console.WriteLine($"  [case {tag}] T={t} segments={segments.Count} voiced={voiced}/{t} " +
                          $"F0 min/max/mean = {(voiced > 0 ? f0Min : 0):F1}/{(voiced > 0 ? f0Max : 0):F1}/{f0Mean:F1} Hz " +
                          $"-> {caseDir}");
    }

    /// <summary>Runs steps c-g (frame-level linguistic features -> acoustic cascade ->
    /// out-scaler denorm -> GV postfilter + trajectory smoothing) for one phrase
    /// segment, exactly as the reference does per segment inside its
    /// segmented-synthesis loop. Returns the postfiltered mgc/bap/f0/vuv.</summary>
    private static (float[,] Mgc, float[,] Bap, float[] F0, int[] Vuv) ProcessSegment(
        HtsLabelFile timingLabels, QuestionSet qs, IReadOnlyList<int> pitchIndices, int pitchIndex,
        MinMaxScaler inAcousticScaler, StandardScaler outAcousticScaler, AcousticModel acousticModel,
        double[] gv, int mgcDim, int bapDim, Random rng, bool usePlms, bool dumpDebug, string tag, int plmsSpeedup = 10, bool useStrided = false)
    {
        // c. frame-level linguistic features (113-dim, incl. coarse_coding + log-F0 conditioning)
        var acousticFeats = LinguisticFeatures.FrameLevel(timingLabels, qs, FrameShift);
        LinguisticFeatures.ApplyLogF0Conditioning(acousticFeats, pitchIndices);
        int t = acousticFeats.GetLength(0);

        if (Environment.GetEnvironmentVariable("NNSVS_DUMP_INFEATS") == "1")
        {
            string dir = Path.Combine(OutDir, $"case{tag}");
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, $"case{tag}_infeats_seg{Interlocked.Increment(ref DumpSegCounter) - 1}.csv");
            using var sw = new StreamWriter(p);
            int dim = acousticFeats.GetLength(1);
            for (int i = 0; i < t; i++)
            {
                var sb = new System.Text.StringBuilder();
                for (int d = 0; d < dim; d++) { if (d > 0) sb.Append(','); sb.Append(acousticFeats[i, d].ToString("G9")); }
                sw.WriteLine(sb.ToString());
            }
        }

        // c'. raw (non-log-f0-conditioned) frame-level features, used only to recover
        // the score pitch column for note_frame_indices (mirrors nnsvs.gen.postprocess_acoustic's
        // fresh call to fe.linguistic_features + nnsvs.io.hts.get_note_frame_indices).
        var rawFrameFeats = LinguisticFeatures.FrameLevel(timingLabels, qs, FrameShift);
        var rawPitchColumn = new float[t];
        for (int i = 0; i < t; i++) rawPitchColumn[i] = rawFrameFeats[i, pitchIndex];

        // c''. force_fix_vuv=True masks (port of nnsvs.gen.correct_vuv_by_phone):
        // frames whose phone matches the hed C-VUV_Voiced flag are forced voiced;
        // frames matching C-VUV_Unvoiced (disabled in this hed) or C-Phone_sil/pau/br
        // are forced unvoiced. This suppresses mid-note V/UV flicker from the
        // acoustic model, exactly like the reference's engine.svs(force_fix_vuv=True).
        var forceVoiced = new bool[t];
        var forceUnvoiced = new bool[t];
        int inVoicedIdx = -1;
        for (int k = 0; k < qs.BinaryDict.Count; k++)
        {
            if (qs.BinaryDict[k].Name.Contains("C-VUV_Voiced")) { inVoicedIdx = k; break; }
        }
        if (inVoicedIdx > 0) // reference guard: index must be > 0
        {
            for (int i = 0; i < t; i++)
                if (rawFrameFeats[i, inVoicedIdx] > 0) forceVoiced[i] = true;
        }
        for (int k = 0; k < qs.BinaryDict.Count; k++)
        {
            string name = qs.BinaryDict[k].Name;
            if (!name.Contains("C-VUV_Unvoiced") &&
                !name.Contains("C-Phone_sil") && !name.Contains("C-Phone_pau") && !name.Contains("C-Phone_br"))
                continue;
            for (int i = 0; i < t; i++)
                if (rawFrameFeats[i, k] > 0) forceUnvoiced[i] = true;
        }

        // d. in-scaler transform + clip (force_clip_input_features=true, config.yaml)
        var x = inAcousticScaler.Transform(ToFlat(acousticFeats), t);
        ClipNonPitch(x, t, inAcousticScaler.Dim, pitchIndices, 0.0, 1.0);

        // e. acoustic cascade (lf0 AR decode + mgc/bap diffusion + vuv), own sampled noise.
        // Uses the PLMS fast sampler (10-step, ~9x fewer denoiser calls than full
        // 100-step DDPM) -- see AcousticModel.RunDiffusionPlms. Only the initial
        // noise is needed for PLMS (it's a deterministic ODE solver), so
        // per-step noise is not generated here.
        int padOuter = AcousticModel.PadOuter(t);
        int t1 = t + padOuter;

        var mgcInitNoise = GaussianNoise(rng, AcousticModel.MgcOutDim * t1);
        var bapInitNoise = GaussianNoise(rng, AcousticModel.BapOutDim * t1);

        AcousticCascadeResult result;
        if (usePlms)
        {
            result = acousticModel.Infer(x, t, mgcInitNoise, Array.Empty<float[]>(), bapInitNoise, Array.Empty<float[]>(),
                usePlms: true, pndmSpeedup: plmsSpeedup);
        }
        else if (useStrided)
        {
            // Strided stochastic sampler (DDIM eta=1): per-step noise like DDPM,
            // but only K_step/interval denoiser calls. Noise arrays are generated
            // for every absolute step for rng-stream stability; only the strided
            // subset is consumed.
            var mgcStepNoises = new float[acousticModel.MgcKStep][];
            for (int i = 0; i < mgcStepNoises.Length; i++)
                mgcStepNoises[i] = GaussianNoise(rng, AcousticModel.MgcOutDim * t1);
            var bapStepNoises = new float[acousticModel.BapKStep][];
            for (int i = 0; i < bapStepNoises.Length; i++)
                bapStepNoises[i] = GaussianNoise(rng, AcousticModel.BapOutDim * t1);
            result = acousticModel.Infer(x, t, mgcInitNoise, mgcStepNoises, bapInitNoise, bapStepNoises,
                usePlms: false, pndmSpeedup: plmsSpeedup, useStrided: true);
        }
        else
        {
            // A/B testing hook: full K_step DDPM (deterministic ancestral steps
            // with per-step noise), for comparing against the PLMS(10) fast
            // sampler used in production, to rule in/out sampler-induced quality
            // loss (e.g. weak/breathy timbre) as a root cause.
            var mgcStepNoises = new float[acousticModel.MgcKStep][];
            for (int i = 0; i < mgcStepNoises.Length; i++)
                mgcStepNoises[i] = GaussianNoise(rng, AcousticModel.MgcOutDim * t1);
            var bapStepNoises = new float[acousticModel.BapKStep][];
            for (int i = 0; i < bapStepNoises.Length; i++)
                bapStepNoises[i] = GaussianNoise(rng, AcousticModel.BapOutDim * t1);
            result = acousticModel.Infer(x, t, mgcInitNoise, mgcStepNoises, bapInitNoise, bapStepNoises,
                usePlms: false);
        }

        // f. out-scaler inverse transform (whole 67-dim vector, incl. vuv -- matches
        // nnsvs.gen.predict_acoustic: pred_acoustic = acoustic_out_scaler.inverse_transform(max_mu))
        var physical = outAcousticScaler.InverseTransform(result.OutFinal, t);

        if (dumpDebug)
        {
            string dbgDir = Path.Combine(TestDataDir, "postfilter_debug");
            Directory.CreateDirectory(dbgDir);
            WriteBinFloat32(Path.Combine(dbgDir, $"case{tag}_acoustic_physical.bin"), physical);
            WriteBinFloat32(Path.Combine(dbgDir, $"case{tag}_score_pitch_raw.bin"), rawPitchColumn);
            File.WriteAllText(Path.Combine(dbgDir, $"case{tag}_manifest.json"),
                $"{{\"T\": {t}, \"pitch_idx\": {pitchIndex}}}");
        }

        // g. GV post-filter + trajectory smoothing + bap clip (nnsvs.gen.postprocess_acoustic),
        // applied per segment exactly like the reference (GV statistics are per-phrase).
        var pf = AcousticPostfilter.Apply(physical, t, AcousticModel.OutDim, mgcDim, bapDim, gv, rawPitchColumn, VuvThreshold,
            forceVoiced, forceUnvoiced);
        return (pf.Mgc, pf.Bap, pf.F0Final, pf.Vuv);
    }

    /// <summary>Concatenates 2D [t, dim] blocks along the time axis.</summary>
    private static float[,] Concat2D(List<float[,]> parts)
    {
        int dim = parts[0].GetLength(1);
        int total = parts.Sum(p => p.GetLength(0));
        var result = new float[total, dim];
        int row = 0;
        foreach (var p in parts)
        {
            int n = p.GetLength(0);
            Buffer.BlockCopy(p, 0, result, row * dim * sizeof(float), n * dim * sizeof(float));
            row += n;
        }
        return result;
    }

    /// <summary>Writes a flat float32 array little-endian, for cross-checking against
    /// a Python-side gold dump (np.fromfile(path, dtype='&lt;f4')).</summary>
    private static void WriteBinFloat32(string path, float[] data)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Deterministic (non-randomized) string hash, FNV-1a 32-bit. Used for the
    /// diffusion initial-noise RNG seed instead of string.GetHashCode(), whose value is
    /// salted per-process by .NET (System.Runtime randomized string hashing), which made
    /// every run's sampled noise -- and hence output -- non-reproducible even for the
    /// same tag, and made PLMS-vs-full-DDPM A/B comparisons invalid (different noise per run).</summary>
    private static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return unchecked((int)h);
    }

    /// <summary>Stage D: decodes mgc/bap (WORLD codec) to sp/ap entirely in C# via
    /// <see cref="WorldCodec"/>, then writes the exact "W2L1" binary format that
    /// WorldToLlsm's FeatureDump.Load reads (see dump_features.py's docstring / the
    /// FeatureDump reader in WorldToLlsm's Program.cs for the byte layout):
    /// magic "W2L1", int32 fs, int32 nfrm, int32 nbin, float32 periodMs,
    /// float32[nfrm] f0 (gated), float32[nfrm*nbin] sp row-major, float32[nfrm*nbin] ap row-major.</summary>
    private static void WriteWorldCodecBinary(string path, float[,] mgc, float[,] bap, float[] f0, int[] vuv, int t)
    {
        const int fs = 48000;
        const float periodMs = 5.0f;
        int mgcDim = mgc.GetLength(1);
        int fftSize = WorldCodec.GetFFTSizeForCheapTrick(fs);
        int nbin = fftSize / 2 + 1;

        var mgcD = new double[t, mgcDim];
        for (int i = 0; i < t; i++) for (int d = 0; d < mgcDim; d++) mgcD[i, d] = mgc[i, d];
        int bapDim = bap.GetLength(1);
        var bapD = new double[t, bapDim];
        for (int i = 0; i < t; i++) for (int d = 0; d < bapDim; d++) bapD[i, d] = bap[i, d];

        var sp = WorldCodec.DecodeSpectralEnvelope(mgcD, t, fs, fftSize, mgcDim);
        var ap = WorldCodec.DecodeAperiodicity(bapD, t, fs, fftSize);

        // f0 gating + belt-and-suspenders unvoiced ap=1 (matches dump_features.py)
        var f0Gated = new float[t];
        for (int i = 0; i < t; i++) f0Gated[i] = vuv[i] != 0 ? f0[i] : 0f;

        var spFlat = new float[t * nbin];
        var apFlat = new float[t * nbin];
        for (int i = 0; i < t; i++)
        {
            bool unvoiced = f0Gated[i] <= 0;
            for (int j = 0; j < nbin; j++)
            {
                spFlat[i * nbin + j] = (float)sp[i, j];
                double apv = unvoiced ? 1.0 : Math.Clamp(ap[i, j], 0.0, 1.0);
                apFlat[i * nbin + j] = (float)apv;
            }
        }

        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(Encoding.ASCII.GetBytes("W2L1"));
        bw.Write(fs);
        bw.Write(t);
        bw.Write(nbin);
        bw.Write(periodMs);
        foreach (var v in f0Gated) bw.Write(v);
        foreach (var v in spFlat) bw.Write(v);
        foreach (var v in apFlat) bw.Write(v);
    }

    /// <summary>Standard-normal noise via Box-Muller (smoke test: does not need to
    /// match any particular gold noise, diffusion sampling is inherently stochastic).</summary>
    private static float[] GaussianNoise(Random rng, int n)
    {
        var result = new float[n];
        int i = 0;
        while (i < n)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double r = Math.Sqrt(-2.0 * Math.Log(u1));
            double theta = 2.0 * Math.PI * u2;
            result[i++] = (float)(r * Math.Cos(theta));
            if (i < n) result[i++] = (float)(r * Math.Sin(theta));
        }
        return result;
    }

    private static float[] ToFlat(float[,] feats)
    {
        var flat = new float[feats.Length];
        Buffer.BlockCopy(feats, 0, flat, 0, flat.Length * 4);
        return flat;
    }

    /// <summary>Mirrors predict_timelag/predict_duration/predict_acoustic's
    /// force_clip_input_features: after MinMaxScaler.transform, clip all
    /// non-pitch columns to [lo, hi] (feature_range default (0,1)).</summary>
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

    private static void CheckFinite2D(float[,] data, string name, string tag)
    {
        int rows = data.GetLength(0), cols = data.GetLength(1);
        for (int i = 0; i < rows; i++)
            for (int d = 0; d < cols; d++)
                if (float.IsNaN(data[i, d]) || float.IsInfinity(data[i, d]))
                    throw new InvalidDataException($"NaN/Inf found in {name} at frame {i}, dim {d} (case {tag})");
    }

    private static void CheckFinite1D(float[] data, string name, string tag)
    {
        for (int i = 0; i < data.Length; i++)
            if (float.IsNaN(data[i]) || float.IsInfinity(data[i]))
                throw new InvalidDataException($"NaN/Inf found in {name} at frame {i} (case {tag})");
    }

    private static void WriteCsv2D(string path, float[,] data)
    {
        int rows = data.GetLength(0), cols = data.GetLength(1);
        using var sw = new StreamWriter(path);
        var sb = new StringBuilder();
        for (int i = 0; i < rows; i++)
        {
            sb.Clear();
            for (int d = 0; d < cols; d++)
            {
                if (d > 0) sb.Append(',');
                sb.Append(((double)data[i, d]).ToString("G9", CultureInfo.InvariantCulture));
            }
            sw.WriteLine(sb.ToString());
        }
    }

    private static void WriteCsv1D(string path, float[] data)
    {
        using var sw = new StreamWriter(path);
        foreach (var v in data)
            sw.WriteLine(((double)v).ToString("G9", CultureInfo.InvariantCulture));
    }

    private static void WriteCsv1DInt(string path, int[] data)
    {
        using var sw = new StreamWriter(path);
        foreach (var v in data)
            sw.WriteLine(v.ToString(CultureInfo.InvariantCulture));
    }
}
