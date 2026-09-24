using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace NnsvsOnnx;

/// <summary>Precomputed p_sample coefficients for one diffusion stream (mgc or bap),
/// loaded from WorldToLlsm/onnx/acoustic_diffusion_params.json (read-only, not
/// regenerated here).</summary>
public sealed class DiffusionParams
{
    public required int KStep { get; init; }
    public required double NormScale { get; init; }
    public required double[] SqrtRecipAlphasCumprod { get; init; }
    public required double[] SqrtRecipm1AlphasCumprod { get; init; }
    public required double[] PosteriorMeanCoef1 { get; init; }
    public required double[] PosteriorMeanCoef2 { get; init; }
    public required double[] PosteriorLogVarianceClipped { get; init; }
    public required double[] AlphasCumprod { get; init; }

    public static DiffusionParams FromJson(JsonElement e) => new()
    {
        KStep = e.GetProperty("K_step").GetInt32(),
        NormScale = e.GetProperty("norm_scale").GetDouble(),
        SqrtRecipAlphasCumprod = ReadArr(e, "sqrt_recip_alphas_cumprod"),
        SqrtRecipm1AlphasCumprod = ReadArr(e, "sqrt_recipm1_alphas_cumprod"),
        PosteriorMeanCoef1 = ReadArr(e, "posterior_mean_coef1"),
        PosteriorMeanCoef2 = ReadArr(e, "posterior_mean_coef2"),
        PosteriorLogVarianceClipped = ReadArr(e, "posterior_log_variance_clipped"),
        AlphasCumprod = ReadArr(e, "alphas_cumprod"),
    };

    private static double[] ReadArr(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().Select(x => x.GetDouble()).ToArray();
}

public sealed class AcousticCascadeResult
{
    public required int T { get; init; }
    public required int T1 { get; init; }
    public required int PadOuter { get; init; }
    public required float[] Lf0T1 { get; init; }   // (T1,1)
    public required float[] MgcT1 { get; init; }   // (T1,60)
    public required float[] BapT1 { get; init; }   // (T1,5)
    public required float[] VuvT1 { get; init; }   // (T1,1)
    public required float[] OutFinal { get; init; } // (T,67)
}

/// <summary>
/// Full NNSVS acoustic cascade (lf0 AR decode + mgc/bap 100-step diffusion +
/// vuv), re-implemented from the ONNX sub-graphs exported by
/// WorldToLlsm/export_onnx_acoustic.py. Mirrors
/// NPSSMDNMultistreamParametricModel.forward()'s inference branch and the
/// outer/inner pad_inference padding cascade (see
/// nnsvs/acoustic_models/util.py::pad_inference and
/// nnsvs/acoustic_models/multistream.py).
/// </summary>
public sealed class AcousticModel : IDisposable
{
    public const int InDim = 113;
    public const int R = 4; // reduction_factor
    public const int Lf0EncOutDim = 129;
    public const int Lf0HiddenDim = 256;
    public const int MgcOutDim = 60;
    public const int MgcCondDim = 256;
    public const int BapOutDim = 5;
    public const int BapCondDim = 128;
    public const int VuvInDim = 174;
    public const int OutDim = 67;
    public const int InLf0Idx = 78;

    private readonly InferenceSession _lf0Enc;
    private readonly InferenceSession _lf0Dec;
    private readonly InferenceSession _mgcEnc;
    private readonly InferenceSession _mgcDen;
    private readonly InferenceSession _bapEnc;
    private readonly InferenceSession _bapDen;
    private readonly InferenceSession _vuv;
    private readonly DiffusionParams _mgcParams;
    private readonly DiffusionParams _bapParams;

    /// <summary>Total DDPM steps (K_step) for the mgc diffusion stream, exposed for
    /// A/B testing the PLMS fast sampler against the full-step sampler.</summary>
    public int MgcKStep => _mgcParams.KStep;
    /// <summary>Total DDPM steps (K_step) for the bap diffusion stream.</summary>
    public int BapKStep => _bapParams.KStep;

