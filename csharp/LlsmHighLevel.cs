using System;
using System.Runtime.InteropServices;

namespace LlsmBindings
{
    /// <summary>
    /// LLSM ライブラリの高レベル C# ラッパー。
    /// 分析 (Analyze)・合成 (Synthesize)・フレーム単位のパラメータ編集を、
    /// SafeHandle ベースのリソース管理で安全に扱える API として提供します。
    /// </summary>
    public static class Llsm
    {
        // cache native lib and function pointers for attach destructors
        private static readonly IntPtr _hLib;
        private static readonly IntPtr _pDeleteFp;
        private static readonly IntPtr _pDeleteFpArray;
        private static readonly IntPtr _pCopyFpArray;

        static Llsm()
        {
            // Ensure the native DLL is loadable via default search (same dir as exe)
            _hLib = NativeHelpers.LoadLibrary("libllsm2.dll");
            _pDeleteFp = NativeHelpers.GetExport(_hLib, nameof(NativeLLSM.llsm_delete_fp));
            _pDeleteFpArray = NativeHelpers.GetExport(_hLib, nameof(NativeLLSM.llsm_delete_fparray));
            _pCopyFpArray = NativeHelpers.GetExport(_hLib, nameof(NativeLLSM.llsm_copy_fparray));
        }

        /// <summary>
        /// libllsm2 の特徴量コーダ (llsm_coder) を生成します。
        /// conf は既存チャンクなどから取得した設定コンテナを渡します。
        /// </summary>
        public static CoderHandle CreateCoder(ContainerRef conf, int orderSpec, int orderBap)
        {
            var p = NativeLLSM.llsm_create_coder(conf.Ptr, orderSpec, orderBap);
            if (p == IntPtr.Zero) throw new Exception("llsm_create_coder failed");
            return CoderHandle.FromExisting(p);
        }

        /// <summary>
        /// 解析用オプション (aoptions) を生成します。
        /// 返されたハンドルは <see cref="AOptionsHandle"/> が所有し、<c>Dispose()</c> 時に解放されます。
        /// </summary>
        /// <returns>解析オプションの SafeHandle</returns>
        /// <exception cref="Exception">ネイティブ側の生成に失敗した場合</exception>
        public static AOptionsHandle CreateAnalysisOptions()
        {
            var p = NativeLLSM.llsm_create_aoptions();
            if (p == IntPtr.Zero) throw new Exception("llsm_create_aoptions failed");
            return AOptionsHandle.FromExisting(p);
        }

        /// <summary>
        /// 合成用オプション (soptions) を生成します。
        /// </summary>
        /// <param name="fs">出力サンプリング周波数 [Hz]</param>
        /// <returns>合成オプションの SafeHandle</returns>
        /// <exception cref="Exception">ネイティブ側の生成に失敗した場合</exception>
        public static SOptionsHandle CreateSynthesisOptions(float fs)
        {
            var p = NativeLLSM.llsm_create_soptions(fs);
            if (p == IntPtr.Zero) throw new Exception("llsm_create_soptions failed");
            return SOptionsHandle.FromExisting(p);
        }

        /// <summary>
        /// 解析オプションから固定パラメータ群 (conf) を作成します。
        /// conf は <c>chunk</c> 等の内部が所有し、ここで返す参照は借用参照です（呼び出し側で解放しません）。
        /// </summary>
        /// <param name="aopts">解析オプション</param>
        /// <param name="fnyq">ナイキスト周波数 [Hz]</param>
        /// <returns>コンテナ参照（借用。<c>llsm_delete_container</c>は不要）</returns>
        /// <exception cref="Exception">作成に失敗した場合</exception>
        public static ContainerRef AOptionsToConf(AOptionsHandle aopts, float fnyq)
        {
            var p = NativeLLSM.llsm_aoptions_toconf(aopts.DangerousGetHandle(), fnyq);
            if (p == IntPtr.Zero) throw new Exception("llsm_aoptions_toconf failed");
            return new ContainerRef(p);
        }

