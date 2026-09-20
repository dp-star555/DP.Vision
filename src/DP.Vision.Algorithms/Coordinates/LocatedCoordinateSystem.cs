using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>列向量二维仿射矩阵；齐次末行为[0,0,1]。</summary>
public sealed class CoordinateMatrix2D
{
    internal CoordinateMatrix2D(double m11, double m12, double tx, double m21, double m22, double ty)
    {
        _ = new Coordinate2D(m11, m12); _ = new Coordinate2D(m21, m22); _ = new Coordinate2D(tx, ty);
        M11 = m11; M12 = m12; Tx = tx; M21 = m21; M22 = m22; Ty = ty;
    }
    /// <summary>X的X系数。</summary>
    public double M11 { get; }
    /// <summary>X的Y系数。</summary>
    public double M12 { get; }
    /// <summary>X平移。</summary>
    public double Tx { get; }
    /// <summary>Y的X系数。</summary>
    public double M21 { get; }
    /// <summary>Y的Y系数。</summary>
    public double M22 { get; }
    /// <summary>Y平移。</summary>
    public double Ty { get; }
    /// <summary>映射有限坐标，不隐式修正半像素。</summary><param name="point">输入。</param><returns>输出。</returns>
    public Coordinate2D Map(Coordinate2D point) => new Coordinate2D(M11 * point.X + M12 * point.Y + Tx, M21 * point.X + M22 * point.Y + Ty);
}

/// <summary>同一检测点的图像/模板局部双坐标及来源身份。</summary>
public sealed class LocatedPoint
{
    internal LocatedPoint(LocatedCoordinateSystem system, PointD image)
    { FrameId = system.FrameId; CoordinateSystemId = system.CoordinateSystemId; TemplateSignature = system.TemplateSignature; ImagePosition = new Coordinate2D(image.X, image.Y); LocalPosition = system.ImageToLocal.Map(ImagePosition); }
    /// <summary>图像内容身份。</summary>
    public string FrameId { get; }
    /// <summary>模板坐标系定义身份。</summary>
    public string CoordinateSystemId { get; }
    /// <summary>模板内容签名。</summary>
    public string TemplateSignature { get; }
    /// <summary>当前原图像素边界坐标。</summary>
    public Coordinate2D ImagePosition { get; }
    /// <summary>模板局部像素边界坐标。</summary>
    public Coordinate2D LocalPosition { get; }
}

