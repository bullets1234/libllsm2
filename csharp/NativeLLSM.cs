using System;
using System.Runtime.InteropServices;

namespace LlsmBindings
{
    public static class NativeLLSM
    {
        private const string Dll = "libllsm2.dll";

        // llsm.h に定義されたインデックス（コンテナのスロット）
        public const int LLSM_FRAME_F0 = 0;
        public const int LLSM_FRAME_HM = 1;
        public const int LLSM_FRAME_NM = 2;
        public const int LLSM_FRAME_PSDRES = 3;
        public const int LLSM_FRAME_PBPEFF = 8;
        public const int LLSM_FRAME_PBPSYN = 9;
        public const int LLSM_FRAME_RD = 10;
        public const int LLSM_FRAME_VTMAGN = 11;
        public const int LLSM_FRAME_VSPHSE = 12;

        public const int LLSM_CONF_NFRM = 0;
        public const int LLSM_CONF_THOP = 1;
        public const int LLSM_CONF_MAXNHAR = 2;
        public const int LLSM_CONF_MAXNHAR_E = 3;
        public const int LLSM_CONF_NPSD = 4;
        public const int LLSM_CONF_FNYQ = 6;
        public const int LLSM_CONF_NCHANNEL = 7;
        public const int LLSM_CONF_CHANFREQ = 8;
        public const int LLSM_CONF_NSPEC = 10;
        public const int LLSM_CONF_LIPRADIUS = 11;

        public const int LLSM_AOPTION_HMPP = 0;
        public const int LLSM_AOPTION_HMCZT = 1;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_aoptions();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_aoptions(IntPtr opt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_aoptions_toconf(IntPtr opt, float fnyq);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_soptions(float fs);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_soptions(IntPtr opt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_analyze(IntPtr aoptions, float[] x, int nx, float fs, float[] f0, int nfrm, IntPtr x_ap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_synthesize(IntPtr soptions, IntPtr chunk);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_chunk(IntPtr chunk);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_chunk(IntPtr conf, int init_frames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_copy_chunk(IntPtr chunk);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_copy_container(IntPtr src);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_output(IntPtr output);

        // coder (feature encoder/decoder)
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_coder(IntPtr conf, int order_spec, int order_bap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_coder(IntPtr coder);

        // NOTE: FP_TYPE は libllsm2 側で float のため、ここでは float[] を渡す
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_coder_encode(IntPtr coder, IntPtr frame);

        // Layer1 frame decode from feature vector
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_coder_decode_layer1(IntPtr coder, float[] enc);

        // フレーム/チャンク単位のレイヤー変換/位相伝播
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_frame_tolayer0(IntPtr frame, IntPtr conf);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_chunk_tolayer0(IntPtr chunk);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_chunk_tolayer1(IntPtr chunk, int nfft);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_chunk_phasepropagate(IntPtr chunk, int sign);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_chunk_phasesync_rps(IntPtr chunk, IntPtr f0, int nfrm);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_frame_phasesync_rps(IntPtr frame, int layer1_based);

        // コンテナ操作（get/attach/new/delete）
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_container_get(IntPtr src, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_container(IntPtr c);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_fp(float x);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_fparray(int size);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_fp(IntPtr p);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_fparray(IntPtr p);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_copy_fparray(IntPtr p);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int llsm_fparray_length(IntPtr p);

        // デストラクタ/コピー関数ポインタを伴う attach 変種（所有権移譲/ディープコピー制御）
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_container_attach_(IntPtr dst, int index, IntPtr ptr, IntPtr dtor, IntPtr copyctor);

        // 出力構造体のミラー（メモリレイアウト固定）
        [StructLayout(LayoutKind.Sequential)]
        public struct llsm_output
        {
            public int ny;
            public float fs;
            public IntPtr y;
            public IntPtr y_sin;
            public IntPtr y_noise;
        }

        // チャンク構造体のミラー（llsm.h の定義と一致させる）
        [StructLayout(LayoutKind.Sequential)]
        public struct llsm_chunk
        {
            public IntPtr conf;    // llsm_container*
            public IntPtr frames;  // llsm_container** (array)
        }

        // hmframe 構造体のミラー
        [StructLayout(LayoutKind.Sequential)]
        public struct llsm_hmframe
        {
            public IntPtr ampl;    // FP_TYPE* harmonic amplitude (linear)
            public IntPtr phse;    // FP_TYPE* harmonic phase (radian)
            public int nhar;       // number of harmonics
        }

        // nmframe 構造体のミラー
        [StructLayout(LayoutKind.Sequential)]
        public struct llsm_nmframe
        {
            public IntPtr eenv;    // llsm_hmframe** noise envelope
            public IntPtr edc;     // FP_TYPE* short-time mean
            public IntPtr psd;     // FP_TYPE* power spectral density (dB)
            public int npsd;       // size of psd
            public int nchannel;   // number of channels
        }
        
        // aoptions 構造体のミラー
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct llsm_aoptions
        {
            public float thop;
            public int maxnhar;
            public int maxnhar_e;
            public int npsd;
            public int nchannel;
            public IntPtr chanfreq;  // float*
            public float lip_radius;
            public int f0_refine;
            public int hm_method;
            public float rel_winsize;
        }

        // Frame/Model creation
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_frame(int nhar, int nchannel, int nhar_e, int npsd);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_hmframe(int nhar);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_nmframe(int nchannel, int nhar_e, int npsd);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_hmframe(IntPtr dst);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_nmframe(IntPtr dst);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_copy_hmframe(IntPtr src);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_copy_nmframe(IntPtr src);

        // Container creation
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_container(int nmember);

        // Create int value
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr llsm_create_int(int x);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void llsm_delete_int(IntPtr p);
        
        // デリータ関数ポインタのデリゲート型
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DeleteFunc(IntPtr ptr);
    }
}
