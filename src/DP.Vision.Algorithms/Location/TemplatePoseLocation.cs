using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>明确离散角度/尺度候选，不宣称连续形状匹配。</summary>
public sealed class TemplatePoseOptions
{
    /// <summary>复制候选并验证预算。</summary>
    /// <param name="anglesRadians">图像坐标顺时针弧度，最多181项。</param><param name="scales">正尺度0.1..10，最多32项；组合不超过512。</param>
    /// <param name="minimumScore">最小1-归一化均方差，0..1，非概率。</param><param name="maximumWork">位置数×模板面积累计预算，最多20亿。</param>
    public TemplatePoseOptions(IEnumerable<double> anglesRadians, IEnumerable<double> scales, double minimumScore = .9, long maximumWork = 200000000)
    {
        var angles = (anglesRadians ?? throw new ArgumentNullException(nameof(anglesRadians))).Take(182).ToArray();
        var factors = (scales ?? throw new ArgumentNullException(nameof(scales))).Take(33).ToArray();
        if (angles.Length < 1 || angles.Length > 181 || factors.Length < 1 || factors.Length > 32 || angles.Length * factors.Length > 512
            || angles.Any(a => double.IsNaN(a) || double.IsInfinity(a) || Math.Abs(a) > Math.PI * 2)
            || factors.Any(s => double.IsNaN(s) || s < .1 || s > 10) || double.IsNaN(minimumScore) || minimumScore < 0 || minimumScore > 1
            || maximumWork < 1 || maximumWork > 2000000000) throw new ArgumentException("Invalid pose search candidates or budget.");
        AnglesRadians = Array.AsReadOnly(angles); Scales = Array.AsReadOnly(factors); MinimumScore = minimumScore; MaximumWork = maximumWork;
    }
    /// <summary>离散角度。</summary>
    public IReadOnlyList<double> AnglesRadians { get; }
    /// <summary>离散尺度。</summary>
    public IReadOnlyList<double> Scales { get; }
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

/// <summary>单个最佳姿态候选；未找到时Transform为空，不能伪装成零位姿。</summary>
public sealed class TemplatePoseResult
{
    /// <summary>创建同帧事实。</summary><param name="frameId">图像身份。</param><param name="templateFrameId">模板身份。</param><param name="score">最佳候选分数。</param><param name="transform">达标变换，未检出为空。</param>
    public TemplatePoseResult(string frameId, string templateFrameId, double score, TemplatePoseTransform? transform)
    {
        if (string.IsNullOrWhiteSpace(frameId) || string.IsNullOrWhiteSpace(templateFrameId) || double.IsNaN(score) || score < 0 || score > 1) throw new ArgumentException("Invalid pose evidence.");
        FrameId = frameId; TemplateFrameId = templateFrameId; Score = score; Transform = transform;
    }
    /// <summary>可选搜索父坐标系，区别于本节点产生的子坐标系。</summary>
    public LocatedCoordinateSystem? SearchCoordinateSystem { get; private set; }
    /// <summary>记录同帧搜索来源；结果姿态已是原图坐标，不再次乘父矩阵。</summary>
    /// <param name="parent">父定位。</param><returns>独立结果。</returns>
    public TemplatePoseResult WithSearchCoordinates(LocatedCoordinateSystem parent)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent));
        if (parent.FrameId != FrameId) throw new InvalidOperationException("Parent coordinate frame mismatch.");
        var copy = (TemplatePoseResult)MemberwiseClone(); copy.SearchCoordinateSystem = parent; return copy;
    }
    /// <summary>成功定位的共享坐标系；普通算法输出或未检出时为空。</summary>
    public LocatedCoordinateSystem? CoordinateSystem { get; private set; }
    /// <summary>为成功定位附加稳定模板定义；不修改原结果。</summary>
    /// <param name="definitionId">持久化定义ID。</param><param name="frame">当前图像。</param><param name="template">本次模板。</param><param name="token">取消。</param><returns>独立结果。</returns>
    public TemplatePoseResult InCoordinateSystem(string definitionId, ImageFrame frame, ImageFrame template, CancellationToken token = default)
    {
        if (frame == null || template == null) throw new ArgumentNullException(nameof(frame));
        if (FrameId != frame.FrameId || TemplateFrameId != template.FrameId) throw new InvalidOperationException("Pose frame identity mismatch.");
        if (Transform == null) return new TemplatePoseResult(FrameId, TemplateFrameId, Score, null);
        if (Transform.TemplateWidth != template.Image.Info.Width || Transform.TemplateHeight != template.Image.Info.Height) throw new InvalidOperationException("Template dimensions mismatch.");
        return new TemplatePoseResult(FrameId, TemplateFrameId, Score, Transform) { CoordinateSystem = new LocatedCoordinateSystem(definitionId,
            LocatedCoordinateSystem.ComputeTemplateSignature(template.Image, token), FrameId, frame.Image.Info.Width, frame.Image.Info.Height, Transform) };
    }
    /// <summary>图像身份。</summary>
    public string FrameId { get; }
    /// <summary>模板身份。</summary>
    public string TemplateFrameId { get; }
    /// <summary>最佳候选分数，不是概率。</summary>
    public double Score { get; }
    /// <summary>是否达到最小分数。</summary>
    public bool Found => Transform != null;
    /// <summary>达标候选的精确坐标变换。</summary>
    public TemplatePoseTransform? Transform { get; }
    /// <summary>正常空检出也完成。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
}

/// <summary>有界离散旋转/尺度模板定位。</summary>
public interface ITemplatePoseLocator
{
    /// <summary>借用图像和模板；8位灰度或显式灰度转换的彩色，拒绝Gray16。</summary>
    /// <param name="frame">图像。</param><param name="template">模板。</param><param name="bounds">搜索矩形。</param><param name="options">候选/分数/预算。</param><param name="token">取消。</param><returns>同帧位姿事实。</returns>
    /// <param name="regionMask">候选模板有效采样足迹必须完全包含于此原图掩码。</param>
    TemplatePoseResult Locate(ImageFrame frame, ImageFrame template, PixelBounds bounds, TemplatePoseOptions options, CancellationToken token = default, RegionGeometry? regionMask = null);
}
