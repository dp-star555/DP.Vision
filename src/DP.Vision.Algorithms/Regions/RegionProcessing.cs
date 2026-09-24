using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>带来源帧和画布尺寸的精确Region事实，不拥有图像。</summary>
public sealed class RegionAnalysisResult
{
    /// <summary>构造并检查所有游程在画布内。</summary>
    /// <param name="frameId">内容身份。</param><param name="width">原图宽。</param><param name="height">原图高。</param><param name="region">精确Region。</param>
    public RegionAnalysisResult(string frameId, int width, int height, RegionGeometry region)
    {
        if (string.IsNullOrWhiteSpace(frameId) || width < 1 || height < 1 || (long)width * height > 16777216)
            throw new ArgumentException("Invalid Region identity or canvas budget.");
        Region = region ?? throw new ArgumentNullException(nameof(region));
        foreach (var run in region.Runs)
            if (run.Row < 0 || run.Row >= height || run.Start < 0 || run.EndExclusive > width) throw new ArgumentException("Region outside canvas.");
        FrameId = frameId; Width = width; Height = height;
    }
    /// <summary>可选定位来源；Region始终是当前图像栅格，不伪造局部栅格。</summary>
    public LocatedCoordinateSystem? CoordinateSystem { get; private set; }
    /// <summary>附加同帧同尺寸定位来源。</summary><param name="system">定位。</param><returns>独立结果。</returns>
    public RegionAnalysisResult InCoordinates(LocatedCoordinateSystem system)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (system.FrameId != FrameId || system.ImageWidth != Width || system.ImageHeight != Height) throw new InvalidOperationException("Result coordinate frame mismatch.");
        return new RegionAnalysisResult(FrameId, Width, Height, Region) { CoordinateSystem = system };
    }
    /// <summary>来源帧。</summary>
    public string FrameId { get; }
    /// <summary>原图宽。</summary>
    public int Width { get; }
    /// <summary>原图高。</summary>
    public int Height { get; }
    /// <summary>精确Region，允许空。</summary>
    public RegionGeometry Region { get; }
    /// <summary>像素面积。</summary>
    public long Area => Region.AreaPixels;
    /// <summary>完成不等于非空或产品合格。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
    /// <summary>拒绝跨帧或尺寸不一致的掩码。</summary>
    /// <param name="frame">借用帧。</param>
    public void ValidateFrame(ImageFrame frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (FrameId != frame.FrameId || Width != frame.Image.Info.Width || Height != frame.Image.Info.Height)
            throw new ArgumentException("Region and image must have the same FrameId and dimensions.");
    }
    /// <summary>线性游程求交，保留孔洞，不依赖UI或SDK。</summary>
    /// <param name="a">第一个Region。</param><param name="b">第二个Region。</param><param name="token">取消。</param>
    /// <returns>精确交集。</returns>
    public static RegionGeometry Intersect(RegionGeometry a, RegionGeometry b, CancellationToken token = default)
    {
        if (a == null || b == null) throw new ArgumentNullException(nameof(a));
        return a.Intersect(b, token);
    }
}

/// <summary>显式二值形态学。</summary>
public enum ERegionMorphology
{
    /// <summary>膨胀。</summary>
    Dilate,
    /// <summary>腐蚀。</summary>
    Erode,
    /// <summary>先腐蚀后膨胀。</summary>
    Open,
    /// <summary>先膨胀后腐蚀。</summary>
    Close,
    /// <summary>填补不与画布边界四连通的背景孔洞。</summary>
    FillHoles
}

/// <summary>结构元素形状。</summary>
public enum ERegionKernel
{
    /// <summary>方形。</summary>
    Rectangle,
    /// <summary>OpenCV离散椭圆。</summary>
    Ellipse,
    /// <summary>十字。</summary>
    Cross
}

/// <summary>中立阈值和精确Region处理。</summary>
public interface IRegionProcessor
{
    /// <summary>8位灰度闭区间分割；彩色显式按标准灰度转换，Gray16拒绝隐式降位深。</summary>
    /// <param name="frame">输入帧。</param><param name="bounds">半开范围。</param><param name="minimumGray">下界。</param><param name="maximumGray">上界。</param><param name="mask">可选精确范围。</param><param name="token">取消。</param>
    /// <returns>原图坐标Region事实。</returns>
    RegionAnalysisResult Threshold(ImageFrame frame, PixelBounds bounds, int minimumGray, int maximumGray, RegionGeometry? mask = null, CancellationToken token = default);
    /// <summary>有限画布上的形态学，画布外恒为背景；半径0为恒等，FillHoles忽略核。</summary>
    /// <param name="input">输入事实。</param><param name="operation">操作。</param><param name="radius">半径0..31。</param><param name="kernel">核形状。</param><param name="token">取消。</param>
    /// <returns>同帧新Region事实。</returns>
    RegionAnalysisResult Morphology(RegionAnalysisResult input, ERegionMorphology operation, int radius = 1, ERegionKernel kernel = ERegionKernel.Rectangle, CancellationToken token = default);
}
