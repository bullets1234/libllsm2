using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace UtauEngineNg.Diagnostics
{
    /// <summary>
    /// 機械可読なトレースを JSON で蓄積・出力する。
    /// パイプライン全体のイベント（ステージ開始/終了、主要パラメータ、警告）を
    /// 1リクエスト=1 JSON ファイルとして書き出す。CI やバッチ検証で差分比較しやすい。
    /// </summary>
    public sealed class TraceWriter
    {
        private readonly List<object> _events = new();
        private readonly Dictionary<string, object?> _summary = new();
        private readonly string? _outputPath;
        private readonly long _startTicks;

        public bool Enabled => _outputPath != null;

        public TraceWriter(string? outputPath)
        {
            _outputPath = outputPath;
            _startTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>イベントを1件記録する（無効時は即 return で低コスト）。</summary>
        public void Event(string stage, string kind, IReadOnlyDictionary<string, object?>? data = null)
        {
            if (!Enabled) return;
            double t = (DateTime.UtcNow.Ticks - _startTicks) / (double)TimeSpan.TicksPerMillisecond;
            var ev = new Dictionary<string, object?>
            {
                ["t_ms"] = Math.Round(t, 3),
                ["stage"] = stage,
                ["kind"] = kind,
            };
            if (data != null)
                foreach (var kv in data) ev[kv.Key] = kv.Value;
            _events.Add(ev);
        }

        /// <summary>最終サマリに残す値（入力概要、最終長さ、フラグなど）。</summary>
        public void SetSummary(string key, object? value)
        {
            if (!Enabled) return;
            _summary[key] = value;
        }

        /// <summary>プロファイル結果をトレースに取り込む。</summary>
        public void AttachTimings(IReadOnlyList<StageTiming> timings)
        {
            if (!Enabled) return;
            var arr = new List<object>(timings.Count);
            foreach (var t in timings)
                arr.Add(new { stage = t.Stage, ms = Math.Round(t.DurationMs, 3) });
            _summary["timings"] = arr;
        }

        /// <summary>蓄積したトレースをファイルへ書き出す。</summary>
        public void Flush()
        {
            if (!Enabled) return;
            try
            {
                var root = new Dictionary<string, object?>
                {
                    ["schema"] = "utauengine-ng/trace/v1",
                    ["generated_utc"] = DateTime.UtcNow.ToString("o"),
                    ["summary"] = _summary,
                    ["events"] = _events,
                };
                var opts = new JsonSerializerOptions { WriteIndented = true };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_outputPath!)) ?? ".");
                File.WriteAllText(_outputPath!, JsonSerializer.Serialize(root, opts));
            }
            catch
            {
                // 診断出力の失敗は本処理を妨げない
            }
        }
    }
}
