using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>角度和尺度的搜索区间；原生引擎直接搜索，采样引擎按步长离散化。</summary>
public sealed class TemplatePoseOptions
{
    /// <summary>创建搜索区间并验证参数。</summary>
    /// <param name="minimumAngleRadians">相对样图的顺时针角度下限，弧度。</param>
    /// <param name="maximumAngleRadians">角度上限；跨度不超过一周，允许区间跨越±π。</param>
    /// <param name="minimumScale">最小尺度，0.1至10。</param><param name="maximumScale">最大尺度。</param>
    /// <param name="minimumScore">引擎定义的最小分数，0..1，非概率；不同引擎不能直接比较。</param><param name="maximumWork">引擎工作预算，最多20亿；像素比较或候选ROI验证。</param>
    /// <param name="angleStepRadians">采样引擎的角度步长；HALCON原生搜索不使用此参数。</param>
    /// <param name="scaleStep">采样引擎的尺度步长；HALCON原生搜索不使用此参数。</param>
    public TemplatePoseOptions(double minimumAngleRadians, double maximumAngleRadians, double minimumScale = 1, double maximumScale = 1,
        double minimumScore = .9, long maximumWork = 200000000, double angleStepRadians = Math.PI / 180, double scaleStep = .01)
    {
        if (new[] { minimumAngleRadians, maximumAngleRadians, minimumScale, maximumScale, minimumScore, angleStepRadians, scaleStep }
                .Any(v => double.IsNaN(v) || double.IsInfinity(v))
            || minimumAngleRadians > maximumAngleRadians || maximumAngleRadians - minimumAngleRadians > 2 * Math.PI + 1e-10
            || minimumScale < .1 || maximumScale > 10 || minimumScale > maximumScale
            || minimumScore < 0 || minimumScore > 1 || maximumWork < 1 || maximumWork > 2000000000
            || angleStepRadians <= 0 || angleStepRadians > 2 * Math.PI || scaleStep <= 0 || scaleStep > 10)
            throw new ArgumentException("搜索区间或预算无效：角度下限不能大于上限、跨度不能超过360°，尺度须为0.1至10，采样步长必须为正数。");
        MinimumAngleRadians = minimumAngleRadians; MaximumAngleRadians = maximumAngleRadians;
        MinimumScale = minimumScale; MaximumScale = maximumScale; MinimumScore = minimumScore; MaximumWork = maximumWork;
        AngleStepRadians = angleStepRadians; ScaleStep = scaleStep;
    }
    /// <summary>顺时针角度下限。</summary>
    public double MinimumAngleRadians { get; }
    /// <summary>顺时针角度上限。</summary>
    public double MaximumAngleRadians { get; }
    /// <summary>尺度下限。</summary>
    public double MinimumScale { get; }
    /// <summary>尺度上限。</summary>
    public double MaximumScale { get; }
    /// <summary>采样引擎的角度步长。</summary>
    public double AngleStepRadians { get; }
    /// <summary>采样引擎的尺度步长。</summary>
    public double ScaleStep { get; }
    /// <summary>将搜索区间拆成[-π,π]内的一至两个连续区间，保留跨界范围及整周搜索。</summary>
    /// <returns>顺时针弧度的起止区间。</returns>
    public IReadOnlyList<(double Minimum, double Maximum)> AngleIntervals()
    {
        double extent = MaximumAngleRadians - MinimumAngleRadians;
        if (extent >= 2 * Math.PI - 1e-10) return new[] { (-Math.PI, Math.PI) };
        double start = Math.Atan2(Math.Sin(MinimumAngleRadians), Math.Cos(MinimumAngleRadians)), end = start + extent;
        return end <= Math.PI + 1e-10 ? new[] { (start, Math.Min(end, Math.PI)) }
            : new[] { (start, Math.PI), (-Math.PI, end - 2 * Math.PI) };
    }
    /// <summary>最小分数。</summary>
    public double MinimumScore { get; }
    /// <summary>保守比较工作量上限。</summary>
    public long MaximumWork { get; }
}