        /// <summary>
        /// コンテナをディープコピーして新しいコンテナを生成します。
        /// 返されるコンテナは所有権を持つため、Ptr を IntPtr として保持し、
        /// 使用後は NativeLLSM.llsm_delete_container で解放するか、
        /// 別の構造体（chunk など）に所有権を移転してください。
        /// </summary>
        public static ContainerRef CopyContainer(ContainerRef src)
        {
            var p = NativeLLSM.llsm_copy_container(src.Ptr);
            if (p == IntPtr.Zero) throw new Exception("llsm_copy_container failed");
            return new ContainerRef(p);
        }

        /// <summary>
        /// frame（Container）をコピーします。
        /// 戻り値は所有権付きの IntPtr なので、chunk に設定する際に使います。
        /// </summary>
        public static IntPtr CopyFrame(ContainerRef frame)
        {
            var p = NativeLLSM.llsm_copy_container(frame.Ptr);
            if (p == IntPtr.Zero) throw new Exception("llsm_copy_container failed for frame");
            return p;
        }

        /// <summary>
        /// chunk 全体をディープコピーします。
        /// </summary>
        public static ChunkHandle CopyChunk(ChunkHandle src)
        {
            var p = NativeLLSM.llsm_copy_chunk(src.DangerousGetHandle());
            if (p == IntPtr.Zero) throw new Exception("llsm_copy_chunk failed");
            return ChunkHandle.FromExisting(p);
        }

        /// <summary>
        /// 波形と F0 列から LLSM 解析を実行し、<c>chunk</c> を生成します。
        /// </summary>
        /// <param name="aopts">解析オプション</param>
        /// <param name="x">入力波形（モノラル、正規化 float）</param>
        /// <param name="fs">サンプリング周波数 [Hz]</param>
        /// <param name="f0">各フレームの基本周波数 [Hz]（無声区間は 0）</param>
        /// <param name="nfrm">フレーム数（<paramref name="f0"/> の長さと一致させる）</param>
        /// <returns>解析結果チャンクの SafeHandle</returns>
        /// <exception cref="Exception">解析に失敗した場合</exception>
        public static ChunkHandle Analyze(AOptionsHandle aopts, float[] x, float fs, float[] f0, int nfrm)
        {
            var p = NativeLLSM.llsm_analyze(aopts.DangerousGetHandle(), x, x.Length, fs, f0, nfrm, IntPtr.Zero);
            if (p == IntPtr.Zero) throw new Exception("llsm_analyze failed");
            return ChunkHandle.FromExisting(p);
        }

        /// <summary>
        /// チャンクから波形を合成します。
        /// </summary>
        /// <param name="sopts">合成オプション</param>
        /// <param name="chunk">解析済みチャンク</param>
        /// <returns>合成出力の SafeHandle（波形バッファは内部保持）</returns>
        /// <exception cref="Exception">合成に失敗した場合</exception>
        public static OutputHandle Synthesize(SOptionsHandle sopts, ChunkHandle chunk)
        {
            var p = NativeLLSM.llsm_synthesize(sopts.DangerousGetHandle(), chunk.DangerousGetHandle());
            if (p == IntPtr.Zero) throw new Exception("llsm_synthesize failed");
            return OutputHandle.FromExisting(p);
        }

        /// <summary>
        /// 合成出力から波形配列を取得します。
        /// このメソッドは内部バッファをコピーして <see cref="float[]"/> を返します。
        /// </summary>
        /// <param name="output">合成出力ハンドル</param>
        /// <returns>波形（モノラル）</returns>
        public static float[] ReadOutput(OutputHandle output)
        {
            var o = Marshal.PtrToStructure<NativeLLSM.llsm_output>(output.DangerousGetHandle());
            var y = new float[o.ny];
            if (o.y != IntPtr.Zero)
                Marshal.Copy(o.y, y, 0, o.ny);
            return y;
        }

