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
    /// <summary>没有提供定位变换时使用的恒等变换；零矩阵会抹掉坐标且不可逆。</summary>
    public static CoordinateMatrix2D Identity { get; } = FromAffine(1, 0, 0, 0, 1, 0);
    /// <summary>构造非退化的二维仿射变换；平移、旋转、缩放和剪切都可表示。</summary>
    /// <param name="m11">目标X的源X系数。</param><param name="m12">目标X的源Y系数。</param><param name="tx">目标X平移。</param>
    /// <param name="m21">目标Y的源X系数。</param><param name="m22">目标Y的源Y系数。</param><param name="ty">目标Y平移。</param>
    /// <returns>可逆的像素边界坐标变换。</returns>
    public static CoordinateMatrix2D FromAffine(double m11, double m12, double tx, double m21, double m22, double ty)
    {
        var matrix = new CoordinateMatrix2D(m11, m12, tx, m21, m22, ty);
        double determinant = m11 * m22 - m12 * m21;
        if (!IsInvertible(m11, m12, m21, m22, determinant))
            throw new ArgumentException("Affine transform must be invertible.");
        return matrix;
    }
    /// <summary>求逆变换，退化矩阵不能反演。</summary>
    /// <returns>目标到源的仿射变换。</returns>
    public CoordinateMatrix2D Inverse()
    {
        double det = M11 * M22 - M12 * M21;
        if (!IsInvertible(M11, M12, M21, M22, det))
            throw new InvalidOperationException("Affine transform is not invertible.");
        return FromAffine(M22 / det, -M12 / det, (M12 * Ty - M22 * Tx) / det,
            -M21 / det, M11 / det, (M21 * Tx - M11 * Ty) / det);
    }
    private static bool IsInvertible(double m11, double m12, double m21, double m22, double determinant)
    {
        // 相对退化阈值不依赖像素/毫米倍率；正反变换采用同一条件。
        double scale = Math.Max(Math.Max(Math.Abs(m11), Math.Abs(m12)), Math.Max(Math.Abs(m21), Math.Abs(m22)));
        double normalizedDet = (m11 / scale) * (m22 / scale) - (m12 / scale) * (m21 / scale);
        return scale > 0 && determinant != 0 && !double.IsNaN(determinant) && !double.IsInfinity(determinant) && Math.Abs(normalizedDet) > 1e-12;
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
    /// <summary>矩阵组合：本矩阵乘右矩阵，先执行右侧转换。</summary>
    /// <param name="right">先执行的转换。</param><returns>组合矩阵。</returns>
    public CoordinateMatrix2D Multiply(CoordinateMatrix2D right)
    {
        if (right == null) throw new ArgumentNullException(nameof(right));
        return FromAffine(M11 * right.M11 + M12 * right.M21, M11 * right.M12 + M12 * right.M22, M11 * right.Tx + M12 * right.Ty + Tx,
            M21 * right.M11 + M22 * right.M21, M21 * right.M12 + M22 * right.M22, M21 * right.Tx + M22 * right.Ty + Ty);
    }
    /// <summary>将矩形或轮廓映射为精确连续轮廓；剪切矩形不能退化为轴对齐外接框。</summary>
    /// <param name="geometry">局部矩形或轮廓；未支持的几何必须显式拒绝。</param>
    /// <returns>原顺序顶点映射后的轮廓，保留填充语义。</returns>
    public ContourGeometry MapGeometry(Geometry geometry)
    {
        if (geometry == null) throw new ArgumentNullException(nameof(geometry));
        if (geometry is RectangleGeometry rectangle)
            return new ContourGeometry(rectangle.Corners.Select(MapPoint), true, true);
        if (geometry is ContourGeometry contour)
            return new ContourGeometry(contour.Points.Select(MapPoint), contour.Closed, contour.Filled);
        throw new NotSupportedException("Affine mapping currently supports rectangles and contours, not raster regions or ellipses.");
    }
    private PointD MapPoint(PointD point)
    {
        var mapped = Map(new Coordinate2D(point.X, point.Y));
        return new PointD(mapped.X, mapped.Y);
    }
}

