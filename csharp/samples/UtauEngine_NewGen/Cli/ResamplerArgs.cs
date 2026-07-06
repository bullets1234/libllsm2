using System;
using System.Collections.Generic;

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
        public int Tempo { get; init; }
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
            int GetInt(int i, int def) => args.Length > i && int.TryParse(args[i], out var v) ? v : def;
            float GetFloat(int i, float def) => args.Length > i && float.TryParse(args[i], out var v) ? v : def;

            int tempo = 120;
            var pitchBend = new List<int>();
            if (args.Length > 11)
                (tempo, pitchBend) = PitchBendDecoder.ParseWithTempo(args, 11);

            string flags = Get(4, "");

            return new ResamplerArgs
            {
                InputWav = Get(0, ""),
                OutputWav = Get(1, ""),
                PitchName = Get(2, "C4"),
                Velocity = GetInt(3, 100),
                Flags = flags,
                Offset = GetFloat(5, 0),
                LengthMs = GetFloat(6, 0),
                Consonant = GetFloat(7, 0),
                Cutoff = GetFloat(8, 0),
                Volume = GetInt(9, 100),
                Modulation = GetInt(10, 0),
                Tempo = tempo,
                PitchBend = pitchBend,
                ParsedFlags = new FlagSet(flags),
            };
        }
    }
}
