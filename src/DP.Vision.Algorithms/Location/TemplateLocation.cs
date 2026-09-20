using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>固定旋转/尺度下的平移模板定位结果；没有满足阈值的位置是正常空检出。</summary>
public sealed class TemplateLocationResult
{
    /// <summary>创建定位事实。</summary>
    /// <param name="frameId">搜索原图身份。</param>
    /// <param name="templateFrameId">模板身份。</param>
    /// <param name="found">是否满足最小分数。</param>
    /// <param name="score">1减归一化均方差，0至1，不是概率。</param>
    /// <param name="bounds">满足阈值时的原图匹配范围。</param>
    public TemplateLocationResult(string frameId, string templateFrameId, bool found, double score, PixelBounds? bounds)
    {
        if (string.IsNullOrWhiteSpace(frameId) || string.IsNullOrWhiteSpace(templateFrameId)
            || double.IsNaN(score) || score < 0 || score > 1 || found != bounds.HasValue)
            throw new ArgumentException("Invalid template location facts.");
        FrameId = frameId; TemplateFrameId = templateFrameId; Found = found; Score = score; Bounds = bounds;
    }
    /// <summary>实际模板到原图姿态；随动时不能把Bounds当作精确匹配几何。</summary>
    public TemplatePoseTransform? Transform { get; private set; }
    /// <summary>搜索范围绑定的父坐标系，不是新模板坐标系。</summary>
    public LocatedCoordinateSystem? SearchCoordinateSystem { get; private set; }
    /// <summary>实际匹配轮廓，原图坐标。</summary>
    public Geometry? MatchGeometry => Transform is { } p
        ? new RectangleGeometry(p.Center, p.TemplateWidth * p.Scale, p.TemplateHeight * p.Scale, p.AngleRadians)
        : Bounds is { } b ? new RectangleGeometry(new PointD(b.X + b.Width / 2d, b.Y + b.Height / 2d), b.Width, b.Height) : null;
    /// <summary>匹配中心相对于父坐标系的双坐标。</summary>
    public LocatedPoint? LocatedCenter => Transform is { } p ? SearchCoordinateSystem?.Locate(p.Center) : null;
    /// <summary>将固定父姿态下的平移搜索结果转换为原图事实。</summary>
    /// <param name="pose">实际匹配。</param><param name="parent">同帧父定位。</param><returns>匹配轮廓及诊断外接矩形。</returns>
    public static TemplateLocationResult FromPose(TemplatePoseResult pose, LocatedCoordinateSystem parent)
    {
        if (pose == null || parent == null) throw new ArgumentNullException(nameof(pose));
        if (pose.FrameId != parent.FrameId) throw new InvalidOperationException("Search coordinate frame mismatch.");
        PixelBounds? bounds = null;
        if (pose.Transform is { } p)
        {
            var box = new RectangleGeometry(p.Center, p.TemplateWidth * p.Scale, p.TemplateHeight * p.Scale, p.AngleRadians).Bounds;
            int x = checked((int)Math.Floor(box.X + 1e-9)), y = checked((int)Math.Floor(box.Y + 1e-9));
            bounds = new PixelBounds(x, y, checked((int)Math.Ceiling(box.X + box.Width - 1e-9)) - x, checked((int)Math.Ceiling(box.Y + box.Height - 1e-9)) - y);
        }
        return new TemplateLocationResult(pose.FrameId, pose.TemplateFrameId, pose.Found, pose.Score, bounds)
        { Transform = pose.Transform, SearchCoordinateSystem = parent };
    }
    /// <summary>搜索原图身份。</summary>
    public string FrameId { get; }
    /// <summary>模板原图身份。</summary>
    public string TemplateFrameId { get; }
    /// <summary>是否存在满足阈值的位置。</summary>
    public bool Found { get; }
    /// <summary>归一化相似分数，不是合格率。</summary>
    public double Score { get; }
    /// <summary>原图诊断外接矩形；随动时精确范围使用MatchGeometry，空检出为空。</summary>
    public PixelBounds? Bounds { get; }
    /// <summary>完成状态，与Found独立。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
}

/// <summary>平移模板定位契约，不支持旋转/尺度搜索，不冒充通用形状定位。</summary>
public interface ITemplateLocator
{
    /// <summary>在搜索矩形中匹配模板矩形；范围不可越界，模板不可比搜索区大。</summary>
    /// <param name="frame">搜索帧。</param>
    /// <param name="search">原图搜索范围。</param>
    /// <param name="template">借用模板帧。</param>
    /// <param name="templateBounds">模板原图范围。</param>
    /// <param name="minimumScore">0至1分数阈值。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="regionMask">候选有效采样足迹须完全包含的精确原图掩码。</param>
    /// <param name="searchCoordinates">固定父姿态，模板在该姿态下仅搜索平移；需使用完整模板。</param>
    /// <returns>最佳位置或明确空检出。</returns>
    TemplateLocationResult Locate(ImageFrame frame, PixelBounds search, ImageFrame template, PixelBounds templateBounds,
        double minimumScore = .9, CancellationToken token = default, RegionGeometry? regionMask = null, LocatedCoordinateSystem? searchCoordinates = null);
}