    public AcousticModel(string onnxDir, string diffusionParamsJsonPath, bool useInt8 = false, int? diffusionIntraOpThreads = null, bool useGpu = false)
    {
        // mgc and bap diffusion (each a 100-step loop) are mathematically independent
        // and are run concurrently in Infer(); split intra-op threads between them so
        // the two don't oversubscribe the CPU. lf0/vuv run alone (sequentially before/
        // after the diffusion phase) and get the full core count.
        // diffusionIntraOpThreads: override for the diffusion sessions when multiple
        // segments run Infer() concurrently (segment-level parallelism).
        // useGpu: put the diffusion encoder/denoiser sessions on DirectML; lf0/vuv
        // (tiny, autoregressive) stay on CPU where they are faster.
        int halfThreads = Math.Max(1, Environment.ProcessorCount / 2);
        int fullThreads = Math.Max(1, Environment.ProcessorCount);
        var seqOptions = MakeSessionOptions(fullThreads);
        var parOptions = MakeSessionOptions(diffusionIntraOpThreads ?? halfThreads, useGpu);

        // useInt8: dynamically-quantized (QInt8 weights) variants of the diffusion
        // encoder/denoiser models, generated offline via onnxruntime.quantization.
        // Only the diffusion-loop models are swapped; lf0/vuv stay float32.
        string Q(string name) => useInt8
            ? Path.Combine(onnxDir, name.Replace(".onnx", "_int8.onnx"))
            : Path.Combine(onnxDir, name);

        _lf0Enc = new InferenceSession(Path.Combine(onnxDir, "acoustic_lf0_encoder.onnx"), seqOptions);
        _lf0Dec = new InferenceSession(Path.Combine(onnxDir, "acoustic_lf0_decoder_step.onnx"), seqOptions);
        _mgcEnc = new InferenceSession(Q("acoustic_mgc_encoder.onnx"), parOptions);
        _mgcDen = new InferenceSession(Q("acoustic_mgc_denoise.onnx"), parOptions);
        _bapEnc = new InferenceSession(Q("acoustic_bap_encoder.onnx"), parOptions);
        _bapDen = new InferenceSession(Q("acoustic_bap_denoise.onnx"), parOptions);
        _vuv = new InferenceSession(Path.Combine(onnxDir, "acoustic_vuv.onnx"), seqOptions);

        using var doc = JsonDocument.Parse(File.ReadAllText(diffusionParamsJsonPath));
        _mgcParams = DiffusionParams.FromJson(doc.RootElement.GetProperty("mgc"));
        _bapParams = DiffusionParams.FromJson(doc.RootElement.GetProperty("bap"));
    }

    private static SessionOptions MakeSessionOptions(int intraOpThreads, bool useGpu = false)
    {
        var opt = new SessionOptions
        {
            IntraOpNumThreads = intraOpThreads,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        if (useGpu)
        {
            // DirectML EP requirements: memory pattern off, sequential execution.
            opt.EnableMemoryPattern = false;
            opt.AppendExecutionProvider_DML(0);
        }
        return opt;
    }

    public static int PadOuter(int t) => R - (t % R); // always in [1,R], never 0

    private static float[] ReplicatePad(float[] x, int t, int dim, int pad)
    {
        int t1 = t + pad;
        var y = new float[t1 * dim];
        Array.Copy(x, y, t * dim);
        for (int i = t; i < t1; i++)
        {
            Array.Copy(x, (t - 1) * dim, y, i * dim, dim);
        }
        return y;
    }

    /// <summary>
    /// Runs the lf0 stream: inner replicate-pad by R, encoder (dynamic T),
    /// autoregressive decoder-step loop (Tr steps of R frames each), then
    /// trims the inner pad back off. x1 is the (already outer-padded) input
    /// of length t1; returns lf0 at length t1.
    /// </summary>
    public float[] RunLf0(float[] x1, int t1)
    {
        int t2 = t1 + R; // inner pad is always a full period
        var xLf0 = ReplicatePad(x1, t1, InDim, R);

        var encInput = new DenseTensor<float>(xLf0, new[] { 1, t2, InDim });
        using var encResults = _lf0Enc.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("x", encInput),
        });
        var enc = encResults.First(r => r.Name == "enc").AsTensor<float>(); // [1, t2/R, 129]