        /// <summary>
        /// 合成出力から分解された波形（正弦波成分・ノイズ成分）を取得します。
        /// llsm_synthesize は内部で y = y_sin + y_noise を計算しており、
        /// 個別の成分を取り出すことで子音ブレンド等の精密な制御が可能になります。
        /// </summary>
        /// <returns>(combined, sinusoidal, noise) の3つの波形配列</returns>
        public static (float[] y, float[] ySin, float[] yNoise) ReadOutputDecomposed(OutputHandle output)
        {
            var o = Marshal.PtrToStructure<NativeLLSM.llsm_output>(output.DangerousGetHandle());
            var y = new float[o.ny];
            var ySin = new float[o.ny];
            var yNoise = new float[o.ny];
            if (o.y != IntPtr.Zero)
                Marshal.Copy(o.y, y, 0, o.ny);
            if (o.y_sin != IntPtr.Zero)
                Marshal.Copy(o.y_sin, ySin, 0, o.ny);
            if (o.y_noise != IntPtr.Zero)
                Marshal.Copy(o.y_noise, yNoise, 0, o.ny);
            return (y, ySin, yNoise);
        }

        // chunk helpers
        /// <summary>
        /// チャンクが保持する設定コンテナ (conf) を取得します（借用参照）。
        /// </summary>
        public static ContainerRef GetConf(ChunkHandle chunk)
        {
            var ck = Marshal.PtrToStructure<NativeLLSM.llsm_chunk>(chunk.DangerousGetHandle());
            return new ContainerRef(ck.conf);
        }

        /// <summary>
        /// チャンク内のフレーム数を返します。
        /// </summary>
        public static int GetNumFrames(ChunkHandle chunk)
        {
            var conf = GetConf(chunk);
            var nfrmPtr = NativeLLSM.llsm_container_get(conf.Ptr, NativeLLSM.LLSM_CONF_NFRM);
            return Marshal.ReadInt32(nfrmPtr);
        }

        /// <summary>
        /// 指定インデックスのフレームコンテナへの参照を取得します（借用参照）。
        /// </summary>
        public static ContainerRef GetFrame(ChunkHandle chunk, int index)
        {
            var ck = Marshal.PtrToStructure<NativeLLSM.llsm_chunk>(chunk.DangerousGetHandle());
            if (ck.frames == IntPtr.Zero) throw new Exception("frames is null");
            var framePtr = Marshal.ReadIntPtr(ck.frames, index * IntPtr.Size);
            return new ContainerRef(framePtr);
        }

        /// <summary>
        /// 空の chunk を conf から生成します（frames 配列は NFRM 分確保されるが、各要素は null）。
        /// </summary>
        public static ChunkHandle CreateChunk(ContainerRef conf, int initFrames = 0)
        {
            var p = NativeLLSM.llsm_create_chunk(conf.Ptr, initFrames);
            if (p == IntPtr.Zero) throw new Exception("llsm_create_chunk failed");
            return ChunkHandle.FromExisting(p);
        }

        /// <summary>
        /// chunk の frames[index] に frame コンテナポインタを設定します。
        /// 既存の frames[index] を上書きします（ネイティブ側での解放はしません）。
        /// frame は llsm_coder_decode_layer1 等で取得した所有権付きポインタを渡す想定。
        /// </summary>
        public static void SetFrame(ChunkHandle chunk, int index, IntPtr framePtr)
        {
            var ck = Marshal.PtrToStructure<NativeLLSM.llsm_chunk>(chunk.DangerousGetHandle());
            if (ck.frames == IntPtr.Zero) throw new Exception("frames is null");
            Marshal.WriteIntPtr(ck.frames, index * IntPtr.Size, framePtr);
        }

        // parameter editors (attach new objects managed by native container)
        /// <summary>
        /// フレームの F0 [Hz] を設定します（内部で単一 float オブジェクトを生成し、フレームにアタッチ）。
        /// </summary>
        public static void SetFrameF0(ContainerRef frame, float f0)
        {
            var p = NativeLLSM.llsm_create_fp(f0);
            NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_F0, p, _pDeleteFp, IntPtr.Zero);
        }

