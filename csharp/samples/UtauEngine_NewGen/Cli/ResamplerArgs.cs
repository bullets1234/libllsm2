using System;
using System.Collections.Generic;
using System.Globalization;

namespace UtauEngineNg.Cli
{
    /// <summary>
    /// UTAU リサンプラーの位置引数を解析した結果。
    /// 引数順: 入力wav, 出力wav, 音名, 子音速度, フラグ, オフセット(ms),
    /// 要求長(ms), 子音部(ms), カットオフ(ms), 音量, モジュレーション, [テンポ!], [ピッチベンド...]
    /// </summary>
    public sealed class ResamplerArgs
    {
        public required string InputWav { get; init; }
        public required string OutputWav { get; init; }
        public required string PitchName { get; init; }
        public int Velocity { get; init; }
        public string Flags { get; init; } = "";
        public float Offset { get; init; }
        public float LengthMs { get; init; }
        public float Consonant { get; init; }
        public float Cutoff { get; init; }
        public int Volume { get; init; }
        public int Modulation { get; init; }
        public float Tempo { get; init; }
        public IReadOnlyList<int> PitchBend { get; init; } = Array.Empty<int>();

        /// <summary>解析済みフラグ。</summary>
        public FlagSet ParsedFlags { get; init; } = new FlagSet("");

        /// <summary>目標 F0 (Hz)。</summary>
        public float TargetF0 => NoteFrequency.NoteNameToHz(PitchName);

        /// <summary>引数が最低限（入力/出力）を満たすか。</summary>
        public bool IsValid => InputWav.Length > 0 && OutputWav.Length > 0;

        public static ResamplerArgs Parse(string[] args)
        {
            string Get(int i, string def) => args.Length > i ? args[i] : def;
            // UTAU は小数点 '.' 固定で数値を渡すため、ロケール非依存（InvariantCulture）で
            // パースする。既定カルチャだと欧州圏 Windows（小数点=','）で "25.5" が失敗し、
            // offset/cutoff 等が黙って 0 になる。整数引数も "100.0" 形式に耐えるよう
            // float として受けて丸める。
            float GetFloat(int i, float def) =>
                args.Length > i && float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)
                    ? v : def;
            int GetInt(int i, int def) => (int)MathF.Round(GetFloat(i, def));

            float tempo = 120f;
            var pitchBend = new List<int>();
            if (args.Length > 11)
                (tempo, pitchBend) = PitchBendDecoder.ParseWithTempo(args, 11);

            string flags = Get(4, "");

            return new ResamplerArgs
            {
                InputWav = Get(0, ""),
                OutputWav = Get(1, ""),
                PitchName = Get(2, "C4"),
                Velocity = Math.Clamp(GetInt(3, 100), 0, 200),
                Flags = flags,
                Offset = GetFloat(5, 0),
                LengthMs = GetFloat(6, 0),
                Consonant = GetFloat(7, 0),
                Cutoff = GetFloat(8, 0),
                Volume = Math.Clamp(GetInt(9, 100), 0, 200),
                Modulation = Math.Clamp(GetInt(10, 0), -200, 200),
                Tempo = tempo,
                PitchBend = pitchBend,
                ParsedFlags = new FlagSet(flags),
            };
        }
    }
}