/// <summary>模板像素边界坐标与图像像素边界坐标间的相似变换。</summary>
public sealed class TemplatePoseTransform
{
    /// <summary>创建以模板中心为旋转/尺度中心的变换。</summary>
    /// <param name="templateWidth">模板宽。</param><param name="templateHeight">模板高。</param><param name="center">目标中心。</param><param name="angleRadians">顺时针弧度。</param><param name="scale">正尺度。</param>
    public TemplatePoseTransform(int templateWidth, int templateHeight, PointD center, double angleRadians, double scale)
    {
        if (templateWidth < 1 || templateHeight < 1 || !Finite(center.X) || !Finite(center.Y) || !Finite(angleRadians) || !Finite(scale) || scale <= 0)
            throw new ArgumentException("Invalid pose transform.");
        TemplateWidth = templateWidth; TemplateHeight = templateHeight; Center = center; AngleRadians = angleRadians; Scale = scale;
    }
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    /// <summary>模板宽。</summary>
    public int TemplateWidth { get; }
    /// <summary>模板高。</summary>
    public int TemplateHeight { get; }
    /// <summary>图像中模板中心。</summary>
    public PointD Center { get; }
    /// <summary>顺时针弧度。</summary>
    public double AngleRadians { get; }
    /// <summary>尺度。</summary>
    public double Scale { get; }
    /// <summary>模板点映射到图像，不额外加减半像素。</summary><param name="point">模板边界坐标。</param><returns>图像边界坐标。</returns>
    public Coordinate2D ToImage(Coordinate2D point)
    {
        double x = (point.X - TemplateWidth / 2d) * Scale, y = (point.Y - TemplateHeight / 2d) * Scale;
        return new Coordinate2D(Center.X + Math.Cos(AngleRadians) * x - Math.Sin(AngleRadians) * y, Center.Y + Math.Sin(AngleRadians) * x + Math.Cos(AngleRadians) * y);
    }
    /// <summary>图像点反向映射到模板。</summary><param name="point">图像边界坐标。</param><returns>模板边界坐标。</returns>
    public Coordinate2D ToTemplate(Coordinate2D point)
    {
        double x = (point.X - Center.X) / Scale, y = (point.Y - Center.Y) / Scale;
        return new Coordinate2D(Math.Cos(AngleRadians) * x + Math.Sin(AngleRadians) * y + TemplateWidth / 2d, -Math.Sin(AngleRadians) * x + Math.Cos(AngleRadians) * y + TemplateHeight / 2d);
    }
}

/// <summary>
/// 模板匹配结果：找到的位姿（中心、角度、缩放）和模板参考点在本帧的位置与方向。
/// 只描述测量值，不产生坐标系；坐标系由下游构建节点用这些数值或整个结果生成。
/// 角度单位为度，顺时针为正（图像Y轴向下），与搜索区间、参考方向的约定一致。
/// 未找到时Transform为空，各数值输出为NaN，不能伪装成零位姿。
/// </summary>
public sealed class TemplatePoseResult : IVisionGeometryFact
{
    /// <summary>创建同帧匹配结果。</summary>
    /// <param name="frameId">图像身份。</param><param name="templateFrameId">模板身份。</param><param name="score">最佳候选分数。</param>
    /// <param name="transform">达标变换，未检出为空。</param><param name="reference">所用模板的参考。</param>
    public TemplatePoseResult(string frameId, string templateFrameId, double score, TemplatePoseTransform? transform, TemplateReference reference)
    {
        if (string.IsNullOrWhiteSpace(frameId) || string.IsNullOrWhiteSpace(templateFrameId) || double.IsNaN(score) || score < 0 || score > 1) throw new ArgumentException("Invalid pose evidence.");
        FrameId = frameId; TemplateFrameId = templateFrameId; Score = score; Transform = transform;
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
    }
    /// <summary>是否达到最小分数。</summary>
    public bool Found => Transform != null;
    /// <summary>最佳候选分数，由引擎定义，不是概率。</summary>
    public double Score { get; }
    /// <summary>匹配中心X，原图像素。</summary>
    public double CenterX => Transform?.Center.X ?? double.NaN;
    /// <summary>匹配中心Y，原图像素。</summary>
    public double CenterY => Transform?.Center.Y ?? double.NaN;
    /// <summary>相对模板样图的旋转角度，度，顺时针为正，范围(-180,180]。</summary>
    public double AngleDegrees => Transform is { } pose ? Degrees(pose.AngleRadians) : double.NaN;
    /// <summary>相对模板样图的缩放。</summary>
    public double Scale => Transform?.Scale ?? double.NaN;
    /// <summary>模板参考原点在本帧的X，原图像素。</summary>
    public double ReferenceX => ReferenceToImage?.Tx ?? double.NaN;
    /// <summary>模板参考原点在本帧的Y，原图像素。</summary>
    public double ReferenceY => ReferenceToImage?.Ty ?? double.NaN;
    /// <summary>模板参考X轴在本帧的方向，度，顺时针为正，范围(-180,180]。</summary>
    public double ReferenceAngleDegrees => Transform is { } pose ? Degrees(pose.AngleRadians + Reference.AxisAngleRadians) : double.NaN;
    /// <inheritdoc/>
    public string Summary => Transform is { } pose
        ? FormattableString.Invariant($"找到 · 分数 {Score:F4} · 中心 ({pose.Center.X:F2}, {pose.Center.Y:F2}) · 角度 {AngleDegrees:F2}° · 缩放 {pose.Scale:F4} · 参考点 ({ReferenceX:F2}, {ReferenceY:F2}) 方向 {ReferenceAngleDegrees:F2}°")
        : FormattableString.Invariant($"未找到 · 最佳分数 {Score:F4}");

