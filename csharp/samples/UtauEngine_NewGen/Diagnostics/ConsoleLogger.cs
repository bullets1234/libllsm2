using System;

namespace UtauEngineNg.Diagnostics
{
    /// <summary>
    /// コンソールへ出力する構造化ロガー。
    /// 形式: <c>[+12.3ms] [INFO ] [Stage] message</c>
    /// 経過時間は生成時刻からの相対値で、トレースを時系列で追える。
    /// </summary>
    public sealed class ConsoleLogger : ILogger
    {
        private readonly long _startTicks;
        private readonly object _gate = new();

        public LogLevel MinLevel { get; }

        public ConsoleLogger(LogLevel minLevel = LogLevel.Info)
        {
            MinLevel = minLevel;
            _startTicks = DateTime.UtcNow.Ticks;
        }

        public bool IsEnabled(LogLevel level) => level >= MinLevel && MinLevel != LogLevel.Silent;

        public void Log(LogLevel level, string stage, string message)
        {
            if (!IsEnabled(level)) return;

            double elapsedMs = (DateTime.UtcNow.Ticks - _startTicks) / (double)TimeSpan.TicksPerMillisecond;
            string line = $"[{elapsedMs,8:F1}ms] [{LevelTag(level)}] [{stage}] {message}";

            lock (_gate)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = LevelColor(level);
                Console.WriteLine(line);
                Console.ForegroundColor = prev;
            }
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO ",
            LogLevel.Warn => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "     ",
        };

        private static ConsoleColor LevelColor(LogLevel level) => level switch
        {
            LogLevel.Trace => ConsoleColor.DarkGray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Info => ConsoleColor.White,
            LogLevel.Warn => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => ConsoleColor.Gray,
        };
    }
}