        int tr = t2 / R;
        var h = new float[Lf0HiddenDim];
        var c = new float[Lf0HiddenDim];
        var prevOut = new float[1];
        var outFull = new float[t2];

        for (int step = 0; step < tr; step++)
        {
            var encT = new float[Lf0EncOutDim];
            for (int k = 0; k < Lf0EncOutDim; k++) encT[k] = enc[0, step, k];

            var seg = new float[R];
            for (int k = 0; k < R; k++) seg[k] = xLf0[(step * R + k) * InDim + InLf0Idx];

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("enc_t", new DenseTensor<float>(encT, new[] { 1, Lf0EncOutDim })),
                NamedOnnxValue.CreateFromTensor("lf0_score_seg", new DenseTensor<float>(seg, new[] { 1, R })),
                NamedOnnxValue.CreateFromTensor("prev_out", new DenseTensor<float>(prevOut, new[] { 1, 1 })),
                NamedOnnxValue.CreateFromTensor("h0", new DenseTensor<float>(h, new[] { 1, Lf0HiddenDim })),
                NamedOnnxValue.CreateFromTensor("c0", new DenseTensor<float>(c, new[] { 1, Lf0HiddenDim })),
            };
            using var res = _lf0Dec.Run(inputs);
            var outFrames = res.First(r => r.Name == "out_frames").AsEnumerable<float>().ToArray();
            var h1 = res.First(r => r.Name == "h1").AsEnumerable<float>().ToArray();
            var c1 = res.First(r => r.Name == "c1").AsEnumerable<float>().ToArray();

            Array.Copy(outFrames, 0, outFull, step * R, R);
            prevOut[0] = outFrames[R - 1];
            h = h1;
            c = c1;
        }

