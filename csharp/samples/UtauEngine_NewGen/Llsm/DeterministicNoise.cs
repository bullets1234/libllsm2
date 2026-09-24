namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// 決定論的ハッシュノイズ。(frameIdx, binIdx) から再現可能な擬似乱数 [-0.5, 0.5] を生成する。
    /// 同一入力で常に同一出力となるため、タイムストレッチでも一貫したテクスチャを保つ。
    /// </summary>
    public static class DeterministicNoise
    {
        /// <summary>[-0.5, 0.5] の決定論的ノイズ値。</summary>
        public static float Hash(int frameIdx, int binIdx)
        {
            uint h = (uint)(frameIdx * 73856093 ^ binIdx * 19349663);
            h *= 2654435761u;
            h ^= h >> 16;
            h *= 0x45d9f3bu;
            h ^= h >> 16;
            return (float)(h & 0xFFFF) / 65536f - 0.5f;
        }
    }
}
