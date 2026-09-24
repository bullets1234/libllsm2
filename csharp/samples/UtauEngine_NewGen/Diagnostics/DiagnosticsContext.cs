using System;

namespace UtauEngineNg.Diagnostics
{
    /// <summary>
    /// 診断系（ログ・プロファイラ・トレース・ダンプ）を1つにまとめた束。
    /// パイプライン全体に1インスタンスを渡し、各ステージが共通の診断面を使う。
    /// </summary>
    public sealed class DiagnosticsContext
    {
        public ILogger Log { get; }
        public StageProfiler Profiler { get; }
        public TraceWriter Trace { get; }
        public DumpWriter Dump { get; }

        public DiagnosticsContext(ILogger log, StageProfiler profiler, TraceWriter trace, DumpWriter dump)
        {
            Log = log;
            Profiler = profiler;
            Trace = trace;
            Dump = dump;
        }

        /// <summary>
        /// 環境変数からの既定構成を作る。
        /// <list type="bullet">
        /// <item>L2R_LOG = trace|debug|info|warn|error|silent（既定 info）</item>
        /// <item>L2R_TRACE = JSON トレースの出力パス</item>
        /// <item>L2R_DUMP = 中間ダンプの出力ディレクトリ</item>
        /// </list>
        /// </summary>
        public static DiagnosticsContext FromEnvironment()
        {
            var level = ParseLevel(Environment.GetEnvironmentVariable("L2R_LOG"));
            var logger = new ConsoleLogger(level);
            var profiler = new StageProfiler(logger);
            var trace = new TraceWriter(NullIfEmpty(Environment.GetEnvironmentVariable("L2R_TRACE")));
            var dump = new DumpWriter(NullIfEmpty(Environment.GetEnvironmentVariable("L2R_DUMP")), logger);
            return new DiagnosticsContext(logger, profiler, trace, dump);
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static LogLevel ParseLevel(string? s) => (s?.Trim().ToLowerInvariant()) switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" => LogLevel.Info,
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            "silent" or "off" or "none" => LogLevel.Silent,
            _ => LogLevel.Info,
        };
    }
}