        // Trim the inner pad (last R frames) -> length t1.
        var lf0T1 = new float[t1];
        Array.Copy(outFull, 0, lf0T1, 0, t1);
        return lf0T1;
    }

    private float[] RunEncoder(InferenceSession session, float[] x, int t, int inDim, int outDim)
    {
        var input = new DenseTensor<float>(x, new[] { 1, t, inDim });
        using var results = session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("x", input),
        });
        var outTensor = results.First(r => r.Name == "out").AsTensor<float>(); // [1,t,outDim]
        var flat = new float[t * outDim];
        for (int i = 0; i < t; i++)
            for (int d = 0; d < outDim; d++)
                flat[i * outDim + d] = outTensor[0, i, d];
        return flat;
    }

    /// <summary>100-step p_sample reverse-diffusion loop. condInput is the
    /// (t, condInDim) flat encoder input (e.g. cat[x1,lf0]); noise arrays are
    /// injected externally for bit-exact reproducibility against gold data.
    /// Returns the denormalized (t, outDim) result.</summary>
    private float[] RunDiffusion(
        InferenceSession encoderSession, InferenceSession denoiseSession, DiffusionParams p,
        float[] condInput, int t, int condInDim, int condDim, int outDim,
        float[] initNoise, float[][] stepNoises)
    {
        var condFlatTD = RunEncoder(encoderSession, condInput, t, condInDim, condDim); // (t,condDim)

        // transpose (t,condDim) -> (condDim,t) to match cond[1,condDim,T] layout
        var condT = new float[condDim * t];
        for (int i = 0; i < t; i++)
            for (int m = 0; m < condDim; m++)
                condT[m * t + i] = condFlatTD[i * condDim + m];

        var x = (float[])initNoise.Clone(); // flat (outDim,t), matches [1,1,outDim,T] layout
        var xNext = new float[x.Length]; // scratch, copied back into x each step

        // OrtValue fast path: all four tensors wrap pinned managed arrays that are
        // created ONCE and reused for every denoiser call, eliminating per-step
        // DenseTensor/NamedOnnxValue allocations and the LINQ output copy.
        var noisePred = new float[x.Length];
        var tBuf = new long[1];
        var xShape = new long[] { 1, 1, outDim, t };
        using var xOrt = OrtValue.CreateTensorValueFromMemory(x, xShape);
        using var tOrt = OrtValue.CreateTensorValueFromMemory(tBuf, new long[] { 1 });
        using var condOrt = OrtValue.CreateTensorValueFromMemory(condT, new long[] { 1, condDim, t });
        using var outOrt = OrtValue.CreateTensorValueFromMemory(noisePred, xShape);
        using var runOpts = new RunOptions();
        string[] inNames = { "x_t", "t", "cond" };
        string[] outNames = { "noise_pred" };
        var inVals = new OrtValue[] { xOrt, tOrt, condOrt };
        var outVals = new OrtValue[] { outOrt };

        for (int i = p.KStep - 1; i >= 0; i--)
        {
            tBuf[0] = i;
            denoiseSession.Run(runOpts, inNames, inVals, outNames, outVals);

            double sra = p.SqrtRecipAlphasCumprod[i];
            double srm1 = p.SqrtRecipm1AlphasCumprod[i];
            double c1 = p.PosteriorMeanCoef1[i];
            double c2 = p.PosteriorMeanCoef2[i];
            double logVar = p.PosteriorLogVarianceClipped[i];
            double noiseScale = i == 0 ? 0.0 : Math.Exp(0.5 * logVar);
            var stepNoise = stepNoises[i];

            for (int j = 0; j < x.Length; j++)
            {
                double xRecon = sra * x[j] - srm1 * noisePred[j];
                xRecon = Math.Clamp(xRecon, -1.0, 1.0);
                double mean = c1 * xRecon + c2 * x[j];
                xNext[j] = (float)(mean + noiseScale * stepNoise[j]);
            }
            Array.Copy(xNext, x, x.Length); // keep xOrt (pinned over x) valid
        }

        // denorm + transpose (outDim,t) -> (t,outDim)
        var final = new float[t * outDim];
        for (int i = 0; i < t; i++)
            for (int m = 0; m < outDim; m++)
                final[i * outDim + m] = (float)(x[m * t + i] * p.NormScale);
        return final;
    }

    /// <summary>Strided stochastic sampler (DDIM with eta=1, i.e. ancestral
    /// DDPM generalized to a strided schedule). Unlike PLMS (deterministic ODE,
    /// which reproduces model bias as audible periodic artifacts), this keeps
    /// DDPM's per-step noise injection while taking interval-sized jumps, so it
    /// needs K_step/interval denoiser calls. stepNoises is indexed by the
    /// absolute diffusion step (same convention as RunDiffusion).</summary>
    private float[] RunDiffusionStrided(
        InferenceSession encoderSession, InferenceSession denoiseSession, DiffusionParams p,
        float[] condInput, int t, int condInDim, int condDim, int outDim,
        float[] initNoise, float[][] stepNoises, int interval)
    {
        var condFlatTD = RunEncoder(encoderSession, condInput, t, condInDim, condDim);

        var condT = new float[condDim * t];
        for (int i = 0; i < t; i++)
            for (int m = 0; m < condDim; m++)
                condT[m * t + i] = condFlatTD[i * condDim + m];

        var x = (float[])initNoise.Clone();
        var xNext = new float[x.Length];
        var ac = p.AlphasCumprod;

        // Same pinned-OrtValue fast path as RunDiffusion.
        var noisePred = new float[x.Length];
        var tBuf = new long[1];
        var xShape = new long[] { 1, 1, outDim, t };
        using var xOrt = OrtValue.CreateTensorValueFromMemory(x, xShape);
        using var tOrt = OrtValue.CreateTensorValueFromMemory(tBuf, new long[] { 1 });
        using var condOrt = OrtValue.CreateTensorValueFromMemory(condT, new long[] { 1, condDim, t });
        using var outOrt = OrtValue.CreateTensorValueFromMemory(noisePred, xShape);
        using var runOpts = new RunOptions();
        string[] inNames = { "x_t", "t", "cond" };
        string[] outNames = { "noise_pred" };
        var inVals = new OrtValue[] { xOrt, tOrt, condOrt };
        var outVals = new OrtValue[] { outOrt };

        for (int step = p.KStep - interval; step >= 0; step -= interval)
        {
            tBuf[0] = step;
            denoiseSession.Run(runOpts, inNames, inVals, outNames, outVals);

            int prevStep = step - interval;
            double aT = ac[step];
            double aPrev = prevStep >= 0 ? ac[prevStep] : 1.0;
            double sqrtAT = Math.Sqrt(aT);
            double sqrtOneMinusAT = Math.Sqrt(1.0 - aT);
            // eta=1 (full DDPM-equivalent stochasticity on the strided schedule)
            double sigma = prevStep >= 0
                ? Math.Sqrt((1.0 - aPrev) / (1.0 - aT)) * Math.Sqrt(1.0 - aT / aPrev)
                : 0.0;
            double dirCoef = Math.Sqrt(Math.Max(0.0, 1.0 - aPrev - sigma * sigma));
            double sqrtAPrev = Math.Sqrt(aPrev);
            var stepNoise = stepNoises[step];

            for (int j = 0; j < x.Length; j++)
            {
                double x0 = (x[j] - sqrtOneMinusAT * noisePred[j]) / sqrtAT;
                x0 = Math.Clamp(x0, -1.0, 1.0);
                xNext[j] = (float)(sqrtAPrev * x0 + dirCoef * noisePred[j] + sigma * stepNoise[j]);
            }
            Array.Copy(xNext, x, x.Length); // keep xOrt (pinned over x) valid
        }

        // denorm + transpose (outDim,t) -> (t,outDim)
        var final = new float[t * outDim];
        for (int i = 0; i < t; i++)
            for (int m = 0; m < outDim; m++)
                final[i * outDim + m] = (float)(x[m * t + i] * p.NormScale);
        return final;
    }

    /// <summary>PLMS (Pseudo Numerical Methods for Diffusion Models on Manifolds,
    /// https://arxiv.org/abs/2202.09778) fast sampler, ported from
    /// nnsvs.diffsinger.diffusion.GaussianDiffusion.p_sample_plms /
    /// .inference()'s pndm_speedup branch. Deterministic ODE solver: no
    /// per-step noise is injected (unlike <see cref="RunDiffusion"/>), only
    /// initNoise (x_T) is needed. With interval=10 and KStep=100 this makes
    /// 11 denoiser calls total instead of 100 (~9x fewer ONNX Run() calls on
    /// the dominant cost of the acoustic cascade).</summary>
    private float[] RunDiffusionPlms(
        InferenceSession encoderSession, InferenceSession denoiseSession, DiffusionParams p,
        float[] condInput, int t, int condInDim, int condDim, int outDim,
        float[] initNoise, int interval)
    {
        var condFlatTD = RunEncoder(encoderSession, condInput, t, condInDim, condDim); // (t,condDim)

        var condT = new float[condDim * t];
        for (int i = 0; i < t; i++)
            for (int m = 0; m < condDim; m++)
                condT[m * t + i] = condFlatTD[i * condDim + m];
        var condTensor = new DenseTensor<float>(condT, new[] { 1, condDim, t });

        var x = (float[])initNoise.Clone();
        int n = x.Length;
        var alphasCumprod = p.AlphasCumprod;

        float[] RunDenoise(float[] xin, int step)
        {
            var xTensor = new DenseTensor<float>(xin, new[] { 1, 1, outDim, t });
            var tTensor = new DenseTensor<long>(new long[] { step }, new[] { 1 });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("x_t", xTensor),
                NamedOnnxValue.CreateFromTensor("t", tTensor),
                NamedOnnxValue.CreateFromTensor("cond", condTensor),
            };
            using var res = denoiseSession.Run(inputs);
            return res.First(r => r.Name == "noise_pred").AsEnumerable<float>().ToArray();
        }

        float[] GetXPred(float[] xin, float[] noiseT, int step)
        {
            int prevStep = Math.Max(step - interval, 0);
            double aT = alphasCumprod[step];
            double aPrev = alphasCumprod[prevStep];
            double aTSq = Math.Sqrt(aT);
            double aPrevSq = Math.Sqrt(aPrev);
            double coef1 = 1.0 / (aTSq * (aTSq + aPrevSq));
            double coef2 = 1.0 / (aTSq * (Math.Sqrt((1 - aPrev) * aT) + Math.Sqrt((1 - aT) * aPrev)));
            double scale = aPrev - aT;
            var xPred = new float[n];
            for (int j = 0; j < n; j++)
                xPred[j] = (float)(xin[j] + scale * (coef1 * xin[j] - coef2 * noiseT[j]));
            return xPred;
        }

        // Rolling history of up to 4 raw (non-averaged) noise predictions,
        // most recent last -- mirrors Python's deque(maxlen=4).
        var noiseHist = new List<float[]>(4);

        for (int step = p.KStep - interval; step >= 0; step -= interval)
        {
            var noisePred = RunDenoise(x, step);
            float[] noisePredPrime;
            if (noiseHist.Count == 0)
            {
                var xPred0 = GetXPred(x, noisePred, step);
                int prevStep = Math.Max(step - interval, 0);
                var noisePredPrev = RunDenoise(xPred0, prevStep);
                noisePredPrime = new float[n];
                for (int j = 0; j < n; j++) noisePredPrime[j] = (noisePred[j] + noisePredPrev[j]) / 2f;
            }
            else if (noiseHist.Count == 1)
            {
                var h1 = noiseHist[^1];
                noisePredPrime = new float[n];
                for (int j = 0; j < n; j++) noisePredPrime[j] = (3f * noisePred[j] - h1[j]) / 2f;
            }
            else if (noiseHist.Count == 2)
            {
                var h1 = noiseHist[^1]; var h2 = noiseHist[^2];
                noisePredPrime = new float[n];
                for (int j = 0; j < n; j++) noisePredPrime[j] = (23f * noisePred[j] - 16f * h1[j] + 5f * h2[j]) / 12f;
            }
            else
            {
                var h1 = noiseHist[^1]; var h2 = noiseHist[^2]; var h3 = noiseHist[^3];
                noisePredPrime = new float[n];
                for (int j = 0; j < n; j++) noisePredPrime[j] = (55f * noisePred[j] - 59f * h1[j] + 37f * h2[j] - 9f * h3[j]) / 24f;
            }

            x = GetXPred(x, noisePredPrime, step);
            noiseHist.Add(noisePred);
            if (noiseHist.Count > 4) noiseHist.RemoveAt(0);
        }

        // denorm + transpose (outDim,t) -> (t,outDim)
        var final = new float[t * outDim];
        for (int i = 0; i < t; i++)
            for (int m = 0; m < outDim; m++)
                final[i * outDim + m] = (float)(x[m * t + i] * p.NormScale);
        return final;
    }

    private float[] RunVuv(float[] vuvInput, int t)
    {
        var input = new DenseTensor<float>(vuvInput, new[] { 1, t, VuvInDim });
        using var results = _vuv.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("x", input),
        });
        var outTensor = results.First(r => r.Name == "out").AsTensor<float>(); // [1,t,1]
        var flat = new float[t];
        for (int i = 0; i < t; i++) flat[i] = outTensor[0, i, 0];
        return flat;
    }

    /// <summary>
    /// Full cascade for one utterance. x: flat (T,113) raw (unpadded)
    /// normalized input features. Noise arrays must be exactly
    /// K_step+1 (t,outDim) tensors each (index 0 = initial x0, matching the
    /// dump_test_vectors.py / export_onnx_acoustic.py convention), flattened
    /// as (outDim, T1) each (channel-major) to match the ONNX NCHW-like layout.
    /// stepNoises are only consumed when usePlms is false (full 100-step DDPM,
    /// bit-exact against gold test data); when usePlms is true, the mgc/bap
    /// diffusion instead uses the deterministic PLMS ODE solver (<see
    /// cref="RunDiffusionPlms"/>), which only needs the initial noise and runs
    /// K_step/pndmSpeedup + 1 denoiser calls instead of K_step.
    /// </summary>
    public AcousticCascadeResult Infer(
        float[] x, int t,
        float[] mgcInitNoise, float[][] mgcStepNoises,
        float[] bapInitNoise, float[][] bapStepNoises,
        bool usePlms = false, int pndmSpeedup = 10, bool useStrided = false)
    {
        int padOuter = PadOuter(t);
        int t1 = t + padOuter;
        var x1 = ReplicatePad(x, t, InDim, padOuter);

        var lf0T1 = RunLf0(x1, t1);

        // mgc_inp = bap_inp = cat([x1, lf0T1], -1) -> (t1, 114)
        var condInp = new float[t1 * (InDim + 1)];
        for (int i = 0; i < t1; i++)
        {
            Array.Copy(x1, i * InDim, condInp, i * (InDim + 1), InDim);
            condInp[i * (InDim + 1) + InDim] = lf0T1[i];
        }

        Task<float[]> mgcTask, bapTask;
        if (usePlms)
        {
            mgcTask = Task.Run(() => RunDiffusionPlms(_mgcEnc, _mgcDen, _mgcParams, condInp, t1, InDim + 1, MgcCondDim, MgcOutDim,
                mgcInitNoise, pndmSpeedup));
            bapTask = Task.Run(() => RunDiffusionPlms(_bapEnc, _bapDen, _bapParams, condInp, t1, InDim + 1, BapCondDim, BapOutDim,
                bapInitNoise, pndmSpeedup));
        }
        else if (useStrided)
        {
            mgcTask = Task.Run(() => RunDiffusionStrided(_mgcEnc, _mgcDen, _mgcParams, condInp, t1, InDim + 1, MgcCondDim, MgcOutDim,
                mgcInitNoise, mgcStepNoises, pndmSpeedup));
            bapTask = Task.Run(() => RunDiffusionStrided(_bapEnc, _bapDen, _bapParams, condInp, t1, InDim + 1, BapCondDim, BapOutDim,
                bapInitNoise, bapStepNoises, pndmSpeedup));
        }
        else
        {
            mgcTask = Task.Run(() => RunDiffusion(_mgcEnc, _mgcDen, _mgcParams, condInp, t1, InDim + 1, MgcCondDim, MgcOutDim,
                mgcInitNoise, mgcStepNoises));
            bapTask = Task.Run(() => RunDiffusion(_bapEnc, _bapDen, _bapParams, condInp, t1, InDim + 1, BapCondDim, BapOutDim,
                bapInitNoise, bapStepNoises));
        }
        Task.WaitAll(mgcTask, bapTask);
        var mgcT1 = mgcTask.Result;
        var bapT1 = bapTask.Result;

        // vuv_inp = cat([x1, mgcT1(60), lf0T1(1)], -1) -> (t1, 174)
        var vuvInp = new float[t1 * VuvInDim];
        for (int i = 0; i < t1; i++)
        {
            int o = i * VuvInDim;
            Array.Copy(x1, i * InDim, vuvInp, o, InDim);
            Array.Copy(mgcT1, i * MgcOutDim, vuvInp, o + InDim, MgcOutDim);
            vuvInp[o + InDim + MgcOutDim] = lf0T1[i];
        }
        var vuvT1 = RunVuv(vuvInp, t1);

        // out_t1 = cat([mgc(60), lf0(1), vuv(1), bap(5)], -1) -> (t1,67); trim tail padOuter -> (t,67)
        var outFinal = new float[t * OutDim];
        for (int i = 0; i < t; i++)
        {
            int o = i * OutDim;
            Array.Copy(mgcT1, i * MgcOutDim, outFinal, o, MgcOutDim);
            outFinal[o + MgcOutDim] = lf0T1[i];
            outFinal[o + MgcOutDim + 1] = vuvT1[i];
            Array.Copy(bapT1, i * BapOutDim, outFinal, o + MgcOutDim + 2, BapOutDim);
        }

        return new AcousticCascadeResult
        {
            T = t,
            T1 = t1,
            PadOuter = padOuter,
            Lf0T1 = lf0T1,
            MgcT1 = mgcT1,
            BapT1 = bapT1,
            VuvT1 = vuvT1,
            OutFinal = outFinal,
        };
    }

    public void Dispose()
    {
        _lf0Enc.Dispose();
        _lf0Dec.Dispose();
        _mgcEnc.Dispose();
        _mgcDen.Dispose();
        _bapEnc.Dispose();
        _bapDen.Dispose();
        _vuv.Dispose();
    }
}
