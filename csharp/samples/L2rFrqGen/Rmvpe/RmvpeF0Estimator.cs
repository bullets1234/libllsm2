using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace L2rFrqGen.Rmvpe
{
    /// <summary>
    /// RMVPE (Robust Model for Vocal Pitch Estimation) による F0 推定。
    ///
    /// 想定する ONNX は tools/export_rmvpe_onnx.py が出力する「音声入力版」:
    ///   input  : float32 (1, N)      16kHz モノラル波形
    ///   output : float32 (1, T, 360) 各フレームの cent ビン salience
    /// メル計算をグラフ内へ畳み込んであるため、C# 側は 16kHz 化するだけでよい。
    /// </summary>
    public sealed class RmvpeF0Estimator : IDisposable
    {
        public const int AnalysisRate = 16000;
        public const int HopSamples = 160;   // 10ms
        public const int WindowSamples = 1024;

        private const int Bins = 360;
        /// <summary>U-Net のダウンサンプリング段数の都合でフレーム数は 32 の倍数が必要。</summary>
        private const int FrameAlign = 32;
        /// <summary>1 回の推論に載せる最大フレーム数（= 60 秒）。長尺 WAV のメモリ暴発を防ぐ。</summary>
        private const int MaxFramesPerRun = 6000;
        /// <summary>分割時、境界の文脈欠落を吸収するために前後へ伸ばすフレーム数。</summary>
        private const int GuardFrames = 64;

        /// <summary>ビン i の cent 値。f0 = 10 * 2^(cent/1200)（10Hz 基準）。</summary>
        private static readonly float[] CentsMapping = CreateCentsMapping();

        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _outputName;
        private readonly bool _serializeRuns;
        private readonly object _runLock = new();

        public RmvpeF0Estimator(string modelPath, bool useGpu, int intraOpThreads)
        {
            _serializeRuns = useGpu;
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Math.Max(1, intraOpThreads),
                // ORT は EP 割り当てなどの情報を警告として stderr に出す。音源フォルダを
                // 一括処理すると進捗表示が埋もれるので、致命的なものだけ通す。
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            };
            if (useGpu)
            {
                // DirectML はメモリパターン最適化と併用できない
                options.EnableMemoryPattern = false;
                options.AppendExecutionProvider_DML(0);
            }

            _session = new InferenceSession(modelPath, options);

            var input = _session.InputMetadata.First();
            _inputName = input.Key;
            if (input.Value.Dimensions.Length != 2)
            {
                throw new InvalidOperationException(
                    $"入力 '{_inputName}' の rank が {input.Value.Dimensions.Length} です。" +
                    "波形入力版 (1, N) の ONNX が必要です。tools/export_rmvpe_onnx.py で書き出してください。");
            }

            var output = _session.OutputMetadata.First();
            _outputName = output.Key;
            int lastDim = output.Value.Dimensions[^1];
            if (lastDim > 0 && lastDim != Bins)
                throw new InvalidOperationException($"出力の最終次元が {lastDim} です（{Bins} を期待）。");
        }

        /// <summary>モデル出力からフレーム数を決める（center=True の torch.stft 相当）。</summary>
        public static int FrameCountFor(int sampleCount16k) => sampleCount16k / HopSamples + 1;

        /// <summary>
        /// 16kHz モノラル波形から F0 と有声確信度を推定する。
        /// </summary>
        /// <param name="audio16k">16kHz モノラル波形</param>
        /// <param name="threshold">salience 最大値がこれ以下のフレームを無声とする（RVC 既定 0.03）</param>
        public (float[] F0, float[] Confidence) Estimate(float[] audio16k, float threshold)
        {
            int total = FrameCountFor(audio16k.Length);
            var f0 = new float[total];
            var conf = new float[total];

            int pos = 0;
            while (pos < total)
            {
                int take = Math.Min(MaxFramesPerRun, total - pos);
                int from = Math.Max(0, pos - GuardFrames);
                int to = Math.Min(total, pos + take + GuardFrames);
                int need = to - from;

                // フレーム数を 32 の倍数へ切り上げ、対応するサンプル長でゼロ詰めする
                int padded = ((need + FrameAlign - 1) / FrameAlign) * FrameAlign;
                int padLen = (padded - 1) * HopSamples;

                var buf = new float[padLen];
                int srcStart = from * HopSamples;
                int copy = Math.Min(padLen, Math.Max(0, audio16k.Length - srcStart));
                if (copy > 0) Array.Copy(audio16k, srcStart, buf, 0, copy);

                var salience = Run(buf, padded);

                int offset = pos - from;
                Decode(salience, offset, take, threshold, f0, conf, pos);

                pos += take;
            }

            return (f0, conf);
        }

        private float[] Run(float[] audio, int frames)
        {
            var tensor = new DenseTensor<float>(audio, new[] { 1, audio.Length });
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;

            // DirectML EP は同一セッションへの同時 Run に対応しておらず、
            // 並列に呼ぶと AccessViolation で即死する。GPU 時のみ直列化する。
            // CPU EP はスレッドセーフなのでロックを取らない。
            if (_serializeRuns)
            {
                lock (_runLock)
                {
                    results = _session.Run(
                        new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) },
                        new[] { _outputName });
                }
            }
            else
            {
                results = _session.Run(
                    new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) },
                    new[] { _outputName });
            }

            using (results)
            {
                var t = results.First().AsTensor<float>();
                var dense = t as DenseTensor<float> ?? t.ToDenseTensor();
                if (dense.Length < (long)frames * Bins)
                    throw new InvalidOperationException($"モデル出力が短すぎます: {dense.Length} < {(long)frames * Bins}");
                return dense.Buffer.ToArray();
            }
        }

        /// <summary>
        /// salience → cent → Hz。ピーク近傍 ±4 ビンの重み付き平均で分解能を上げる
        /// （RVC / CREPE の local weighted average と同じ手順）。
        /// </summary>
        private static void Decode(
            float[] salience, int frameOffset, int count, float threshold,
            float[] f0Out, float[] confOut, int outOffset)
        {
            for (int i = 0; i < count; i++)
            {
                int b = (frameOffset + i) * Bins;

                int argmax = 0;
                float max = salience[b];
                for (int k = 1; k < Bins; k++)
                {
                    float v = salience[b + k];
                    if (v > max) { max = v; argmax = k; }
                }

                confOut[outOffset + i] = max;
                if (max <= threshold) { f0Out[outOffset + i] = 0f; continue; }

                int lo = Math.Max(0, argmax - 4);
                int hi = Math.Min(Bins - 1, argmax + 4);
                double num = 0.0, den = 0.0;
                for (int k = lo; k <= hi; k++)
                {
                    double w = salience[b + k];
                    num += w * CentsMapping[k];
                    den += w;
                }
                if (den <= 0.0) { f0Out[outOffset + i] = 0f; continue; }

                double cents = num / den;
                f0Out[outOffset + i] = (float)(10.0 * Math.Pow(2.0, cents / 1200.0));
            }
        }

        private static float[] CreateCentsMapping()
        {
            var m = new float[Bins];
            for (int i = 0; i < Bins; i++) m[i] = 20f * i + 1997.3794084376191f;
            return m;
        }

        public void Dispose() => _session.Dispose();
    }
}
