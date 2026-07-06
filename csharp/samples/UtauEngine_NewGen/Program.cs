using System;
using System.Text;
using UtauEngineNg.Cli;
using UtauEngineNg.Core;
using UtauEngineNg.Diagnostics;

namespace UtauEngineNg
{
    /// <summary>
    /// L2R — libllsm ベースの UTAU リサンプラー（新世代エンジン）。
    /// エントリポイント。引数を解析し <see cref="EnginePipeline"/> を 1 回実行する。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] rawArgs)
        {
            // UTAU は Shift-JIS でパスを渡すため、コンソール/引数エンコーディングを合わせる
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                Console.OutputEncoding = Encoding.GetEncoding("shift_jis");
            }
            catch { /* 端末が未対応でも処理は継続 */ }

            var diag = DiagnosticsContext.FromEnvironment();
            var log = diag.Log;

            if (rawArgs.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            try
            {
                var args = ResamplerArgs.Parse(rawArgs);
                if (!args.IsValid)
                {
                    log.Error("Main", "Invalid arguments: input/output path missing");
                    return 1;
                }

                new EnginePipeline(diag).Run(args);
                foreach (var t in diag.Profiler.Report())
                    log.Debug("Profiler", $"{t.Stage}: {t.DurationMs:F1}ms");
                diag.Trace.Flush();
                return 0;
            }
            catch (Exception ex)
            {
                log.Error("Main", $"{ex.GetType().Name}: {ex.Message}");
                log.Debug("Main", ex.StackTrace ?? "");
                return 2;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("L2R — libllsm UTAU resampler");
            Console.WriteLine("Usage: L2R <input.wav> <output.wav> <pitch> <velocity> <flags> <offset> <length> <consonant> <cutoff> <volume> <modulation> [tempo!pitchbend]");
            Console.WriteLine();
            Console.WriteLine("Environment: L2R_LOG=Trace|Debug|Info|Warn|Error|Silent  L2R_TRACE=<path>  L2R_DUMP=<dir>");
        }
    }
}
