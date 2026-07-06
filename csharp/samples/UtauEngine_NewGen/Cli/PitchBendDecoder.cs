using System;
using System.Collections.Generic;

namespace UtauEngineNg.Cli
{
    /// <summary>
    /// UTAU のピッチベンド文字列をデコードする。
    /// 形式: <c>!tempo</c> でテンポ指定、独自 Base64 2文字=1値（-2048..2047 cent）、
    /// <c>#n#</c> で直前値の n 回リピート。
    /// （UtauEngine の ParsePitchBendWithTempo 系を移植）
    /// </summary>
    public static class PitchBendDecoder
    {
        private const string Base64Chars =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        /// <summary>引数配列の startIndex 以降からテンポとピッチベンド列を取り出す。</summary>
        public static (int tempo, List<int> pitchBend) ParseWithTempo(string[] args, int startIndex)
        {
            var result = new List<int>();
            int tempo = 120;

            for (int i = startIndex; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg)) continue;

                if (arg.StartsWith("!", StringComparison.Ordinal))
                {
                    if (int.TryParse(arg.Substring(1), out int t)) tempo = t;
                    continue;
                }

                result.AddRange(DecodeWithRepeat(arg));
            }

            return (tempo, result);
        }

        /// <summary>#n# リピート記号を展開しつつデコードする。</summary>
        public static List<int> DecodeWithRepeat(string input)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(input)) return result;

            int pos = 0;
            int lastValue = 0;

            while (pos < input.Length)
            {
                int hashStart = input.IndexOf('#', pos);

                if (hashStart < 0)
                {
                    var decoded = Decode(input.Substring(pos));
                    result.AddRange(decoded);
                    if (decoded.Count > 0) lastValue = decoded[^1];
                    break;
                }

                if (hashStart > pos)
                {
                    var decoded = Decode(input.Substring(pos, hashStart - pos));
                    result.AddRange(decoded);
                    if (decoded.Count > 0) lastValue = decoded[^1];
                }

                int hashEnd = input.IndexOf('#', hashStart + 1);
                if (hashEnd < 0)
                {
                    var decoded = Decode(input.Substring(hashStart + 1));
                    result.AddRange(decoded);
                    break;
                }

                string repeatStr = input.Substring(hashStart + 1, hashEnd - hashStart - 1);
                if (int.TryParse(repeatStr, out int repeatCount))
                {
                    for (int i = 0; i < repeatCount; i++)
                        result.Add(lastValue);
                }

                pos = hashEnd + 1;
            }

            return result;
        }

        /// <summary>UTAU 独自 Base64（2文字=12bit符号付き、中心0）をデコード。</summary>
        public static List<int> Decode(string base64)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(base64)) return result;

            for (int i = 0; i < base64.Length; i += 2)
            {
                if (i + 1 >= base64.Length) break;

                int idx1 = Base64Chars.IndexOf(base64[i]);
                int idx2 = Base64Chars.IndexOf(base64[i + 1]);
                if (idx1 < 0 || idx2 < 0) continue;

                int value = idx1 * 64 + idx2;
                if (value >= 2048) value -= 4096;
                result.Add(value);
            }

            return result;
        }
    }
}
