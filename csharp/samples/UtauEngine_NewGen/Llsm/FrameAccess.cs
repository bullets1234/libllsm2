using System;
using System.Runtime.InteropServices;
using LlsmBindings;

namespace UtauEngineNg.Llsm
{
    /// <summary>
    /// LLSM フレーム（<see cref="ContainerRef"/>）の調波モデル(HM)・雑音モデル(NM)・
    /// 声道スペクトル(VTMAGN) へ型安全にアクセスするための薄いラッパ群。
    /// 元エンジンに散在していた Marshal ボイラープレートを一箇所へ集約し、
    /// ポインタ操作のミスを防ぐ（積極リファクタの一環）。
    /// </summary>
    public static class FrameAccess
    {
        public static float GetF0(ContainerRef frame) => LlsmBindings.Llsm.GetFrameF0(frame);
        public static void SetF0(ContainerRef frame, float f0) => LlsmBindings.Llsm.SetFrameF0(frame, f0);

        /// <summary>HM が存在し nhar&gt;0 なら有声とみなす。</summary>
        public static bool IsVoiced(ContainerRef frame)
        {
            if (GetF0(frame) <= 0) return false;
            var hm = TryGetHm(frame);
            return hm.HasValue && hm.Value.NHar > 0;
        }

        /// <summary>HM ビューを取得（存在しなければ null）。</summary>
        public static HmView? TryGetHm(ContainerRef frame)
        {
            IntPtr hmPtr = LlsmBindings.Llsm.GetFrameHM(frame);
            if (hmPtr == IntPtr.Zero) return null;
            var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(hmPtr);
            return new HmView(hm);
        }

        /// <summary>NM ビューを取得（存在しなければ null）。</summary>
        public static NmView? TryGetNm(ContainerRef frame)
        {
            IntPtr nmPtr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_NM);
            if (nmPtr == IntPtr.Zero) return null;
            var nm = Marshal.PtrToStructure<NativeLLSM.llsm_nmframe>(nmPtr);
            return new NmView(nm);
        }

        /// <summary>声道スペクトル(VTMAGN, dB)を長さ自動判定で読む。なければ空配列。</summary>
        public static float[] ReadVtMagn(ContainerRef frame)
        {
            IntPtr ptr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (ptr == IntPtr.Zero) return Array.Empty<float>();
            int len = NativeLLSM.llsm_fparray_length(ptr);
            if (len <= 0) return Array.Empty<float>();
            var v = new float[len];
            Marshal.Copy(ptr, v, 0, len);
            return v;
        }

        /// <summary>VTMAGN を書き戻す（長さは既存配列に合わせる）。</summary>
        public static void WriteVtMagn(ContainerRef frame, float[] vtmagn)
        {
            IntPtr ptr = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            if (ptr == IntPtr.Zero) return;
            int len = NativeLLSM.llsm_fparray_length(ptr);
            if (len <= 0) return;
            Marshal.Copy(vtmagn, 0, ptr, Math.Min(len, vtmagn.Length));
        }
    }

    /// <summary>調波モデル(HM)のビュー。読み書きはマネージ配列経由。</summary>
    public readonly struct HmView
    {
        private readonly NativeLLSM.llsm_hmframe _hm;
        internal HmView(NativeLLSM.llsm_hmframe hm) => _hm = hm;

        public int NHar => _hm.nhar;
        public bool HasAmplitudes => _hm.ampl != IntPtr.Zero && _hm.nhar > 0;
        public bool HasPhases => _hm.phse != IntPtr.Zero && _hm.nhar > 0;

        public float[] ReadAmplitudes()
        {
            var a = new float[_hm.nhar];
            if (HasAmplitudes) Marshal.Copy(_hm.ampl, a, 0, _hm.nhar);
            return a;
        }

        public void WriteAmplitudes(float[] ampl)
        {
            if (!HasAmplitudes) return;
            Marshal.Copy(ampl, 0, _hm.ampl, Math.Min(ampl.Length, _hm.nhar));
        }

        public float[] ReadPhases()
        {
            var p = new float[_hm.nhar];
            if (HasPhases) Marshal.Copy(_hm.phse, p, 0, _hm.nhar);
            return p;
        }

        public void WritePhases(float[] phse)
        {
            if (!HasPhases) return;
            Marshal.Copy(phse, 0, _hm.phse, Math.Min(phse.Length, _hm.nhar));
        }
    }

    /// <summary>雑音モデル(NM)のビュー。PSD と チャンネル毎の eenv へアクセスする。</summary>
    public readonly struct NmView
    {
        private readonly NativeLLSM.llsm_nmframe _nm;
        internal NmView(NativeLLSM.llsm_nmframe nm) => _nm = nm;

        public int NPsd => _nm.npsd;
        public int NChannel => _nm.nchannel;
        public bool HasPsd => _nm.psd != IntPtr.Zero && _nm.npsd > 0;
        public bool HasEenv => _nm.eenv != IntPtr.Zero && _nm.nchannel > 0;
        public bool HasEdc => _nm.edc != IntPtr.Zero && _nm.nchannel > 0;

        public float[] ReadPsd()
        {
            var p = new float[_nm.npsd];
            if (HasPsd) Marshal.Copy(_nm.psd, p, 0, _nm.npsd);
            return p;
        }

        public void WritePsd(float[] psd)
        {
            if (!HasPsd) return;
            Marshal.Copy(psd, 0, _nm.psd, Math.Min(psd.Length, _nm.npsd));
        }

        public float[] ReadEdc()
        {
            var e = new float[_nm.nchannel];
            if (HasEdc) Marshal.Copy(_nm.edc, e, 0, _nm.nchannel);
            return e;
        }

        /// <summary>チャンネル毎の eenv（<see cref="HmView"/>）を列挙する。</summary>
        public HmView? GetEenvChannel(int channel)
        {
            if (!HasEenv || channel < 0 || channel >= _nm.nchannel) return null;
            IntPtr ptr = Marshal.ReadIntPtr(_nm.eenv, channel * IntPtr.Size);
            if (ptr == IntPtr.Zero) return null;
            var hm = Marshal.PtrToStructure<NativeLLSM.llsm_hmframe>(ptr);
            return new HmView(hm);
        }
    }
}
