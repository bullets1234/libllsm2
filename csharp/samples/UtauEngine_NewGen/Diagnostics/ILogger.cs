using System;

namespace UtauEngineNg.Diagnostics
{
    /// <summary>ログの重要度レベル。</summary>
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Silent = 5,
    }

    /// <summary>
    /// 構造化ログのインターフェース。各メッセージはステージ名で分類され、
    /// レベルでフィルタリングできる。実装は <see cref="ConsoleLogger"/> など。
    /// </summary>
    public interface ILogger
    {
        LogLevel MinLevel { get; }

        void Log(LogLevel level, string stage, string message);

        /// <summary>指定レベルが出力対象かどうか（重い文字列生成の前に判定するため）。</summary>
        bool IsEnabled(LogLevel level);
    }

    /// <summary>ILogger の拡張ヘルパ（ステージ名付きの簡易呼び出し）。</summary>
    public static class LoggerExtensions
    {
        public static void Trace(this ILogger log, string stage, string message)
        {
            if (log.IsEnabled(LogLevel.Trace)) log.Log(LogLevel.Trace, stage, message);
        }

        public static void Debug(this ILogger log, string stage, string message)
        {
            if (log.IsEnabled(LogLevel.Debug)) log.Log(LogLevel.Debug, stage, message);
        }

        public static void Info(this ILogger log, string stage, string message)
        {
            if (log.IsEnabled(LogLevel.Info)) log.Log(LogLevel.Info, stage, message);
        }

        public static void Warn(this ILogger log, string stage, string message)
        {
            if (log.IsEnabled(LogLevel.Warn)) log.Log(LogLevel.Warn, stage, message);
        }

        public static void Error(this ILogger log, string stage, string message)
        {
            if (log.IsEnabled(LogLevel.Error)) log.Log(LogLevel.Error, stage, message);
        }
    }
}