/// <summary>同一检测点的原图/业务局部双坐标及来源身份。</summary>
public sealed class LocatedPoint
{
    internal LocatedPoint(VisionCoordinateSystem system, PointD image)
    { FrameId = system.FrameId; CoordinateSystemId = system.CoordinateSystemId; TemplateSignature = (system as LocatedCoordinateSystem)?.TemplateSignature ?? ""; ImagePosition = new Coordinate2D(image.X, image.Y); LocalPosition = system.ImageToLocal.Map(ImagePosition); Definition = system.Definition; }
    /// <summary>通用业务定义与单位。</summary>
    public VisionCoordinateDefinition Definition { get; }
    /// <summary>图像内容身份。</summary>
    public string FrameId { get; }
    /// <summary>业务坐标定义身份。</summary>
    public string CoordinateSystemId { get; }
    /// <summary>模板兼容来源的内容签名；通用来源为空。</summary>
    public string TemplateSignature { get; }
    /// <summary>当前原图像素边界坐标。</summary>
    public Coordinate2D ImagePosition { get; }
    /// <summary>业务局部坐标，单位由Definition决定。</summary>
    public Coordinate2D LocalPosition { get; }
}

/// <summary>一次成功定位产生的不可变坐标系；只描述本帧，不保存上一帧回退状态。</summary>
public sealed class LocatedCoordinateSystem : VisionCoordinateSystem
{
    /// <summary>模板定位兼容来源；通用业务坐标请使用VisionCoordinateBuilder。</summary>
    /// <param name="coordinateSystemId">模板定义。</param><param name="templateSignature">模型签名。</param><param name="frameId">本帧。</param>
    /// <param name="imageWidth">宽。</param><param name="imageHeight">高。</param><param name="pose">定位姿态。</param>
    public LocatedCoordinateSystem(string coordinateSystemId, string templateSignature, string frameId, int imageWidth, int imageHeight, TemplatePoseTransform pose)
        : base(new VisionCoordinateDefinition(coordinateSystemId, coordinateSystemId, 1, EVisionCoordinateUnit.TemplatePixel,
            "template:" + RequireSignature(templateSignature), "模板X右/Y下"), frameId, imageWidth, imageHeight, PoseMatrix(pose), "template:" + templateSignature)
    {
        if (pose.Scale < .1 || pose.Scale > 10 || pose.Center.X < 0 || pose.Center.Y < 0 || pose.Center.X > imageWidth || pose.Center.Y > imageHeight)
            throw new ArgumentException("Located template pose is outside supported bounds.");
        TemplateSignature = templateSignature; Pose = pose;
    }
    private static string RequireSignature(string value) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Template signature is required.");
    private static CoordinateMatrix2D PoseMatrix(TemplatePoseTransform pose)
    {
        if (pose == null) throw new ArgumentNullException(nameof(pose));
        var origin = pose.ToImage(new Coordinate2D(0, 0));
        double a = pose.Scale * Math.Cos(pose.AngleRadians), b = pose.Scale * Math.Sin(pose.AngleRadians);
        return CoordinateMatrix2D.FromAffine(a, -b, origin.X, b, a, origin.Y);
    }
    /// <summary>模板定位证据签名，仅供兼容绑定及来源校验。</summary>
    public string TemplateSignature { get; }
    /// <summary>原始模板姿态证据。</summary>
    public TemplatePoseTransform Pose { get; }
    /// <summary>兼容旧模板定义和模型签名校验。</summary>
    /// <param name="frame">本帧。</param><param name="expectedId">定义。</param><param name="expectedSignature">模型签名。</param>
    public void Validate(ImageFrame frame, string expectedId, string expectedSignature)
    {
        ValidateFrame(frame);
        if (CoordinateSystemId != expectedId || TemplateSignature != expectedSignature)
            throw new InvalidOperationException("Coordinate definition or template content differs from the authored ROI.");
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
