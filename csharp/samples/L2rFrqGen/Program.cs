using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using L2rFrqGen.Audio;
using L2rFrqGen.Rmvpe;
using UtauEngineNg.Audio;

namespace L2rFrqGen
{
    /// <summary>
    /// UTAU 音源に対してニューラル F0 の周波数表 "*.frq.l2r" を事前生成するスタンドアロンツール。
    /// エンジン (L2R.exe) は表があれば自動的に利用し、なければ従来通り .frq / PYIN へ落ちる。
    /// </summary>
    public static class Program
    {
        private sealed class Options
        {
            public List<string> Inputs { get; } = new();
            public string ModelPath = Path.Combine(AppContext.BaseDirectory, "models", "rmvpe.onnx");
            public float Threshold = 0.03f;
            /// <summary>ピークからこの dB 以下のフレームは無声扱い。</summary>
            public float SilenceDb = -40f;
            public int Jobs = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
            public bool Force;
            public bool UseGpu;
            public bool Normalize = true;
            public bool DryRun;
            public bool Verbose;
        }

        public static int Main(string[] args)
        {
            Options opt;
            try { opt = Parse(args); }
            catch (Exception ex) { Console.Error.WriteLine("error: " + ex.Message); return 2; }

            if (opt.Inputs.Count == 0) { PrintUsage(); return 1; }

            if (!File.Exists(opt.ModelPath))
            {
                Console.Error.WriteLine($"error: model not found: {opt.ModelPath}");
                Console.Error.WriteLine("       Export it with tools/export_rmvpe_onnx.py, or pass --model <path>.");
                return 2;
            }

            var files = CollectWavFiles(opt.Inputs);
            if (files.Count == 0) { Console.Error.WriteLine("error: no .wav found"); return 1; }

            Console.WriteLine($"model   : {opt.ModelPath}");
            Console.WriteLine($"files   : {files.Count}");
            Console.WriteLine($"jobs    : {opt.Jobs}  ep: {(opt.UseGpu ? "DirectML" : "CPU")}  threshold: {opt.Threshold:F3}");

            // ORT 内部スレッドとファイル並列が二重に走ると取り合いになるので intra-op を割り振る
            int intraOp = Math.Max(1, Environment.ProcessorCount / opt.Jobs);

            using var estimator = new RmvpeF0Estimator(opt.ModelPath, opt.UseGpu, intraOp);

            int done = 0, skipped = 0, failed = 0, planned = 0;
            var sw = Stopwatch.StartNew();

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = opt.Jobs }, path =>
            {
                try
                {
                    var status = ProcessOne(path, estimator, opt);
                    if (status == Status.Skipped) Interlocked.Increment(ref skipped);
                    else if (status == Status.Planned) Interlocked.Increment(ref planned);
                    else Interlocked.Increment(ref done);

                    if (opt.Verbose || status != Status.Skipped)
                        Console.WriteLine($"[{status}] {Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Console.Error.WriteLine($"[FAIL] {Path.GetFileName(path)}: {ex.Message}");
                }
            });

            sw.Stop();
            string what = opt.DryRun
                ? $"{planned} to generate"
                : $"{done} written";
            Console.WriteLine($"done: {what}, {skipped} up-to-date, {failed} failed in {sw.Elapsed.TotalSeconds:F1}s");
            return failed > 0 ? 1 : 0;
        }

        private enum Status { Written, Skipped, Planned }

        private static Status ProcessOne(string wavPath, RmvpeF0Estimator estimator, Options opt)
        {
            var (samples, fs) = WavIo.ReadMono(wavPath);
            if (samples.Length < RmvpeF0Estimator.HopSamples)
                throw new InvalidDataException("too short");

            ulong hash = L2rF0File.ComputeHash(samples);
            string outPath = L2rF0File.PathFor(wavPath);

            if (!opt.Force && File.Exists(outPath))
            {
                var existing = L2rF0File.TryRead(outPath);
                if (existing != null && existing.SourceSamples == samples.Length && existing.SourceHash == hash)
                    return Status.Skipped;
            }
            if (opt.DryRun) return Status.Planned;

            var work = (float[])samples.Clone();
            AudioPrep.RemoveDc(work);
            if (opt.Normalize)
            {
                // 録音レベルは音源ごとに数十 dB 単位で違う。log-mel は絶対レベルに
                // 依存するので、揃えておかないと音源差がそのまま推定精度の差になる。
                float peak = 0f;
                foreach (var v in work) { float a = MathF.Abs(v); if (a > peak) peak = a; }
                if (peak > 1e-6f)
                {
                    float g = 0.95f / peak;
                    for (int i = 0; i < work.Length; i++) work[i] *= g;
                }
            }

            var audio16k = AudioPrep.Resample(work, fs, RmvpeF0Estimator.AnalysisRate);
            var (f0, conf) = estimator.Estimate(audio16k, opt.Threshold);
            var rms = AudioPrep.FrameRms(audio16k, RmvpeF0Estimator.HopSamples,
                                         RmvpeF0Estimator.WindowSamples, f0.Length);
            int gated = GateByLevel(f0, rms, opt.SilenceDb);
            if (opt.Verbose && gated > 0)
                Console.WriteLine($"  gated {gated} low-level frames ({opt.SilenceDb:F0}dB below peak)");

            L2rF0File.Write(outPath, new L2rF0Data
            {
                AnalysisRate = RmvpeF0Estimator.AnalysisRate,
                HopSamples = RmvpeF0Estimator.HopSamples,
                SourceRate = fs,
                SourceSamples = samples.Length,
                SourceHash = hash,
                Model = "rmvpe-v1",
                F0 = f0,
                Confidence = conf,
                Rms = rms,
            });
            return Status.Written;
        }

