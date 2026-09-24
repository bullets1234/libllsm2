using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UtauEngineNg.Diagnostics
{
    /// <summary>1つのステージの計測結果。</summary>
    public readonly record struct StageTiming(string Stage, double DurationMs, long Order);

    /// <summary>
    /// パイプラインの各ステージの処理時間を計測する。
    /// <code>
    /// using (profiler.Measure("Analyze")) { ... }
    /// </code>
    /// の形で使い、最後に <see cref="Report"/> で一覧を取得する。
    /// 同名ステージは合算される（ループ内ステージにも対応）。
    /// </summary>
    public sealed class StageProfiler
    {
        private readonly Dictionary<string, double> _totals = new();
        private readonly Dictionary<string, long> _orders = new();
        private long _orderCounter;
        private readonly ILogger? _logger;

        public StageProfiler(ILogger? logger = null)
        {
            _logger = logger;
        }

        public IDisposable Measure(string stage) => new Scope(this, stage);

        private void Record(string stage, double ms)
        {
            if (_totals.TryGetValue(stage, out var cur))
            {
                _totals[stage] = cur + ms;
            }
            else
            {
                _totals[stage] = ms;
                _orders[stage] = _orderCounter++;
            }
            _logger?.Debug("Profiler", $"{stage} took {ms:F2}ms");
        }

        /// <summary>計測順に並べたステージ別合計時間を返す。</summary>
        public IReadOnlyList<StageTiming> Report()
        {
            var list = new List<StageTiming>(_totals.Count);
            foreach (var kv in _totals)
                list.Add(new StageTiming(kv.Key, kv.Value, _orders[kv.Key]));
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return list;
        }

        public double TotalMs
        {
            get
            {
                double sum = 0;
                foreach (var v in _totals.Values) sum += v;
                return sum;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly StageProfiler _owner;
            private readonly string _stage;
            private readonly long _start;

            public Scope(StageProfiler owner, string stage)
            {
                _owner = owner;
                _stage = stage;
                _start = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
                _owner.Record(_stage, ms);
            }
        }
    }
}