/// <summary>一次成功定位产生的不可变坐标系；只描述本帧，不保存上一帧回退状态。</summary>
public sealed class LocatedCoordinateSystem
{
    /// <summary>建立定位坐标系；定义ID须由模板制作/文档持久保存。</summary>
    /// <param name="coordinateSystemId">稳定定义ID。</param><param name="templateSignature">模板像素签名。</param>
    /// <param name="frameId">当前帧身份。</param><param name="imageWidth">当前图像宽。</param><param name="imageHeight">当前图像高。</param><param name="pose">模板到本帧姿态。</param>
    public LocatedCoordinateSystem(string coordinateSystemId, string templateSignature, string frameId, int imageWidth, int imageHeight, TemplatePoseTransform pose)
    {
        if (string.IsNullOrWhiteSpace(coordinateSystemId) || string.IsNullOrWhiteSpace(templateSignature) || string.IsNullOrWhiteSpace(frameId)
            || imageWidth < 1 || imageHeight < 1) throw new ArgumentException("Coordinate identity and dimensions are required.");
        Pose = pose ?? throw new ArgumentNullException(nameof(pose));
        if (pose.Scale < .1 || pose.Scale > 10) throw new ArgumentException("Located coordinate scale must be 0.1..10.");
        if (pose.Center.X < 0 || pose.Center.Y < 0 || pose.Center.X > imageWidth || pose.Center.Y > imageHeight)
            throw new ArgumentException("Located template center must lie inside the image.");
        CoordinateSystemId = coordinateSystemId; TemplateSignature = templateSignature; FrameId = frameId; ImageWidth = imageWidth; ImageHeight = imageHeight;
        double a = pose.Scale * Math.Cos(pose.AngleRadians), b = -pose.Scale * Math.Sin(pose.AngleRadians);
        var origin = pose.ToImage(new Coordinate2D(0, 0));
        LocalToImage = new CoordinateMatrix2D(a, b, origin.X, -b, a, origin.Y);
        double factor = pose.Scale * pose.Scale;
        ImageToLocal = new CoordinateMatrix2D(a / factor, -b / factor, (-a * origin.X + b * origin.Y) / factor,
            b / factor, a / factor, (-b * origin.X - a * origin.Y) / factor);
    }
    /// <summary>模板坐标系定义ID，区别于帧ID。</summary>
    public string CoordinateSystemId { get; }
    /// <summary>模板像素与布局的SHA256签名，换模板时不自动接受旧ROI。</summary>
    public string TemplateSignature { get; }
    /// <summary>本帧身份。</summary>
    public string FrameId { get; }
    /// <summary>本帧宽。</summary>
    public int ImageWidth { get; }
    /// <summary>本帧高。</summary>
    public int ImageHeight { get; }
    /// <summary>本帧姿态。</summary>
    public TemplatePoseTransform Pose { get; }
    /// <summary>模板局部到当前图像矩阵。</summary>
    public CoordinateMatrix2D LocalToImage { get; }
    /// <summary>当前图像到模板局部矩阵。</summary>
    public CoordinateMatrix2D ImageToLocal { get; }
    /// <summary>验证图像与预期定义；不能仅依据尺寸推断同帧。</summary>
    /// <param name="frame">输入帧。</param><param name="expectedId">预期定义。</param><param name="expectedSignature">预期模板内容。</param>
    public void Validate(ImageFrame frame, string expectedId, string expectedSignature)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (FrameId != frame.FrameId || ImageWidth != frame.Image.Info.Width || ImageHeight != frame.Image.Info.Height)
            throw new InvalidOperationException("Located coordinate system belongs to another frame.");
        if (CoordinateSystemId != expectedId || TemplateSignature != expectedSignature)
            throw new InvalidOperationException("Coordinate definition or template content differs from the authored ROI.");
    }
    /// <summary>构建同一实际检测点的双坐标表达。</summary><param name="imagePoint">当前图像点。</param><returns>带身份的双坐标点。</returns>
    public LocatedPoint Locate(PointD imagePoint) => new LocatedPoint(this, imagePoint);
    /// <summary>连续ROI映射到当前图像；不变成外接框。</summary><param name="local">局部几何。</param><returns>精确连续几何。</returns>
    public Geometry ToImageGeometry(Geometry local) => TransformGeometry(local, false);
    /// <summary>制作时将图上绘制的ROI逆变换为局部配置。</summary><param name="image">图像几何。</param><returns>局部几何。</returns>
    public Geometry ToLocalGeometry(Geometry image) => TransformGeometry(image, true);
    private Geometry TransformGeometry(Geometry geometry, bool inverse)
    {
        if (geometry == null) throw new ArgumentNullException(nameof(geometry));
        var matrix = inverse ? ImageToLocal : LocalToImage;
        PointD Map(PointD p) { var q = matrix.Map(new Coordinate2D(p.X, p.Y)); return new PointD(q.X, q.Y); }
        double scale = inverse ? 1 / Pose.Scale : Pose.Scale, angle = inverse ? -Pose.AngleRadians : Pose.AngleRadians;
        if (geometry is RectangleGeometry r) return new RectangleGeometry(Map(r.Center), r.Width * scale, r.Height * scale, r.Angle + angle);
        if (geometry is EllipseGeometry e) return new EllipseGeometry(Map(e.Center), e.RadiusX * scale, e.RadiusY * scale, e.Angle + angle);
        if (geometry is ContourGeometry c)
        {
            if (c.Points.Count > 4096) throw new ArgumentException("Located ROI contour point budget exceeded.");
            return new ContourGeometry(c.Points.Select(Map), c.Closed, c.Filled);
        }
        throw new NotSupportedException("Transform continuous ROI shapes before rasterization; raster Region cannot be replaced with its bounding box.");
    }
    /// <summary>局部包含/排除ROI先连续变换，再在当前原图精确栅格化。</summary>
    /// <param name="frame">当前原图。</param><param name="include">局部包含形状，必须非空。</param><param name="exclude">局部排除形状。</param><param name="token">取消。</param><returns>当前原图的精确掩码。</returns>
    public RegionGeometry ResolveRegion(ImageFrame frame, IEnumerable<Geometry> include, IEnumerable<Geometry> exclude, CancellationToken token = default)
    {
        Validate(frame, CoordinateSystemId, TemplateSignature); token.ThrowIfCancellationRequested();
        var shapes = (include ?? throw new ArgumentNullException(nameof(include))).Take(513).ToArray();
        var holes = (exclude ?? throw new ArgumentNullException(nameof(exclude))).Take(513).ToArray();
        if (shapes.Length == 0 || shapes.Length + holes.Length > 512) throw new ArgumentException("Located ROI requires explicit bounded include shapes.");
        return InspectionMask.Compose(frame.Image, shapes.Select(ToImageGeometry), holes.Select(ToImageGeometry), token);
    }
    /// <summary>计算模板布局/像素签名，不绑定读取产生的临时FrameId；最大64MiB。</summary>
    /// <param name="image">借用模板。</param><param name="token">取消。</param><returns>SHA256大写十六进制。</returns>
    public static string ComputeTemplateSignature(IImageSource image, CancellationToken token = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Info.ByteLength > 64 * 1024 * 1024) throw new ArgumentException("Template signature budget exceeded.");
        using var hash = SHA256.Create(); using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, System.Text.Encoding.UTF8, true))
        { writer.Write(image.Info.Width); writer.Write(image.Info.Height); writer.Write((int)image.Info.Layout); }
        var bytes = header.ToArray(); hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        var row = new byte[image.Info.Stride];
        for (int y = 0; y < image.Info.Height; y++)
        { token.ThrowIfCancellationRequested(); image.CopyTo(y * row.Length, row, 0, row.Length); hash.TransformBlock(row, 0, row.Length, row, 0); }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hash.Hash!).Replace("-", "");
    }
}
