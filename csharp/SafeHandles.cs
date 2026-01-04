using System;
using System.Runtime.InteropServices;

namespace LlsmBindings
{
    /// <summary>
    /// 解析オプション (aoptions) の SafeHandle。
    /// 所有権は本ハンドルが持ち、Dispose により <c>llsm_delete_aoptions</c> を呼び出します。
    /// </summary>
    public sealed class AOptionsHandle : SafeHandle
    {
        public AOptionsHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle()
        {
            NativeLLSM.llsm_delete_aoptions(handle);
            return true;
        }
        internal static AOptionsHandle FromExisting(IntPtr p) { var h = new AOptionsHandle(); h.SetHandle(p); return h; }
    }

    /// <summary>
    /// 合成オプション (soptions) の SafeHandle。
    /// </summary>
    public sealed class SOptionsHandle : SafeHandle
    {
        public SOptionsHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle()
        {
            NativeLLSM.llsm_delete_soptions(handle);
            return true;
        }
        internal static SOptionsHandle FromExisting(IntPtr p) { var h = new SOptionsHandle(); h.SetHandle(p); return h; }
    }

    /// <summary>
    /// 解析結果チャンク (chunk) の SafeHandle。
    /// </summary>
    public sealed class ChunkHandle : SafeHandle
    {
        public ChunkHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle()
        {
            NativeLLSM.llsm_delete_chunk(handle);
            return true;
        }
        internal static ChunkHandle FromExisting(IntPtr p) { var h = new ChunkHandle(); h.SetHandle(p); return h; }
    }

    /// <summary>
    /// 合成出力 (output) の SafeHandle。
    /// </summary>
    public sealed class OutputHandle : SafeHandle
    {
        public OutputHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle()
        {
            NativeLLSM.llsm_delete_output(handle);
            return true;
        }
        internal static OutputHandle FromExisting(IntPtr p) { var h = new OutputHandle(); h.SetHandle(p); return h; }
    }

    /// <summary>
    /// 各種コンテナ（conf や frame）への「借用参照」。
    /// 所有権は保持せず、呼び出し側で解放しません（親オブジェクトが生存している前提）。
    /// </summary>
    public readonly struct ContainerRef
    {
        public readonly IntPtr Ptr;
        public bool IsNull => Ptr == IntPtr.Zero;
        public ContainerRef(IntPtr p) { Ptr = p; }
    }
}
