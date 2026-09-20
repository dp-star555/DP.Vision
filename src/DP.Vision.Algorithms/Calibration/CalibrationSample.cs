namespace DP.Vision.Algorithms;

/// <summary>同一物理位置在源坐标系及目标坐标系中的观测对。</summary>
public sealed class CalibrationSample
{
    /// <summary>创建标定观测；图像输入应使用原图像素边界坐标，旧像素中心坐标由外部Adapter转换。</summary>
    /// <param name="source">源坐标。</param>
    /// <param name="target">目标坐标，例如机械毫米坐标。</param>
    public CalibrationSample(Coordinate2D source, Coordinate2D target)
    {
        Source = source;
        Target = target;
    }

    /// <summary>源坐标。</summary>
    public Coordinate2D Source { get; }
    /// <summary>目标坐标。</summary>
    public Coordinate2D Target { get; }
}
