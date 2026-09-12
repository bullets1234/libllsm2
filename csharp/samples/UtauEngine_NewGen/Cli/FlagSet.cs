using System;
using System.Globalization;
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
        /// <summary>J: マイクロプロソディ深度（原音由来ジッター/シマーの転写量、0-100、未指定=0=オフ）。</summary>
        public int Jitter { get; }
        /// <summary>Y: 高域ハイブリッド励振（試験）。高域倍音をピッチ同期ノイズへ再配分（1-100、0=オフ）。</summary>
        public int HybridExcitation { get; }
        /// <summary>Q: 動的声質カーブ（試験）。音高・ノート内位置に Rd/明るさ/息を連動（1-100、0=オフ）。</summary>
        public int VoiceQuality { get; }

        /// <summary>
        /// N: 診断用の機能無効化ビットマスク（アーティファクト切り分け用）。
        /// N1=VSPHSE高域拡張off, N2=位相スムーザoff, N4=残差包絡補正off, N8=eenvクランプoff,
        /// N16=NMテクスチャ実時間転写off, N32=解析/合成の端パディングoff, N64=倍音らしさゲートoff,
        /// N128=残差励振off。
        /// 合算可（例 N3 = 拡張+スムーザoff）。環境変数トグル（L2R_VSEXT等）と OR で効く。
        /// </summary>
        public int DiagDisable { get; }
        public bool DisableVsphseExtension => (DiagDisable & 1) != 0;
        public bool DisableVsphseSmoother => (DiagDisable & 2) != 0;
        public bool DisableResidualCorrection => (DiagDisable & 4) != 0;
        public bool DisableEenvClamp => (DiagDisable & 8) != 0;
        public bool DisableNoiseTexture => (DiagDisable & 16) != 0;
        public bool DisableEdgePad => (DiagDisable & 32) != 0;
        public bool DisableHarmonicityGate => (DiagDisable & 64) != 0;
        public bool DisableResidualExcitation => (DiagDisable & 128) != 0;

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
                          || Raw.Contains("M1", StringComparison.Ordinal);

            Breathiness = Num(@"B(\d+)", def: 50, lo: 0, hi: 100);
            GenderFactor = Num(@"g([+-]?\d+)", def: 0, lo: -100, hi: 100);
            FormantFollow = Num(@"F(\d+)", def: 100, lo: 0, hi: 100);
            UnvoicedAttenuation = OptNum(@"U(\d+)", lo: 6, hi: 20);
            SpectralTilt = Num(@"T([+-]?\d+)", def: 0, lo: -12, hi: 12);
            GrowlStrength = OptNum(@"G(\d+)", lo: 1, hi: 100);
            GlottalClosure = Num(@"K(\d+)", def: 50, lo: 0, hi: 100);
            ConsonantBlend = OptNum(@"C(\d+)", lo: 1, hi: 100);
            HnrEnhancement = OptNum(@"D(\d+)", lo: 1, hi: 100);
            Jitter = Num(@"J(\d+)", def: 0, lo: 0, hi: 100);
            HybridExcitation = OptNum(@"Y(\d+)", lo: 1, hi: 100);
            VoiceQuality = OptNum(@"Q(\d+)", lo: 1, hi: 100);
            DiagDisable = Num(@"N(\d+)", def: 0, lo: 0, hi: 255);
        }

        // UTAU フラグは大文字小文字が意味を持つ（e=弾性ストレッチ / E=FRQ直用 など）ため
        // 常に厳密一致で照合する。ignore-case だと対のフラグが相互に誤発動し、
        // 他エンジン向けフラグ（Mt-50 等）の数値も誤って拾ってしまう。
        // さらに bool フラグは「直後に数字が続かない」場合のみ真とする — 他エンジンの
        // 数値付きフラグ（tn_fnds/moresampler の P86 等）を bool として誤発動させない。
        private bool Has(string token) =>
            Regex.IsMatch(Raw, Regex.Escape(token) + @"(?!\d)");
        private bool HasChar(char c) => Has(c.ToString());

        private int Num(string pattern, int def, int lo, int hi)
        {
            var m = Regex.Match(Raw, pattern);
            if (!m.Success) return def;
            // 桁あふれでも例外にせずクランプ／既定値へ（B99999999999 等の混入対策）
            return long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? (int)Math.Clamp(v, lo, hi)
                : def;
        }

        /// <summary>マッチしないとき 0（オフ）を返す数値フラグ。</summary>
        private int OptNum(string pattern, int lo, int hi)
        {
            var m = Regex.Match(Raw, pattern);
            if (!m.Success) return 0;
            return long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? (int)Math.Clamp(v, lo, hi)
                : 0;
        }
    }
}
