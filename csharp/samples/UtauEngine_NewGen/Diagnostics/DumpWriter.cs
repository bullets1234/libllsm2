using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace UtauEngineNg.Diagnostics
{
    /// <summary>
    /// 中間成果物（F0トラック、波形、フレームパラメータ）をファイルに書き出す。
    /// デバッグ時に各ステージの出力を可視化・比較するために使う。
    /// 無効時はすべて no-op。出力先ディレクトリが指定された場合のみ動作する。
    /// </summary>
    public sealed class DumpWriter
    {
        private readonly string? _dir;
        private readonly ILogger? _logger;

        public bool Enabled => _dir != null;

        public DumpWriter(string? directory, ILogger? logger = null)
        {
            _dir = directory;
            _logger = logger;
            if (_dir != null)
            {
                try { Directory.CreateDirectory(_dir); }
                catch { _dir = null; }
            }
        }

        /// <summary>F0 トラックを CSV（index,f0Hz）で書き出す。</summary>
        public void DumpF0(string name, float[] f0, double hopSeconds)
        {
            if (!Enabled) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("index,time_s,f0_hz");
                for (int i = 0; i < f0.Length; i++)
                    sb.Append(i).Append(',')
                      .Append((i * hopSeconds).ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                      .Append(f0[i].ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(PathFor($"{name}.f0.csv"), sb.ToString());
                _logger?.Debug("Dump", $"F0 '{name}' ({f0.Length} frames)");
            }
            catch { }
        }

        /// <summary>波形を 16bit WAV で書き出す。</summary>
        public void DumpWav(string name, float[] samples, int sampleRate)
        {
            if (!Enabled) return;
            try
            {
                Audio.WavIo.WriteMono16(PathFor($"{name}.wav"), samples, sampleRate);
                _logger?.Debug("Dump", $"WAV '{name}' ({samples.Length} samples @ {sampleRate}Hz)");
            }
            catch { }
        }

        /// <summary>任意のキー=値テキストを書き出す（パラメータスナップショット）。</summary>
        public void DumpText(string name, string content)
        {
            if (!Enabled) return;
            try
            {
                File.WriteAllText(PathFor($"{name}.txt"), content);
                _logger?.Debug("Dump", $"text '{name}'");
            }
            catch { }
        }

        private string PathFor(string file) => Path.Combine(_dir!, file);
    }
}
