using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>稳定定义在一次执行图像中的不可变仿射映射；统一处理身份、ROI及坐标表达。</summary>
public class VisionCoordinateSystem
{
    /// <summary>建立局部到原图的可逆仿射映射；允许业务原点在图像外。</summary>
    /// <param name="definition">稳定定义。</param><param name="frameId">本帧身份。</param>
    /// <param name="imageWidth">宽。</param><param name="imageHeight">高。</param>
    /// <param name="localToImage">局部到原图。</param><param name="sourceIdentity">本次构建证据标识。</param>
    public VisionCoordinateSystem(VisionCoordinateDefinition definition, string frameId, int imageWidth, int imageHeight,
        CoordinateMatrix2D localToImage, string sourceIdentity = "")
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(frameId) || imageWidth < 1 || imageHeight < 1) throw new ArgumentException("坐标变换必须具有有效帧和尺寸。");
        LocalToImage = localToImage ?? throw new ArgumentNullException(nameof(localToImage));
        ImageToLocal = localToImage.Inverse(); FrameId = frameId; ImageWidth = imageWidth; ImageHeight = imageHeight;
        SourceIdentity = sourceIdentity ?? throw new ArgumentNullException(nameof(sourceIdentity));
    }
    /// <summary>稳定业务定义。</summary>
    public VisionCoordinateDefinition Definition { get; }
    /// <summary>兼容定义身份名称。</summary>
    public string CoordinateSystemId => Definition.Id;
    /// <summary>适用帧身份。</summary>
    public string FrameId { get; }
    /// <summary>适用图像宽。</summary>
    public int ImageWidth { get; }
    /// <summary>适用图像高。</summary>
    public int ImageHeight { get; }
    /// <summary>定位/参数/标定构建来源；不代替业务定义。</summary>
    public string SourceIdentity { get; }
    /// <summary>局部到原图矩阵。</summary>
    public CoordinateMatrix2D LocalToImage { get; }
    /// <summary>原图到局部矩阵。</summary>
    public CoordinateMatrix2D ImageToLocal { get; }
    /// <summary>是否可表示为正方向旋转和等比例缩放。</summary>
    public bool IsSimilarity
    {
        get
        {
            var m = LocalToImage; double a = m.M11 * m.M11 + m.M21 * m.M21, b = m.M12 * m.M12 + m.M22 * m.M22;
            return m.M11 * m.M22 - m.M12 * m.M21 > 0 && Math.Abs(a - b) <= 1e-10 * Math.Max(a, b)
                && Math.Abs(m.M11 * m.M12 + m.M21 * m.M22) <= 1e-10 * Math.Sqrt(a * b);
        }
    }
    /// <summary>相似变换尺度；非等比例仿射必须明确拒绝需要单一尺度的算子。</summary>
    public double SimilarityScale => IsSimilarity ? Math.Sqrt(LocalToImage.M11 * LocalToImage.M11 + LocalToImage.M21 * LocalToImage.M21)
        : throw new NotSupportedException("此算子要求旋转/等比例缩放，不能用单一尺度替代剪切或非等比仿射。");
    /// <summary>相似变换顺时针方向角。</summary>
    public double RotationRadians { get { _ = SimilarityScale; return Math.Atan2(LocalToImage.M21, LocalToImage.M11); } }
    /// <summary>验证本帧身份和尺寸。</summary><param name="frame">当前帧。</param>
    public void ValidateFrame(ImageFrame frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (FrameId != frame.FrameId || ImageWidth != frame.Image.Info.Width || ImageHeight != frame.Image.Info.Height)
            throw new InvalidOperationException("坐标变换属于另一帧或不同图像尺寸。");
    }
    /// <summary>验证制作时稳定定义，不依赖模板。</summary>
    /// <param name="frame">当前帧。</param><param name="id">期望定义。</param><param name="version">版本。</param><param name="signature">语义签名。</param>
    public void ValidateDefinition(ImageFrame frame, string id, int version, string signature)
    {
        ValidateFrame(frame);
        if (Definition.Id != id || Definition.Version != version || Definition.Signature != signature)
            throw new InvalidOperationException("坐标定义、版本或原点/轴/单位约定与制作配置不一致。");
    }
    /// <summary>给原图点附加双坐标表达。</summary><param name="imagePoint">原图点。</param><returns>双坐标事实。</returns>
    public LocatedPoint Locate(PointD imagePoint) => new LocatedPoint(this, imagePoint);
    /// <summary>局部连续几何转换为原图，不重采样图像。</summary><param name="local">局部几何。</param><returns>原图几何。</returns>
    public Geometry ToImageGeometry(Geometry local) => MapGeometry(local, LocalToImage);
    /// <summary>制作时将原图几何转换到稳定坐标。</summary><param name="image">原图几何。</param><returns>局部几何。</returns>
    public Geometry ToLocalGeometry(Geometry image) => MapGeometry(image, ImageToLocal);
    private static Geometry MapGeometry(Geometry geometry, CoordinateMatrix2D matrix)
    {
        if (geometry == null) throw new ArgumentNullException(nameof(geometry));
        PointD Map(PointD p) { var q = matrix.Map(new Coordinate2D(p.X, p.Y)); return new PointD(q.X, q.Y); }
        if (geometry is ContourGeometry c)
        {
            if (c.Points.Count > 4096) throw new ArgumentException("ROI轮廓预算超限。");
            return new ContourGeometry(c.Points.Select(Map), c.Closed, c.Filled);
        }
        if (geometry is RectangleGeometry r)
        {
            double co = Math.Cos(r.Angle), si = Math.Sin(r.Angle);
            double ux = matrix.M11 * co + matrix.M12 * si, uy = matrix.M21 * co + matrix.M22 * si;
            double vx = -matrix.M11 * si + matrix.M12 * co, vy = -matrix.M21 * si + matrix.M22 * co;
            double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
            if (Math.Abs(ux * vx + uy * vy) <= 1e-10 * lu * lv)
                return new RectangleGeometry(Map(r.Center), r.Width * lu, r.Height * lv, Math.Atan2(uy, ux));
            return new ContourGeometry(r.Corners.Select(Map), true, true);
        }
        if (geometry is EllipseGeometry e)
        {
            double co = Math.Cos(e.Angle), si = Math.Sin(e.Angle);
            double ux = (matrix.M11 * co + matrix.M12 * si) * e.RadiusX, uy = (matrix.M21 * co + matrix.M22 * si) * e.RadiusX;
            double vx = (-matrix.M11 * si + matrix.M12 * co) * e.RadiusY, vy = (-matrix.M21 * si + matrix.M22 * co) * e.RadiusY;
            double xx = ux * ux + vx * vx, yy = uy * uy + vy * vy, xy = ux * uy + vx * vy;
            double disc = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy), major = (xx + yy + disc) / 2;
            double determinant = ux * vy - uy * vx, minor = determinant * determinant / major;
            return new EllipseGeometry(Map(e.Center), Math.Sqrt(major), Math.Sqrt(minor), .5 * Math.Atan2(2 * xy, xx - yy));
        }
        throw new NotSupportedException("只转换连续矩形、椭圆和轮廓；栅格Region保持本帧像素身份。");
    }
    /// <summary>局部包含/排除ROI精确转换并在本帧栅格化。</summary>
    /// <param name="frame">本帧。</param><param name="include">包含形状。</param><param name="exclude">排除形状。</param><param name="token">取消。</param><returns>原图掩码。</returns>
    public RegionGeometry ResolveRegion(ImageFrame frame, IEnumerable<Geometry> include, IEnumerable<Geometry> exclude, CancellationToken token = default)
    {
        ValidateFrame(frame); token.ThrowIfCancellationRequested();
        var shapes = (include ?? throw new ArgumentNullException(nameof(include))).Take(513).ToArray();
        var holes = (exclude ?? throw new ArgumentNullException(nameof(exclude))).Take(513).ToArray();
        if (shapes.Length == 0 || shapes.Length + holes.Length > 512) throw new ArgumentException("坐标ROI需要显式包含形状并遵守预算。");
        return InspectionMask.Compose(frame.Image, shapes.Select(ToImageGeometry), holes.Select(ToImageGeometry), token);
    }
}

