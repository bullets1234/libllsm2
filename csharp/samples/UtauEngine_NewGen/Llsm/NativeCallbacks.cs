using System;
using System.Runtime.InteropServices;
using LlsmBindings;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// LLSM コンテナへオブジェクトをアタッチする際に必要な、ネイティブ
    /// デストラクタ／コピーコンストラクタの関数ポインタを一元管理する。
    /// ネイティブ DLL のエクスポートを直接参照する（マネージドデリゲートの
    /// 逆サンクだと GC ファイナライザスレッド／プロセス終了時の
    /// llsm_delete_chunk がマネージドコードへ再入し、ランタイム破棄中の
    /// クラッシュ源になる）。
    /// </summary>
    public static class NativeCallbacks
    {
        private const string Kernel32 = "kernel32.dll";

        [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport(Kernel32, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        private static readonly IntPtr s_hLib;

        static NativeCallbacks()
        {
            // 既に P/Invoke でロード済みの同一モジュール（参照カウント +1）
            s_hLib = LoadLibraryW("libllsm2.dll");
            if (s_hLib == IntPtr.Zero)
                throw new DllNotFoundException("libllsm2.dll not loadable for callback exports");

            IntPtr Get(string name)
            {
                var p = GetProcAddress(s_hLib, name);
                if (p == IntPtr.Zero)
                    throw new EntryPointNotFoundException($"libllsm2.dll: {name} not exported");
                return p;
            }

            DeleteFp = Get("llsm_delete_fp");
            CopyFp = Get("llsm_copy_fp");
            DeleteFpArray = Get("llsm_delete_fparray");
            CopyFpArray = Get("llsm_copy_fparray");
            DeleteNm = Get("llsm_delete_nmframe");
            CopyNm = Get("llsm_copy_nmframe");
            DeleteInt = Get("llsm_delete_int");
            CopyInt = Get("llsm_copy_int");
            DeletePbpEffect = Get("llsm_delete_pbpeffect");
            CopyPbpEffect = Get("llsm_copy_pbpeffect");
            DeleteHm = Get("llsm_delete_hmframe");
            CopyHm = Get("llsm_copy_hmframe");
        }

        /// <summary>FP（単一 float）削除関数ポインタ。</summary>
        public static IntPtr DeleteFp { get; }
        /// <summary>FP（単一 float）コピー関数ポインタ。</summary>
        public static IntPtr CopyFp { get; }
        /// <summary>FP 配列 削除関数ポインタ。</summary>
        public static IntPtr DeleteFpArray { get; }
        /// <summary>FP 配列 コピー関数ポインタ。</summary>
        public static IntPtr CopyFpArray { get; }
        /// <summary>NM フレーム 削除関数ポインタ。</summary>
        public static IntPtr DeleteNm { get; }
        /// <summary>NM フレーム コピー関数ポインタ。</summary>
        public static IntPtr CopyNm { get; }
        /// <summary>int 削除関数ポインタ。</summary>
        public static IntPtr DeleteInt { get; }
        /// <summary>int コピー関数ポインタ。</summary>
        public static IntPtr CopyInt { get; }
        /// <summary>PBP エフェクト 削除関数ポインタ。</summary>
        public static IntPtr DeletePbpEffect { get; }
        /// <summary>PBP エフェクト コピー関数ポインタ。</summary>
        public static IntPtr CopyPbpEffect { get; }
        /// <summary>HM フレーム 削除関数ポインタ。</summary>
        public static IntPtr DeleteHm { get; }
        /// <summary>HM フレーム コピー関数ポインタ。</summary>
        public static IntPtr CopyHm { get; }

        /// <summary>コンテナへ F0（単一 float）をアタッチする。</summary>
        public static void AttachF0(IntPtr container, float f0)
        {
            var p = NativeLLSM.llsm_create_fp(f0);
            // copyctor 必須: NULL だと llsm_copy_container がポインタをエイリアスし、
            // コピー元の解放でコピー側が dangling になる（libllsm2 の契約違反）
            NativeLLSM.llsm_container_attach_(container, NativeLLSM.LLSM_FRAME_F0, p, DeleteFp, CopyFp);
        }

        /// <summary>コンテナへ Rd（単一 float）をアタッチする。</summary>
        public static void AttachRd(IntPtr container, float rd)
        {
            var p = NativeLLSM.llsm_create_fp(rd);
            NativeLLSM.llsm_container_attach_(container, NativeLLSM.LLSM_FRAME_RD, p, DeleteFp, CopyFp);
        }

        /// <summary>コンテナへ float 配列を生成してアタッチする（VTMAGN/VSPHSE 等）。</summary>
        public static void AttachFpArray(IntPtr container, int key, float[] values)
        {
            var arr = NativeLLSM.llsm_create_fparray(values.Length);
            Marshal.Copy(values, 0, arr, values.Length);
            NativeLLSM.llsm_container_attach_(container, key, arr, DeleteFpArray, CopyFpArray);
        }

        /// <summary>既存の fparray ポインタをコピーしてコンテナへアタッチする。</summary>
        public static void AttachFpArrayCopy(IntPtr container, int key, IntPtr srcArray)
        {
            var copy = NativeLLSM.llsm_copy_fparray(srcArray);
            NativeLLSM.llsm_container_attach_(container, key, copy, DeleteFpArray, CopyFpArray);
        }

        /// <summary>NM フレームポインタ（所有権譲渡）をコンテナへアタッチする。</summary>
        public static void AttachNm(IntPtr container, IntPtr nmPtr)
        {
            NativeLLSM.llsm_container_attach_(container, NativeLLSM.LLSM_FRAME_NM, nmPtr, DeleteNm, CopyNm);
        }
    }
}
