using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>原始8位RGB编码值的区域均值，不进行线性化、白平衡或产品判定。</summary>
public sealed class ColorAnalysisResult
{
    /// <summary>建立RGB均值事实。</summary>
    /// <param name="frameId">原图身份。</param>
    /// <param name="pixelCount">参与统计的像素数。</param>
    /// <param name="red">红均值，0至255。</param>
    /// <param name="green">绿均值，0至255。</param>
    /// <param name="blue">蓝均值，0至255。</param>
    public ColorAnalysisResult(string frameId, long pixelCount, double red, double green, double blue)
    {
        if (string.IsNullOrWhiteSpace(frameId) || pixelCount < 1 || !Valid(red) || !Valid(green) || !Valid(blue))
            throw new ArgumentException("Invalid color statistics.");
        FrameId = frameId; PixelCount = pixelCount; Red = red; Green = green; Blue = blue;
    }
    /// <summary>本次检测使用的定位坐标系；统计值仍为原图像素统计。</summary>
    public LocatedCoordinateSystem? CoordinateSystem { get; private set; }
    /// <summary>附加同帧定位来源，不修改原结果。</summary><param name="system">定位。</param><returns>独立结果。</returns>
    public ColorAnalysisResult InCoordinates(LocatedCoordinateSystem system)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (system.FrameId != FrameId) throw new InvalidOperationException("Result coordinate frame mismatch.");
        return new ColorAnalysisResult(FrameId, PixelCount, Red, Green, Blue) { CoordinateSystem = system };
    }
    private static bool Valid(double value) => !double.IsNaN(value) && value >= 0 && value <= 255;
    /// <summary>输入帧身份。</summary>
    public string FrameId { get; }
    /// <summary>完成状态。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
    /// <summary>像素数量。</summary>
    public long PixelCount { get; }
    /// <summary>红通道均值。</summary>
    public double Red { get; }
    /// <summary>绿通道均值。</summary>
    public double Green { get; }
    /// <summary>蓝通道均值。</summary>
    public double Blue { get; }
}

/// <summary>矩形范围RGB编码值统计；Gray8按R=G=B处理，Alpha不参与加权。</summary>
public interface IColorAnalyzer
{
    /// <summary>借用图像统计颜色，不支持Gray16且不隐式降位深。</summary>
    /// <param name="frame">只读帧。</param>
    /// <param name="bounds">完全在原图内的半开范围。</param>
    /// <param name="token">协作取消。</param>
    /// <param name="regionMask">可选原图精确Region；空选区不能计算均值，明确抛错。</param>
    /// <returns>不拥有图像的颜色事实。</returns>
    ColorAnalysisResult Analyze(ImageFrame frame, PixelBounds bounds, CancellationToken token = default, RegionGeometry? regionMask = null);
}
