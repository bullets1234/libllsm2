using System;
using System.Runtime.InteropServices;
using LlsmBindings;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// LLSM コンテナへオブジェクトをアタッチする際に必要な、ネイティブ
    /// デストラクタ／コピーコンストラクタの関数ポインタを一元管理する。
    /// デリゲートは GC されないよう static フィールドで保持する。
    /// （UtauEngine に散在していた _deleteFp 等の関数ポインタ取得を集約＝改善）
    /// </summary>
    public static class NativeCallbacks
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DeleteDelegate(IntPtr p);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CopyDelegate(IntPtr p);

        // GC 防止のためインスタンスを保持
        private static readonly DeleteDelegate s_deleteFp = NativeLLSM.llsm_delete_fp;
        private static readonly DeleteDelegate s_deleteFpArray = NativeLLSM.llsm_delete_fparray;
        private static readonly DeleteDelegate s_deleteNm = NativeLLSM.llsm_delete_nmframe;
        private static readonly CopyDelegate s_copyFpArray = NativeLLSM.llsm_copy_fparray;
        private static readonly CopyDelegate s_copyNm = NativeLLSM.llsm_copy_nmframe;
        private static readonly DeleteDelegate s_deleteInt = NativeLLSM.llsm_delete_int;
        private static readonly CopyDelegate s_copyInt = NativeLLSM.llsm_copy_int;
        private static readonly DeleteDelegate s_deletePbpEffect = NativeLLSM.llsm_delete_pbpeffect;
        private static readonly CopyDelegate s_copyPbpEffect = NativeLLSM.llsm_copy_pbpeffect;
        private static readonly DeleteDelegate s_deleteHm = NativeLLSM.llsm_delete_hmframe;
        private static readonly CopyDelegate s_copyHm = NativeLLSM.llsm_copy_hmframe;

        /// <summary>FP（単一 float）削除関数ポインタ。</summary>
        public static IntPtr DeleteFp { get; } = Marshal.GetFunctionPointerForDelegate(s_deleteFp);
        /// <summary>FP 配列 削除関数ポインタ。</summary>
        public static IntPtr DeleteFpArray { get; } = Marshal.GetFunctionPointerForDelegate(s_deleteFpArray);
        /// <summary>NM フレーム 削除関数ポインタ。</summary>
        public static IntPtr DeleteNm { get; } = Marshal.GetFunctionPointerForDelegate(s_deleteNm);
        /// <summary>FP 配列 コピー関数ポインタ。</summary>
        public static IntPtr CopyFpArray { get; } = Marshal.GetFunctionPointerForDelegate(s_copyFpArray);
        /// <summary>NM フレーム コピー関数ポインタ。</summary>
        public static IntPtr CopyNm { get; } = Marshal.GetFunctionPointerForDelegate(s_copyNm);
        /// <summary>int 削除関数ポインタ。</summary>
        public static IntPtr DeleteInt { get; } = Marshal.GetFunctionPointerForDelegate(s_deleteInt);
        /// <summary>int コピー関数ポインタ。</summary>
        public static IntPtr CopyInt { get; } = Marshal.GetFunctionPointerForDelegate(s_copyInt);
        /// <summary>PBP エフェクト 削除関数ポインタ。</summary>
        public static IntPtr DeletePbpEffect { get; } = Marshal.GetFunctionPointerForDelegate(s_deletePbpEffect);
        /// <summary>PBP エフェクト コピー関数ポインタ。</summary>
        public static IntPtr CopyPbpEffect { get; } = Marshal.GetFunctionPointerForDelegate(s_copyPbpEffect);

        /// <summary>HM フレーム 削除関数ポインタ。</summary>
        public static IntPtr DeleteHm { get; } = Marshal.GetFunctionPointerForDelegate(s_deleteHm);
        /// <summary>HM フレーム コピー関数ポインタ。</summary>
        public static IntPtr CopyHm { get; } = Marshal.GetFunctionPointerForDelegate(s_copyHm);

        /// <summary>コンテナへ F0（単一 float）をアタッチする。</summary>
        public static void AttachF0(IntPtr container, float f0)
        {
            var p = NativeLLSM.llsm_create_fp(f0);
            NativeLLSM.llsm_container_attach_(container, NativeLLSM.LLSM_FRAME_F0, p, DeleteFp, IntPtr.Zero);
        }

        /// <summary>コンテナへ Rd（単一 float）をアタッチする。</summary>
        public static void AttachRd(IntPtr container, float rd)
        {
            var p = NativeLLSM.llsm_create_fp(rd);
            NativeLLSM.llsm_container_attach_(container, NativeLLSM.LLSM_FRAME_RD, p, DeleteFp, IntPtr.Zero);
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
