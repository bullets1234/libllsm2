using LlsmBindings;

namespace UtauEngineNg.Effects
{
    /// <summary>フレーム単位エフェクトに渡す文脈。</summary>
    public readonly struct FrameEffectContext
    {
        /// <summary>対象フレーム（補間後の dstChunk フレーム）。</summary>
        public ContainerRef Frame { get; init; }
        /// <summary>出力フレーム番号（決定論的テクスチャ生成のシード）。負値は未指定。</summary>
        public int OutFrameIdx { get; init; }
        /// <summary>ピッチ比（newF0 / srcF0）。フォルマント追従用。</summary>
        public float PitchRatio { get; init; }
        /// <summary>サンプリング周波数（Hz）。</summary>
        public int Fs { get; init; }
    }

    /// <summary>補間後の各フレームに適用されるエフェクト。</summary>
    public interface IFrameEffect
    {
        /// <summary>このエフェクトが有効か（無効ならパイプラインがスキップ）。</summary>
        bool IsActive { get; }
        /// <summary>1 フレームへ適用する。</summary>
        void Apply(in FrameEffectContext ctx);
    }

    /// <summary>チャンク全体に対して一括適用されるエフェクト。</summary>
    public interface IChunkEffect
    {
        /// <summary>このエフェクトが有効か。</summary>
        bool IsActive { get; }
        /// <summary>チャンク全体へ適用する。</summary>
        void Apply(ChunkHandle chunk, int nfrm, int fs);
    }
}
