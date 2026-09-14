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

            CaptureCall(rawArgs);

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

        /// <summary>
        /// 再現用の呼び出し記録。exe 本体（Environment.ProcessPath）と同じフォルダに
        /// L2R_capture.on（空ファイル）があるとき、全引数を L2R_calls.log へ 1 行ずつ追記する
        /// （UTAU からの呼び出しをそのまま再実行できる）。exe のフォルダに書けない場合は
        /// %TEMP%\L2R_calls.log へ書く。環境変数 L2R_CAPTURE=&lt;ログのパス&gt; でも有効化できる。
        /// </summary>
        private static void CaptureCall(string[] rawArgs)
        {
            try
            {
                string? exeDir = null;
                try { var pp = Environment.ProcessPath; if (!string.IsNullOrEmpty(pp)) exeDir = System.IO.Path.GetDirectoryName(pp); } catch { }
                string baseDir = AppContext.BaseDirectory;
                string? logPath = Environment.GetEnvironmentVariable("L2R_CAPTURE");
                if (string.IsNullOrEmpty(logPath))
                {
                    foreach (var d in new[] { exeDir, baseDir })
                    {
                        if (d != null && System.IO.File.Exists(System.IO.Path.Combine(d, "L2R_capture.on")))
                        { logPath = System.IO.Path.Combine(d, "L2R_calls.log"); break; }
                    }
                }
                if (string.IsNullOrEmpty(logPath)) return;

                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('	');
                foreach (var a in rawArgs)
                {
                    bool quote = a.Length == 0 || a.IndexOf(' ') >= 0;
                    if (quote) sb.Append('"').Append(a).Append('"'); else sb.Append(a);
                    sb.Append(' ');
                }
                string line = sb.ToString().TrimEnd() + Environment.NewLine;
                try { System.IO.File.AppendAllText(logPath, line, Encoding.UTF8); }
                catch
                {
                    // 書き込み不可（Program Files 等）: TEMP へ退避
                    System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "L2R_calls.log"), line, Encoding.UTF8);
                }
            }
            catch { /* 記録失敗は無視 */ }
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
