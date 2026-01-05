using System;
using System.IO;
using LlsmBindings;

namespace LlsmBindings.Samples.TestCoder
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("TestCoder: ENUNU features -> LLSM synth (coder経由)");

            // ソース音声 (conf を取るためのベース)
            string sourceWav = Path.Combine(Environment.CurrentDirectory, "temp.wav");
            if (!File.Exists(sourceWav))
            {
                // 上位ディレクトリも探す
                sourceWav = Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "temp.wav");
            }
            if (!File.Exists(sourceWav))
            {
                // SmokeTest と同階層かも
                sourceWav = Path.Combine(Environment.CurrentDirectory, "..", "SmokeTest", "temp.wav");
            }
            if (!File.Exists(sourceWav))
            {
                Console.WriteLine("temp.wav が見つかりません。SmokeTest を先に実行するか、temp.wav をカレントディレクトリにコピーしてください。");
                return;
            }
            Console.WriteLine($"Source wav: {sourceWav}");

            string featDir = Path.Combine(Environment.CurrentDirectory, "enunu_format");
            string f0Csv  = Path.Combine(featDir, "0_acoustic_f0.csv");
            string vuvCsv = Path.Combine(featDir, "0_acoustic_vuv.csv");
            string mgcCsv = Path.Combine(featDir, "0_acoustic_mgc.csv");
            string bapCsv = Path.Combine(featDir, "0_acoustic_bap.csv");

            if (!File.Exists(f0Csv) || !File.Exists(vuvCsv) || !File.Exists(mgcCsv) || !File.Exists(bapCsv))
            {
                Console.WriteLine("enunu_format/ に f0/vuv/mgc/bap の各 CSV が見つかりません。");
                return;
            }

            // ENUNU 特徴量を読み込み
            float[] f0 = ReadCsvVector(f0Csv);
            float[] vuv = ReadCsvVector(vuvCsv);
            float[][] mgc = ReadCsvMatrix(mgcCsv);
            float[][] bap = ReadCsvMatrix(bapCsv);

            int nfrm = Math.Min(
                Math.Min(f0.Length, vuv.Length),
                Math.Min(mgc.Length, bap.Length));

            if (nfrm == 0)
            {
                Console.WriteLine("no frames after length alignment");
                return;
            }

            if (f0.Length != nfrm || vuv.Length != nfrm || mgc.Length != nfrm || bap.Length != nfrm)
            {
                Console.WriteLine($"length mismatch, truncating to common length {nfrm}: f0={f0.Length}, vuv={vuv.Length}, mgc={mgc.Length}, bap={bap.Length}");
            }

            int orderSpec = Math.Min(64, mgc[0].Length);
            int orderBap  = Math.Min(5, bap[0].Length);

            Console.WriteLine($"nfrm={nfrm}, orderSpec={orderSpec}, orderBap={orderBap}");

            // ソース音声を解析して conf を取得
            var (srcX, srcFs) = Wav.ReadMono(sourceWav);
            Console.WriteLine($"Source: {srcX.Length} samples, {srcFs} Hz");

            using var aopt = Llsm.CreateAnalysisOptions();
            int nhop = 128;
            var srcF0 = Pyin.Analyze(srcX, srcFs, nhop);
            Console.WriteLine($"Source F0 frames: {srcF0.Length}");

            using var srcChunk = Llsm.Analyze(aopt, srcX, srcFs, srcF0, srcF0.Length);
            Llsm.ChunkToLayer1(srcChunk, 2048);

            var conf = Llsm.GetConf(srcChunk);
            int origNfrm = Llsm.GetNumFrames(srcChunk); // 元のフレーム数を保存

            // coder を作成（NFRM 変更前に）
            using var coder = Llsm.CreateCoder(conf, orderSpec, orderBap);
            Console.WriteLine("Coder created successfully");

            // conf に NFRM を設定（ENUNU フレーム数に上書き）
            SetConfNfrm(conf, nfrm);

            // 新しい chunk を作成（frames は null 初期化）
            using var newChunk = Llsm.CreateChunk(conf, 0);
            Console.WriteLine("New chunk created");

            // srcChunk の conf の NFRM を元に戻す（srcChunk 解放時に正しいフレーム数で解放されるように）
            SetConfNfrm(conf, origNfrm);

            // 各フレームを coder でデコードして詰める
            // ENUNU の mgc/bap を LLSM spec/bap に変換
            for (int i = 0; i < nfrm; i++)
            {
                float f0i = f0[i];
                float vuvi = vuv[i] > 0.5f ? 1f : 0f;
                if (vuvi <= 0f) f0i = 0f;

                // enc ベクトル: [VUV, F0, Rd, spec[orderSpec], bap[orderBap]]
                var enc = new float[3 + orderSpec + orderBap];
                enc[0] = vuvi;
                enc[1] = f0i;
                enc[2] = 0.5f; // Rd: 中間的な値

                // mgc -> spec 変換
                // ENUNU mgc[0] ≈ -16、LLSM spec[0] ≈ -9〜-15 → オフセット調整
                int mgcLen = Math.Min(orderSpec, mgc[i].Length);
                for (int j = 0; j < mgcLen; j++)
                {
                    if (j == 0)
                        enc[3 + j] = mgc[i][j] + 7f; // c0 オフセット
                    else
                        enc[3 + j] = mgc[i][j] * 1.5f; // 他の係数スケール
                }

                // bap: dB → 線形
                int bapLen = Math.Min(orderBap, bap[i].Length);
                for (int j = 0; j < bapLen; j++)
                {
                    float bapDb = bap[i][j];
                    float bapLin = MathF.Pow(10f, bapDb / 20f);
                    enc[3 + orderSpec + j] = Math.Clamp(bapLin, 0.001f, 1f);
                }

                // フレームをデコードして chunk に設定
                var framePtr = Llsm.DecodeFrameLayer1Ptr(coder, enc);
                Llsm.SetFrame(newChunk, i, framePtr);
            }
            Console.WriteLine("All frames decoded and set");

            // Layer0 に変換して位相伝播
            Llsm.ChunkToLayer0(newChunk);
            Llsm.ChunkPhasePropagate(newChunk, 1);

            // 合成
            int outFs = srcFs;
            using var sopt = Llsm.CreateSynthesisOptions(outFs);
            using var output = Llsm.Synthesize(sopt, newChunk);
            var y = Llsm.ReadOutput(output);

            string outPath = Path.Combine(Environment.CurrentDirectory, "enunu_synth.wav");
            Wav.WriteMono16(outPath, y, outFs);
            Console.WriteLine($"Wrote {outPath} ({y.Length} samples)");
        }

        /// <summary>
        /// conf の NFRM を書き換えるヘルパー
        /// </summary>
        static void SetConfNfrm(ContainerRef conf, int nfrm)
        {
            var p = NativeLLSM.llsm_container_get(conf.Ptr, NativeLLSM.LLSM_CONF_NFRM);
            if (p != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(p, nfrm);
            }
        }

        static float[] ReadCsvVector(string path)
        {
            var lines = File.ReadAllLines(path);
            var list = new System.Collections.Generic.List<float>();
            foreach (var l in lines)
            {
                var s = l.Trim();
                if (string.IsNullOrEmpty(s)) continue;
                var cols = s.Split(',');
                if (cols.Length == 0) continue;
                if (float.TryParse(cols[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                    list.Add(v);
            }
            return list.ToArray();
        }

        static float[][] ReadCsvMatrix(string path)
        {
            var lines = File.ReadAllLines(path);
            var rows = new System.Collections.Generic.List<float[]>();
            foreach (var l in lines)
            {
                var s = l.Trim();
                if (string.IsNullOrEmpty(s)) continue;
                var cols = s.Split(',');
                var row = new float[cols.Length];
                for (int i = 0; i < cols.Length; i++)
                {
                    var c = cols[i].Trim();
                    if (string.IsNullOrEmpty(c)) { row[i] = 0f; continue; }
                    if (!float.TryParse(c, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out row[i]))
                        row[i] = 0f;
                }
                rows.Add(row);
            }
            return rows.ToArray();
        }
    }
}