        /// <summary>
        /// フレームの Rd を設定します（内部で単一 float オブジェクトを生成し、フレームにアタッチ）。
        /// </summary>
        public static void SetFrameRd(ContainerRef frame, float rd)
        {
            var p = NativeLLSM.llsm_create_fp(rd);
            NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_RD, p, _pDeleteFp, IntPtr.Zero);
        }

        /// <summary>
        /// フレームの声道スペクトル（dB マグニチュード配列）を設定します。
        /// 内部で float 配列オブジェクトを生成し、ディープコピー用関数ポインタを渡してアタッチします。
        /// </summary>
        public static void SetFrameVtMagn(ContainerRef frame, float[] dbMagn)
        {
            var arr = NativeLLSM.llsm_create_fparray(dbMagn.Length);
            Marshal.Copy(dbMagn, 0, arr, dbMagn.Length);
            NativeLLSM.llsm_container_attach_(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN, arr, _pDeleteFpArray, _pCopyFpArray);
        }

        /// <summary>
        /// conf からホップ長 [秒] を取得します。
        /// </summary>
        public static float GetThopSeconds(ContainerRef conf)
        {
            var p = NativeLLSM.llsm_container_get(conf.Ptr, NativeLLSM.LLSM_CONF_THOP);
            return Marshal.PtrToStructure<float>(p);
        }

        /// <summary>
        /// ホップ長(秒)を新しい値に差し替えます。タイムストレッチ用途（値を大きくすると遅く/長く、値を小さくすると速く/短く）
        /// </summary>
        /// <param name="conf">設定コンテナ</param>
        /// <param name="newThop">新しいホップ長(秒)</param>
        public static void SetConfThopSeconds(ContainerRef conf, float newThop)
        {
            var p = NativeLLSM.llsm_create_fp(newThop);
            // ホップ長は LLSM_CONF_THOP (index=1)
            NativeLLSM.llsm_container_attach_(conf.Ptr, NativeLLSM.LLSM_CONF_THOP, p, _pDeleteFp, IntPtr.Zero);
        }

        /// <summary>
        /// チャンク全体を Layer1 表現へ変換します（スペクトル解像度は <paramref name="nfft"/>）。
        /// </summary>
        public static void ChunkToLayer1(ChunkHandle chunk, int nfft)
            => NativeLLSM.llsm_chunk_tolayer1(chunk.DangerousGetHandle(), nfft);

        /// <summary>
        /// チャンク全体を Layer0 表現へ変換します。
        /// </summary>
        public static void ChunkToLayer0(ChunkHandle chunk)
            => NativeLLSM.llsm_chunk_tolayer0(chunk.DangerousGetHandle());

        /// <summary>
        /// チャンクの位相を前方向（+1）/後方向（-1）に伝播させます。
        /// </summary>
        public static void ChunkPhasePropagate(ChunkHandle chunk, int sign)
            => NativeLLSM.llsm_chunk_phasepropagate(chunk.DangerousGetHandle(), sign);

        /// <summary>
        /// conf から int 値を取得します。
        /// </summary>
        public static int GetConfInt(ContainerRef conf, int index)
        {
            var p = NativeLLSM.llsm_container_get(conf.Ptr, index);
            return Marshal.ReadInt32(p);
        }

        /// <summary>
        /// conf から float 値を取得します。
        /// </summary>
        public static float GetConfFloat(ContainerRef conf, int index)
        {
            var p = NativeLLSM.llsm_container_get(conf.Ptr, index);
            return Marshal.PtrToStructure<float>(p);
        }

        /// <summary>
        /// フレームの VTMAGN (dB) を取得します。長さは conf の NSPEC に従います。
        /// </summary>
        public static float[] GetFrameVtMagn(ContainerRef frame, int nspec)
        {
            var p = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_VTMAGN);
            var arr = new float[nspec];
            if (p != IntPtr.Zero)
                Marshal.Copy(p, arr, 0, nspec);
            return arr;
        }

        /// <summary>
        /// スペクトルのピッチ追従的な周波数スケーリングを適用します（Layer1 で VTMAGN をリサンプリング）。
        /// shiftMul>1 で高く、<1 で低くなります。
        /// </summary>
        public static void ApplySpectralShift(ChunkHandle chunk, float shiftMul, int nfft = 2048)
        {
            if (Math.Abs(shiftMul - 1f) < 1e-6f) return;
            ChunkToLayer1(chunk, nfft);
            var conf = GetConf(chunk);
            int nspec = GetConfInt(conf, NativeLLSM.LLSM_CONF_NSPEC);
            if (nspec <= 0) return;
            // 周波数軸は 0..fnyq の線形等間隔と仮定
            // リサンプリング: out[k] = in[k / shiftMul]（線形補間、範囲外は最小値で埋め）
            int nfrm = GetNumFrames(chunk);
            for (int i = 0; i < nfrm; i++)
            {
                var frame = GetFrame(chunk, i);
                var src = GetFrameVtMagn(frame, nspec);
                if (src.Length != nspec) continue;
                float minVal = float.MaxValue;
                for (int t = 0; t < nspec; t++) if (src[t] < minVal) minVal = src[t];
                var dst = new float[nspec];
                for (int k = 0; k < nspec; k++)
                {
                    float srcPos = k / shiftMul;
                    if (srcPos <= 0) { dst[k] = src[0]; continue; }
                    if (srcPos >= nspec - 1) { dst[k] = minVal; continue; }
                    int i0 = (int)MathF.Floor(srcPos);
                    int i1 = i0 + 1;
                    float w = srcPos - i0;
                    float a = src[i0];
                    float b = src[i1];
                    dst[k] = a + (b - a) * w;
                }
                SetFrameVtMagn(frame, dst);
            }
            // 位相は後段で伝播させて滑らかに
            ChunkPhasePropagate(chunk, +1);
        }

        /// <summary>
        /// coder + Layer1 デコードを用いて、特徴量ベクトルから新しいフレームコンテナを生成します。
        /// enc のレイアウトは libllsm2 の coder と同じ (VUV, F0, Rd, spec[orderSpec], bap[orderBap]) を想定します。
        /// </summary>
        public static ContainerRef DecodeFrameLayer1(CoderHandle coder, float[] enc)
        {
            if (coder.IsInvalid) throw new ObjectDisposedException(nameof(CoderHandle));
            if (enc is null || enc.Length == 0) throw new ArgumentException("enc is empty", nameof(enc));
            var p = NativeLLSM.llsm_coder_decode_layer1(coder.DangerousGetHandle(), enc);
            if (p == IntPtr.Zero) throw new Exception("llsm_coder_decode_layer1 failed");
            return new ContainerRef(p);
        }

        /// <summary>
        /// coder + Layer1 デコードを用いて、特徴量ベクトルから新しいフレームコンテナを生成します。
        /// 戻り値は SetFrame に渡すための生ポインタです（所有権は呼び出し側に移動）。
        /// </summary>
        public static IntPtr DecodeFrameLayer1Ptr(CoderHandle coder, float[] enc)
        {
            if (coder.IsInvalid) throw new ObjectDisposedException(nameof(CoderHandle));
            if (enc is null || enc.Length == 0) throw new ArgumentException("enc is empty", nameof(enc));
            var p = NativeLLSM.llsm_coder_decode_layer1(coder.DangerousGetHandle(), enc);
            if (p == IntPtr.Zero) throw new Exception("llsm_coder_decode_layer1 failed");
            return p;
        }

        /// <summary>
        /// coder を用いてフレームコンテナを特徴量ベクトルにエンコードします。
        /// 返される配列の長さは coder 作成時の orderSpec/orderBap に依存します。
        /// </summary>
        public static float[] EncodeFrame(CoderHandle coder, ContainerRef frame, int orderSpec, int orderBap)
        {
            if (coder.IsInvalid) throw new ObjectDisposedException(nameof(CoderHandle));
            if (frame.Ptr == IntPtr.Zero) throw new ArgumentException("frame is null", nameof(frame));
            var ptr = NativeLLSM.llsm_coder_encode(coder.DangerousGetHandle(), frame.Ptr);
            if (ptr == IntPtr.Zero) throw new Exception("llsm_coder_encode failed");

            // 長さは coder.c 実装より: order_spec + order_bap + 3
            int dim = orderSpec + orderBap + 3;
            var enc = new float[dim];
            Marshal.Copy(ptr, enc, 0, dim);
            // Note: ネイティブ側で calloc された配列は解放されない（小さなリーク）
            // 本格実装では libllsm2 側に free 関数を追加するか、別途対応が必要
            return enc;
        }

        /// <summary>
        /// フレームの F0 [Hz] を取得（無声なら 0）。
        /// </summary>
        public static float GetFrameF0(ContainerRef frame)
        {
            var p = NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_F0);
            if (p == IntPtr.Zero) return 0f;
            return Marshal.PtrToStructure<float>(p);
        }

        /// <summary>
        /// フレームからハーモニクスモデル（HM）を取得
        /// </summary>
        public static IntPtr GetFrameHM(ContainerRef frame)
        {
            return NativeLLSM.llsm_container_get(frame.Ptr, NativeLLSM.LLSM_FRAME_HM);
        }

        /// <summary>
        /// ハーモニクスモデルからハーモニクス数を取得
        /// </summary>
        public static int GetHMNHar(IntPtr hmPtr)
        {
            if (hmPtr == IntPtr.Zero) return 0;
            // llsm_hmframe構造体: {FP_TYPE* ampl, FP_TYPE* phse, int nhar}
            // nharは2つのポインタの後（IntPtr.Size * 2バイト後）
            return Marshal.ReadInt32(hmPtr + IntPtr.Size * 2);
        }

        /// <summary>
        /// ハーモニクスモデルから振幅配列を取得
        /// </summary>
        public static float[] GetHMAmpl(IntPtr hmPtr, int nhar)
        {
            if (hmPtr == IntPtr.Zero || nhar <= 0) return new float[0];
            // llsm_hmframe構造体: {FP_TYPE* ampl, FP_TYPE* phse, int nhar}
            // amplは最初のメンバー（オフセット0）
            IntPtr amplPtr = Marshal.ReadIntPtr(hmPtr);
            if (amplPtr == IntPtr.Zero) return new float[0];
            
            float[] ampl = new float[nhar];
            Marshal.Copy(amplPtr, ampl, 0, nhar);
            return ampl;
        }

        /// <summary>
        /// 無声フレームで VTMAGN を一律減衰（dB）させます（残響的な有声痕跡の抑制）。
        /// </summary>
        public static void AttenuateUnvoiced(ChunkHandle chunk, float uvDb = -9f)
        {
            var conf = GetConf(chunk);
            int nspec = GetConfInt(conf, NativeLLSM.LLSM_CONF_NSPEC);
            if (nspec <= 0) return;
            int nfrm = GetNumFrames(chunk);
            for (int i = 0; i < nfrm; i++)
            {
                var frame = GetFrame(chunk, i);
                if (GetFrameF0(frame) > 0) continue;
                var mag = GetFrameVtMagn(frame, nspec);
                for (int k = 0; k < mag.Length; k++) mag[k] += uvDb; // dB 加算で減衰
                SetFrameVtMagn(frame, mag);
            }
        }

        /// <summary>
        /// 対数周波数スケールでスペクトルをリサンプリング（Envelope を移動平均で平滑化してから再配置）。
        /// </summary>
        public static void ApplySpectralShiftLog(ChunkHandle chunk, float shiftMul, int nfft = 2048, int smoothWin = 7)
        {
            if (Math.Abs(shiftMul - 1f) < 1e-6f) return;
            ChunkToLayer1(chunk, nfft);
            var conf = GetConf(chunk);
            int nspec = GetConfInt(conf, NativeLLSM.LLSM_CONF_NSPEC);
            if (nspec <= 0) return;
            int nfrm = GetNumFrames(chunk);
            float[] Smooth(float[] v)
            {
                if (smoothWin <= 1) return v;
                var r = new float[v.Length];
                int hw = smoothWin / 2;
                for (int i = 0; i < v.Length; i++)
                {
                    int a = Math.Max(0, i - hw);
                    int b = Math.Min(v.Length - 1, i + hw);
                    float s = 0; int cnt = 0;
                    for (int j = a; j <= b; j++) { s += v[j]; cnt++; }
                    r[i] = s / cnt;
                }
                return r;
            }
            for (int fi = 0; fi < nfrm; fi++)
            {
                var frame = GetFrame(chunk, fi);
                var src = GetFrameVtMagn(frame, nspec);
                if (src.Length != nspec) continue;
                var env = Smooth(src);
                var dst = new float[nspec];
                // log(1+k) 軸を使用（k=0 を定義可能に）。out[k] <- env[srcIndex]
                for (int k = 0; k < nspec; k++)
                {
                    float logk = MathF.Log(1f + k);
                    float srcLog = logk / shiftMul;
                    float srcPos = MathF.Exp(srcLog) - 1f;
                    if (srcPos <= 0) { dst[k] = env[0]; continue; }
                    if (srcPos >= nspec - 1) { dst[k] = env[nspec - 1]; continue; }
                    int i0 = (int)MathF.Floor(srcPos);
                    int i1 = i0 + 1;
                    float w = srcPos - i0;
                    dst[k] = env[i0] + (env[i1] - env[i0]) * w;
                }
                SetFrameVtMagn(frame, dst);
            }
            ChunkPhasePropagate(chunk, +1);
        }
    }

    /// <summary>
    /// llsm_coder* 用 SafeHandle。
    /// </summary>
    public sealed class CoderHandle : SafeHandle
    {
        private CoderHandle() : base(IntPtr.Zero, true) { }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            NativeLLSM.llsm_delete_coder(handle);
            return true;
        }

        internal static CoderHandle FromExisting(IntPtr ptr)
        {
            var h = new CoderHandle();
            h.SetHandle(ptr);
            return h;
        }

        internal new IntPtr DangerousGetHandle() => handle;
    }

    /// <summary>
    /// PYIN による F0 推定の高レベルラッパー。
    /// 入力波形からフレーム系列の F0 [Hz] を返します（無声は 0）。
    /// </summary>
    public static class Pyin
    {
        /// <summary>
        /// PYIN で F0 を推定します。
        /// </summary>
        /// <param name="x">入力波形（モノラル、正規化 float）</param>
        /// <param name="fs">サンプリング周波数 [Hz]</param>
        /// <param name="nhop">ホップ長 [サンプル]（例: 128）</param>
        /// <param name="fmin">最小 F0 [Hz]</param>
        /// <param name="fmax">最大 F0 [Hz]</param>
        /// <returns>F0 列（無声は 0、長さはフレーム数）</returns>
        public static float[] Analyze(float[] x, float fs, int nhop = 128, float fmin = 50f, float fmax = 500f)
        {
            var cfg = NativePyin.pyin_init(nhop);
            cfg.fmin = fmin;
            cfg.fmax = fmax;
            cfg.trange = NativePyin.pyin_trange(cfg.nq, cfg.fmin, cfg.fmax);
            cfg.nf = (int)MathF.Ceiling(fs * 0.025f);
            int nfrm = 0;
            IntPtr f0Ptr = IntPtr.Zero;
            try
            {
                f0Ptr = NativePyin.pyin_analyze(cfg, x, x.Length, fs, ref nfrm);
                if (f0Ptr == IntPtr.Zero || nfrm <= 0)
                    throw new Exception("pyin_analyze failed");
                var f0 = new float[nfrm];
                Marshal.Copy(f0Ptr, f0, 0, nfrm);
                return f0;
            }
            finally
            {
                if (f0Ptr != IntPtr.Zero) NativePyin.pyin_free(f0Ptr);
            }
        }
    }
}
