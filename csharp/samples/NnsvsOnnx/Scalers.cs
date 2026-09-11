namespace NnsvsOnnx;

/// <summary>
/// NNSVS scaler semantics (nnsvs.util.MinMaxScaler / StandardScaler, verified
/// against nnsvs-master/nnsvs/util.py):
///   MinMaxScaler.transform(x)          = x * scale_ + min_
///   MinMaxScaler.inverse_transform(x)  = (x - min_) / scale_
///   StandardScaler.transform(x)        = (x - mean_) / scale_
///   StandardScaler.inverse_transform(x)= x * scale_ + mean_
/// Coefficients are per-feature-dimension (broadcast over the T axis).
/// </summary>
public sealed class MinMaxScaler
{
    private readonly double[] _min;
    private readonly double[] _scale;

    public int Dim => _min.Length;

    public MinMaxScaler(double[] min, double[] scale)
    {
        if (min.Length != scale.Length) throw new ArgumentException("min/scale length mismatch");
        _min = min;
        _scale = scale;
    }

    public static MinMaxScaler FromNpy(string minPath, string scalePath)
    {
        var min = Npy.Load(minPath).Data;
        var scale = Npy.Load(scalePath).Data;
        return new MinMaxScaler(min, scale);
    }

    /// <summary>x: flat (T, Dim) row-major. Returns a new flat (T, Dim) array.</summary>
    public float[] Transform(float[] x, int t)
    {
        int dim = Dim;
        var y = new float[x.Length];
        for (int i = 0; i < t; i++)
        {
            for (int d = 0; d < dim; d++)
            {
                y[i * dim + d] = (float)(x[i * dim + d] * _scale[d] + _min[d]);
            }
        }
        return y;
    }
}

public sealed class StandardScaler
{
    private readonly double[] _mean;
    private readonly double[] _scale;

    public int Dim => _mean.Length;

    public StandardScaler(double[] mean, double[] scale)
    {
        if (mean.Length != scale.Length) throw new ArgumentException("mean/scale length mismatch");
        _mean = mean;
        _scale = scale;
    }

    public static StandardScaler FromNpy(string meanPath, string scalePath)
    {
        var mean = Npy.Load(meanPath).Data;
        var scale = Npy.Load(scalePath).Data;
        return new StandardScaler(mean, scale);
    }

    /// <summary>x: flat (T, Dim) row-major normalized values. Returns denormalized (T, Dim).</summary>
    public float[] InverseTransform(float[] x, int t)
    {
        int dim = Dim;
        var y = new float[x.Length];
        for (int i = 0; i < t; i++)
        {
            for (int d = 0; d < dim; d++)
            {
                y[i * dim + d] = (float)(x[i * dim + d] * _scale[d] + _mean[d]);
            }
        }
        return y;
    }
}