    /// <summary>图像身份。</summary>
    [Browsable(false)] public string FrameId { get; }
    /// <summary>模板身份。</summary>
    [Browsable(false)] public string TemplateFrameId { get; }
    /// <summary>达标候选的模板到原图变换。</summary>
    [Browsable(false)] public TemplatePoseTransform? Transform { get; }
    /// <summary>所用模板的参考。</summary>
    [Browsable(false)] public TemplateReference Reference { get; }
    /// <summary>匹配中心视觉点，供几何测量绑定。</summary>
    [Browsable(false)] public VisionPoint? CenterPoint => Transform is { } pose ? new VisionPoint(FrameId, pose.Center) : null;
    /// <summary>模板参考原点视觉点，供双点等构建方式绑定。</summary>
    [Browsable(false)] public VisionPoint? ReferencePoint => ReferenceToImage is { } m ? new VisionPoint(FrameId, new PointD(m.Tx, m.Ty)) : null;
    /// <summary>参考坐标（原点在参考点、X轴沿参考方向、单位为模板像素）到原图的映射；未找到为空。</summary>
    [Browsable(false)]
    public CoordinateMatrix2D? ReferenceToImage
    {
        get
        {
            if (Transform is not { } pose) return null;
            var origin = pose.ToImage(new Coordinate2D(Reference.OriginX, Reference.OriginY));
            return VisionCoordinateBuilder.PoseMatrix(new PointD(origin.X, origin.Y), pose.AngleRadians + Reference.AxisAngleRadians, pose.Scale);
        }
    }
    /// <summary>实际匹配轮廓，原图坐标；未找到为空。</summary>
    [Browsable(false)]
    public Geometry? MatchGeometry => Transform is { } p ? new RectangleGeometry(p.Center, p.TemplateWidth * p.Scale, p.TemplateHeight * p.Scale, p.AngleRadians) : null;
    /// <inheritdoc/>
    [Browsable(false)]
    public IReadOnlyList<Geometry> DisplayGeometry
    {
        get
        {
            if (Transform is not { } pose || ReferenceToImage is not { } m) return Array.Empty<Geometry>();
            var origin = new PointD(m.Tx, m.Ty);
            double length = Math.Max(8, Math.Min(pose.TemplateWidth, pose.TemplateHeight) * pose.Scale / 3);
            PointD Axis(double x, double y) { double norm = Math.Sqrt(x * x + y * y); return new PointD(origin.X + length * x / norm, origin.Y + length * y / norm); }
            return Array.AsReadOnly(new Geometry[] { MatchGeometry!, new EllipseGeometry(origin, 2, 2),
                new ContourGeometry(new[] { origin, Axis(m.M11, m.M21) }, false, false),
                new ContourGeometry(new[] { origin, Axis(m.M12, m.M22) }, false, false) });
        }
    }
    /// <summary>正常空检出也完成。</summary>
    [Browsable(false)] public EAlgorithmStatus Status => EAlgorithmStatus.Completed;

    private static double Degrees(double radians)
    {
        double degrees = Math.IEEERemainder(radians * 180 / Math.PI, 360);
        return degrees <= -180 ? degrees + 360 : degrees;
    }
}

/// <summary>按角度和尺度区间搜索模板位置。</summary>
[VisionCapability("location.template-pose", "定位", "旋转尺度模板定位")]
public interface ITemplatePoseLocator
{
    /// <summary>借用图像和模板；8位灰度或显式灰度转换的彩色，拒绝Gray16。</summary>
    /// <param name="frame">图像。</param><param name="template">模板。</param><param name="bounds">搜索矩形。</param><param name="options">搜索区间、分数、预算及采样步长。</param><param name="token">取消。</param><returns>同帧位姿事实；模板参考取整张模板中心。</returns>
    /// <param name="regionMask">候选模板有效采样足迹必须完全包含于此原图掩码。</param>
    TemplatePoseResult Locate(ImageFrame frame, ImageFrame template, PixelBounds bounds, TemplatePoseOptions options, CancellationToken token = default, RegionGeometry? regionMask = null);
}
