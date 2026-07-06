using System;
using System.Text.RegularExpressions;

namespace UtauEngineNg.Cli
{
    /// <summary>
    /// UTAU フラグ文字列を一括で型付きパースする集中管理クラス。
    /// 元エンジンに散在していた個別 Regex を一箇所へ集約し、トレース可能にする。
    /// </summary>
    public sealed class FlagSet
    {
        /// <summary>元のフラグ文字列（生）。</summary>
        public string Raw { get; }

        // --- bool フラグ ---
        /// <summary>P: FRQ をバイパス（PYIN で F0 推定）。</summary>
        public bool BypassFrq { get; }
        /// <summary>E: FRQ の F0 をそのまま使用。</summary>
        public bool UseFrqF0 { get; }
        /// <summary>Z: 純 FRQ モード（PYIN の V/UV マスクを無効化、Whisper 音源向け）。</summary>
        public bool PureFrq { get; }
        /// <summary>H: 高解像度モード（F0 に応じた動的倍音数）。</summary>
        public bool HighResolution { get; }
        /// <summary>W: 適応窓サイズ。</summary>
        public bool AdaptiveWindow { get; }
        /// <summary>S: ノイズ成分もピッチシフトに追従。</summary>
        public bool ShiftNoise { get; }
        /// <summary>R: チャンクレベル RPS。</summary>
        public bool ChunkRps { get; }
        /// <summary>e: 弾性タイムストレッチ（トランジェント保護）。</summary>
        public bool ElasticStretch { get; }
        /// <summary>A: ピッチマーク駆動ストレッチ。</summary>
        public bool PitchMarkStretch { get; }
        /// <summary>M+ / M1: ノート境界でのモジュレーションフェード。</summary>
        public bool ModulationPlus { get; }
        /// <summary>O: 4x オーバーサンプリング。</summary>
        public bool Oversampling { get; }
        /// <summary>L: 声門パラメータ自動推定 + リップ放射フィルタ。</summary>
        public bool GlottalAutoEstimate { get; }
        /// <summary>X: 固定振幅比モード（targetF0/srcF0）。</summary>
        public bool FixedAmplitudeRatio { get; }
        /// <summary>V: F0 ベースの有声境界自動検出。</summary>
        public bool F0Boundary { get; }

        // --- 数値フラグ ---
        /// <summary>B: 息成分（0-100、既定 50）。</summary>
        public int Breathiness { get; }
        /// <summary>g（小文字）: ジェンダーファクター（±、既定 0）。</summary>
        public int GenderFactor { get; }
        /// <summary>F: フォルマント追従率（0-100、既定 100）。</summary>
        public int FormantFollow { get; }
        /// <summary>U: 無声音減衰 dB（6-20、0=オフ）。</summary>
        public int UnvoicedAttenuation { get; }
        /// <summary>T: スペクトル傾斜 dB/oct（±12、既定 0）。</summary>
        public int SpectralTilt { get; }
        /// <summary>G（大文字）: グロウル強度（1-100、0=オフ）。</summary>
        public int GrowlStrength { get; }
        /// <summary>K: 声門閉鎖係数（0-100、既定 50）。</summary>
        public int GlottalClosure { get; }
        /// <summary>C: 子音原音ブレンド率（1-100、0=オフ）。</summary>
        public int ConsonantBlend { get; }
        /// <summary>D: HNR 改善強度（1-100、0=オフ）。</summary>
        public int HnrEnhancement { get; }

        public FlagSet(string? flags)
        {
            Raw = flags ?? "";

            BypassFrq = Has("P");
            UseFrqF0 = Has("E");
            PureFrq = Has("Z");
            HighResolution = Has("H");
            AdaptiveWindow = Has("W");
            ShiftNoise = Has("S");
            ChunkRps = Has("R");
            ElasticStretch = Has("e");
            PitchMarkStretch = Has("A");
            Oversampling = Has("O");
            GlottalAutoEstimate = Has("L");
            FixedAmplitudeRatio = HasChar('X');
            F0Boundary = Has("V");
            ModulationPlus = Raw.Contains("M+", StringComparison.Ordinal)
                          || Raw.Contains("M1", StringComparison.OrdinalIgnoreCase);

            Breathiness = Num(@"B(\d+)", ignoreCase: true, def: 50, lo: 0, hi: 100);
            GenderFactor = Num(@"g([+-]?\d+)", ignoreCase: false, def: 0, lo: -100, hi: 100);
            FormantFollow = Num(@"F(\d+)", ignoreCase: true, def: 100, lo: 0, hi: 100);
            UnvoicedAttenuation = OptNum(@"U(\d+)", ignoreCase: true, lo: 6, hi: 20);
            SpectralTilt = Num(@"T([+-]?\d+)", ignoreCase: true, def: 0, lo: -12, hi: 12);
            GrowlStrength = OptNum(@"G(\d+)", ignoreCase: false, lo: 1, hi: 100);
            GlottalClosure = Num(@"K(\d+)", ignoreCase: true, def: 50, lo: 0, hi: 100);
            ConsonantBlend = OptNum(@"C(\d+)", ignoreCase: false, lo: 1, hi: 100);
            HnrEnhancement = OptNum(@"D(\d+)", ignoreCase: false, lo: 1, hi: 100);
        }

        private bool Has(string token) => Raw.Contains(token, StringComparison.OrdinalIgnoreCase);
        private bool HasChar(char c) => Raw.Contains(c, StringComparison.OrdinalIgnoreCase);

        private int Num(string pattern, bool ignoreCase, int def, int lo, int hi)
        {
            var m = Regex.Match(Raw, pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            if (!m.Success) return def;
            return Math.Clamp(int.Parse(m.Groups[1].Value), lo, hi);
        }

        /// <summary>マッチしないとき 0（オフ）を返す数値フラグ。</summary>
        private int OptNum(string pattern, bool ignoreCase, int lo, int hi)
        {
            var m = Regex.Match(Raw, pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            if (!m.Success) return 0;
            return Math.Clamp(int.Parse(m.Groups[1].Value), lo, hi);
        }
    }
}
