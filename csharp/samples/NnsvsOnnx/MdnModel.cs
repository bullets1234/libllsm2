using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace NnsvsOnnx;

/// <summary>
/// timelag.onnx / duration.onnx: x[1,T,inDim] -> mu[1,T,1], sigma[1,T,1]
/// (MDN most-probable component already selected inside the graph; see
/// WorldToLlsm/export_onnx_simple.py MdnWrapper).
/// </summary>
public sealed class MdnModel : IDisposable
{
    private readonly InferenceSession _session;

    public MdnModel(string onnxPath)
    {
        _session = new InferenceSession(onnxPath);
    }

    public (float[] mu, float[] sigma) Run(float[] x, int t, int inDim)
    {
        var inputTensor = new DenseTensor<float>(x, new[] { 1, t, inDim });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("x", inputTensor),
        };
        using var results = _session.Run(inputs);
        var mu = results.First(r => r.Name == "mu").AsEnumerable<float>().ToArray();
        var sigma = results.First(r => r.Name == "sigma").AsEnumerable<float>().ToArray();
        return (mu, sigma);
    }

    public void Dispose() => _session.Dispose();
}
