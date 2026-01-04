using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace UtauEngine
{
    /// <summary>
    /// Neural Vocoder (HiFi-GAN) による音質向上
    /// </summary>
    public class NeuralVocoder : IDisposable
    {
        private InferenceSession? _session;
        private readonly string _modelPath;
        
        public NeuralVocoder(string modelPath)
        {
            _modelPath = modelPath;
        }
        
        /// <summary>
        /// モデルを読み込み
        /// </summary>
        public void LoadModel()
        {
            // Debug logs disabled
            // Console.WriteLine($"[NeuralVocoder] DEBUG: _modelPath = {_modelPath}");
            // Console.WriteLine($"[NeuralVocoder] DEBUG: File.Exists = {File.Exists(_modelPath)}");
            
            if (!File.Exists(_modelPath))
            {
                throw new FileNotFoundException($"HiFi-GAN model not found: {_modelPath}");
            }
            
            var options = new SessionOptions
            {
                // CPUで実行（GPUが使えるならExecutionProviderを追加）
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };
            
            _session = new InferenceSession(_modelPath, options);
            Console.WriteLine($"[NeuralVocoder] Model loaded: {_modelPath}");
        }
        
        /// <summary>
        /// WAV音声をMel-Spectrogramに変換
        /// </summary>
        public float[,] WavToMel(float[] wav, int sampleRate = 44100)
        {
            // HiFi-GAN標準パラメータ
            int nFft = 1024;
            int hopLength = 256;  // 44100Hz / 256 ≈ 172fps
            int winLength = 1024;
            int nMels = 80;
            
            // STFTを計算（簡易実装、本番では高品質なSTFTライブラリ使用推奨）
            var stft = ComputeSTFT(wav, nFft, hopLength, winLength);
            
            // パワースペクトログラム
            var powerSpec = ComputePowerSpectrum(stft);
            
            // Mel filterbank適用
            var melSpec = ApplyMelFilterbank(powerSpec, sampleRate, nFft, nMels);
            
            // 対数変換
            for (int i = 0; i < melSpec.GetLength(0); i++)
            {
                for (int j = 0; j < melSpec.GetLength(1); j++)
                {
                    melSpec[i, j] = MathF.Log(MathF.Max(1e-5f, melSpec[i, j]));
                }
            }
            
            return melSpec;
        }
        
        /// <summary>
        /// Mel-SpectrogramからWAVを生成（HiFi-GAN推論）
        /// </summary>
        public float[] MelToWav(float[,] melSpec)
        {
            if (_session == null)
            {
                throw new InvalidOperationException("Model not loaded. Call LoadModel() first.");
            }
            
            // Mel-Spectrogramをテンソルに変換 [batch, mels, time]
            int nMels = melSpec.GetLength(0);
            int nFrames = melSpec.GetLength(1);
            
            var inputTensor = new DenseTensor<float>(new[] { 1, nMels, nFrames });
            for (int i = 0; i < nMels; i++)
            {
                for (int j = 0; j < nFrames; j++)
                {
                    inputTensor[0, i, j] = melSpec[i, j];
                }
            }
            
            // 推論実行
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("mel", inputTensor)
            };
            
            using var results = _session.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();
            
            return output;
        }
        
        /// <summary>
        /// WAV → Mel → WAV パイプライン（音質向上）
        /// </summary>
        public float[] Enhance(float[] inputWav, int sampleRate = 44100)
        {
            Console.WriteLine($"[NeuralVocoder] Enhancing audio: {inputWav.Length} samples @ {sampleRate}Hz");
            
            // HiFi-GANは22050Hzで学習されているため、ダウンサンプル
            float[] resampled = inputWav;
            int targetSampleRate = 22050;
            
            if (sampleRate != targetSampleRate)
            {
                Console.WriteLine($"[NeuralVocoder] Resampling {sampleRate}Hz → {targetSampleRate}Hz");
                resampled = SimpleResample(inputWav, sampleRate, targetSampleRate);
            }
            
            // WAV → Mel
            var melSpec = WavToMel(resampled, targetSampleRate);
            Console.WriteLine($"[NeuralVocoder] Mel-Spec: {melSpec.GetLength(0)}x{melSpec.GetLength(1)}");
            
            // Mel → WAV
            var enhancedWav = MelToWav(melSpec);
            Console.WriteLine($"[NeuralVocoder] Enhanced: {enhancedWav.Length} samples @ {targetSampleRate}Hz");
            
            // 元のサンプルレートに戻す
            if (sampleRate != targetSampleRate)
            {
                Console.WriteLine($"[NeuralVocoder] Resampling {targetSampleRate}Hz → {sampleRate}Hz");
                enhancedWav = SimpleResample(enhancedWav, targetSampleRate, sampleRate);
            }
            
            // 長さを入力に合わせる
            if (enhancedWav.Length > inputWav.Length)
            {
                Array.Resize(ref enhancedWav, inputWav.Length);
            }
            else if (enhancedWav.Length < inputWav.Length)
            {
                var padded = new float[inputWav.Length];
                Array.Copy(enhancedWav, padded, enhancedWav.Length);
                enhancedWav = padded;
            }
            
            return enhancedWav;
        }
        
        private float[] SimpleResample(float[] input, int fromRate, int toRate)
        {
            double ratio = (double)toRate / fromRate;
            int outputLength = (int)(input.Length * ratio);
            var output = new float[outputLength];
            
            for (int i = 0; i < outputLength; i++)
            {
                double srcPos = i / ratio;
                int srcIdx = (int)srcPos;
                float frac = (float)(srcPos - srcIdx);
                
                if (srcIdx + 1 < input.Length)
                {
                    output[i] = input[srcIdx] * (1 - frac) + input[srcIdx + 1] * frac;
                }
                else if (srcIdx < input.Length)
                {
                    output[i] = input[srcIdx];
                }
            }
            
            return output;
        }
        
        // --- 簡易STFT実装（本番では高品質ライブラリ推奨） ---
        
        private float[,][] ComputeSTFT(float[] signal, int nFft, int hopLength, int winLength)
        {
            int nFrames = (signal.Length - nFft) / hopLength + 1;
            var stft = new float[nFrames, nFft / 2 + 1][];
            
            // Hann窓
            var window = new float[winLength];
            for (int i = 0; i < winLength; i++)
            {
                window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / winLength));
            }
            
            for (int frame = 0; frame < nFrames; frame++)
            {
                int start = frame * hopLength;
                var windowed = new float[nFft];
                
                for (int i = 0; i < winLength && start + i < signal.Length; i++)
                {
                    windowed[i] = signal[start + i] * window[i];
                }
                
                // FFT（簡易実装: 実部のみ）
                for (int k = 0; k <= nFft / 2; k++)
                {
                    float real = 0, imag = 0;
                    for (int n = 0; n < nFft; n++)
                    {
                        float angle = -2 * MathF.PI * k * n / nFft;
                        real += windowed[n] * MathF.Cos(angle);
                        imag += windowed[n] * MathF.Sin(angle);
                    }
                    stft[frame, k] = new[] { real, imag };
                }
            }
            
            return stft;
        }
        
        private float[,] ComputePowerSpectrum(float[,][] stft)
        {
            int nFrames = stft.GetLength(0);
            int nFreqs = stft.GetLength(1);
            var power = new float[nFrames, nFreqs];
            
            for (int i = 0; i < nFrames; i++)
            {
                for (int j = 0; j < nFreqs; j++)
                {
                    float real = stft[i, j][0];
                    float imag = stft[i, j][1];
                    power[i, j] = real * real + imag * imag;
                }
            }
            
            return power;
        }
        
        private float[,] ApplyMelFilterbank(float[,] powerSpec, int sampleRate, int nFft, int nMels)
        {
            int nFrames = powerSpec.GetLength(0);
            int nFreqs = powerSpec.GetLength(1);
            
            // Mel filterbank生成（簡易版）
            var melFilters = CreateMelFilterbank(nFreqs, sampleRate, nMels);
            
            var melSpec = new float[nMels, nFrames];
            for (int frame = 0; frame < nFrames; frame++)
            {
                for (int mel = 0; mel < nMels; mel++)
                {
                    float sum = 0;
                    for (int freq = 0; freq < nFreqs; freq++)
                    {
                        sum += powerSpec[frame, freq] * melFilters[mel, freq];
                    }
                    melSpec[mel, frame] = sum;
                }
            }
            
            return melSpec;
        }
        
        private float[,] CreateMelFilterbank(int nFreqs, int sampleRate, int nMels)
        {
            // Mel scale変換
            float HzToMel(float hz) => 2595f * MathF.Log10(1 + hz / 700f);
            float MelToHz(float mel) => 700f * (MathF.Pow(10, mel / 2595f) - 1);
            
            float minMel = HzToMel(0);
            float maxMel = HzToMel(sampleRate / 2f);
            
            var melPoints = new float[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
            {
                melPoints[i] = minMel + (maxMel - minMel) * i / (nMels + 1);
            }
            
            var hzPoints = melPoints.Select(MelToHz).ToArray();
            var binPoints = hzPoints.Select(hz => (int)(hz * (nFreqs - 1) / (sampleRate / 2f))).ToArray();
            
            var filters = new float[nMels, nFreqs];
            for (int mel = 0; mel < nMels; mel++)
            {
                int left = binPoints[mel];
                int center = binPoints[mel + 1];
                int right = binPoints[mel + 2];
                
                for (int freq = left; freq < center; freq++)
                {
                    filters[mel, freq] = (float)(freq - left) / (center - left);
                }
                for (int freq = center; freq < right; freq++)
                {
                    filters[mel, freq] = (float)(right - freq) / (right - center);
                }
            }
            
            return filters;
        }
        
        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}