/// <summary>构建节点的统一输出，适用于定位和参数构建来源。</summary>
public sealed class VisionCoordinateSystemResult : IVisionCoordinateResult, IVisionGeometryFact
{
    /// <summary>包装已验证的本帧坐标系。</summary><param name="coordinateSystem">有效坐标系。</param><param name="calibrationRms">可选标定拟合误差。</param>
    public VisionCoordinateSystemResult(VisionCoordinateSystem coordinateSystem, double? calibrationRms = null)
    {
        CoordinateSystem = coordinateSystem ?? throw new ArgumentNullException(nameof(coordinateSystem));
        if (calibrationRms.HasValue && (double.IsNaN(calibrationRms.Value) || double.IsInfinity(calibrationRms.Value) || calibrationRms.Value < 0)) throw new ArgumentException("标定误差必须有限非负。");
        CalibrationRms = calibrationRms;
    }
    /// <inheritdoc/>
    public string FrameId => CoordinateSystem.FrameId;
    /// <inheritdoc/>
    public VisionCoordinateSystem CoordinateSystem { get; }
    /// <summary>原图像素拟合RMS；不表示独立验证精度。</summary>
    public double? CalibrationRms { get; }
    /// <inheritdoc/>
    public string Summary => "坐标系 " + CoordinateSystem.Definition.Name + "（" + CoordinateSystem.Definition.Id + "，v" + CoordinateSystem.Definition.Version + "，" + CoordinateSystem.Definition.UnitName + "）；来源 " + CoordinateSystem.SourceIdentity
        + (CalibrationRms.HasValue ? FormattableString.Invariant($"；拟合RMS {CalibrationRms.Value:F4}image-px。") : "。");
    /// <inheritdoc/>
    public IReadOnlyList<Geometry> DisplayGeometry
    {
        get
        {
            var m = CoordinateSystem.LocalToImage; var origin = new PointD(m.Tx, m.Ty);
            PointD Axis(double x, double y) { double length = Math.Sqrt(x * x + y * y); return new PointD(origin.X + 40 * x / length, origin.Y + 40 * y / length); }
            return Array.AsReadOnly(new Geometry[] { new EllipseGeometry(origin, 2, 2),
                new ContourGeometry(new[] { origin, Axis(m.M11, m.M21) }, false, false),
                new ContourGeometry(new[] { origin, Axis(m.M12, m.M22) }, false, false) });
        }
    }
}
