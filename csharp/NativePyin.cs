using System;
using System.Runtime.InteropServices;

namespace LlsmBindings
{
    /// <summary>
    /// PYIN の設定構造体（ネイティブと同一レイアウト）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PyinConfig
    {
        public float fmin;
        public float fmax;
        public int nq;
        public int w;
        public float beta_a;
        public float beta_u;
        public float threshold;
        public float bias;
        public float ptrans;
        public int trange;
        public int nf;
        public int nhop;
    }

    internal static class NativePyin
    {
        private const string Dll = "libpyin.dll";

        /// <summary>
        /// 既定値で設定を初期化します（<c>nhop</c> はサンプル数）。
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern PyinConfig pyin_init(int nhop);

        /// <summary>
        /// 探索レンジ（半音等価の離散ビン数）を f0 範囲から計算します。
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int pyin_trange(int nq, float fmin, float fmax);

        /// <summary>
        /// 波形から F0 列を推定し、ネイティブ配列を返します（呼び出し側で <see cref="pyin_free"/> を必ず呼ぶ）。
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr pyin_analyze(PyinConfig param, float[] x, int nx, float fs, ref int nfrm);

        /// <summary>
        /// <see cref="pyin_analyze"/> の戻り配列を解放します。
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void pyin_free(IntPtr p);
    }
}