        /// <summary>
        /// 暗騒音レベルのフレームを無声化する。RMVPE は -50dBFS 程度のハムや息にも
        /// F0 を返してしまい（実測: conf 0.6 で 190Hz を検出）、これが下流の
        /// 中央値ベースの srcF0 推定や V/UV 判定を汚す。閾値はファイル内のピーク
        /// 基準の相対値にして、音源ごとの録音レベル差に依存しないようにする。
        /// </summary>
        private static int GateByLevel(float[] f0, float[] rms, float silenceDb)
        {
            float peak = 0f;
            foreach (var v in rms) if (v > peak) peak = v;
            if (peak <= 0f) return 0;

            float floor = peak * MathF.Pow(10f, silenceDb / 20f);
            int gated = 0;
            for (int i = 0; i < f0.Length; i++)
            {
                if (f0[i] > 0f && rms[i] < floor) { f0[i] = 0f; gated++; }
            }
            return gated;
        }

        private static List<string> CollectWavFiles(IEnumerable<string> inputs)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in inputs)
            {
                if (Directory.Exists(input))
                {
                    foreach (var f in Directory.EnumerateFiles(input, "*.wav", SearchOption.AllDirectories))
                        set.Add(Path.GetFullPath(f));
                }
                else if (File.Exists(input))
                {
                    set.Add(Path.GetFullPath(input));
                }
                else
                {
                    Console.Error.WriteLine($"warn: not found, skipping: {input}");
                }
            }
            return set.ToList();
        }

        private static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-m":
                    case "--model": o.ModelPath = Next(args, ref i, a); break;
                    case "-t":
                    case "--threshold": o.Threshold = float.Parse(Next(args, ref i, a), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--silence-db": o.SilenceDb = -MathF.Abs(float.Parse(Next(args, ref i, a), System.Globalization.CultureInfo.InvariantCulture)); break;
                    case "-j":
                    case "--jobs": o.Jobs = Math.Max(1, int.Parse(Next(args, ref i, a), System.Globalization.CultureInfo.InvariantCulture)); break;
                    case "-f":
                    case "--force": o.Force = true; break;
                    case "--gpu": o.UseGpu = true; break;
                    case "--no-normalize": o.Normalize = false; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "-v":
                    case "--verbose": o.Verbose = true; break;
                    case "-h":
                    case "--help": o.Inputs.Clear(); return o;
                    default:
                        if (a.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"unknown option: {a}");
                        o.Inputs.Add(a);
                        break;
                }
            }
            return o;
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
            return args[++i];
        }

        private static void PrintUsage()
        {
            Console.WriteLine("""
l2rfrq - neural F0 table generator for the L2R UTAU engine

  usage: l2rfrq [options] <wav-or-directory> [...]

  Generates "<name>_wav.frq.l2r" next to each .wav. The engine picks it up
  automatically (priority: .frq.l2r > .frq > pYIN) and skips its runtime
  pYIN call entirely, so rendering gets both more accurate and faster.

  options:
    -m, --model <onnx>    RMVPE ONNX model (default: models/rmvpe.onnx)
    -t, --threshold <v>   voicing salience threshold (default: 0.03)
        --silence-db <v>  gate frames this many dB below the file peak (default: 40)
    -j, --jobs <n>        files processed in parallel
    -f, --force           regenerate even if the table is up to date
        --gpu             use DirectML execution provider
        --no-normalize    do not peak-normalize before analysis
        --dry-run         list what would be generated
    -v, --verbose         also report skipped files
""");
        }
    }
}
