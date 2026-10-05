using System;
using System.Collections.Generic;

namespace DP.Vision.Algorithms;

/// <summary>同帧视觉点；原图位置始终保留，局部位置带明确定位来源。</summary>
public sealed class VisionPoint : IVisionGeometryFact
{
    /// <summary>创建原图事实，可附加同帧定位。</summary>
    /// <param name="frameId">来源帧身份。</param><param name="imagePosition">原图像素坐标。</param><param name="coordinates">可选定位来源。</param>
    public VisionPoint(string frameId, PointD imagePosition, VisionCoordinateSystem? coordinates = null)
    {
        if (string.IsNullOrWhiteSpace(frameId)) throw new ArgumentException("视觉点必须携带帧身份。", nameof(frameId));
        ValidatePosition(imagePosition);
        if (coordinates != null && coordinates.FrameId != frameId) throw new InvalidOperationException("视觉点与定位不属于同一帧。");
        FrameId = frameId; ImagePosition = imagePosition; CoordinateSystem = coordinates;
    }
    /// <summary>来源原图身份。</summary>
    public string FrameId { get; }
    /// <summary>原图像素边界坐标。</summary>
    public PointD ImagePosition { get; }
    /// <inheritdoc/>
    public IReadOnlyList<Geometry> DisplayGeometry => Array.AsReadOnly(new Geometry[] { new EllipseGeometry(ImagePosition, 1, 1) });
    /// <inheritdoc/>
    public string Summary => FormattableString.Invariant($"视觉点 原图({ImagePosition.X:F4}, {ImagePosition.Y:F4})image-px；坐标来源 {CoordinateSystem?.CoordinateSystemId ?? "原图"}")
        + (LocalPosition is { } local ? FormattableString.Invariant($"；局部({local.X:F4}, {local.Y:F4}){CoordinateSystem!.Definition.UnitName}。") : "。");
    /// <summary>局部坐标来源；空表示只有原图表达。</summary>
    public VisionCoordinateSystem? CoordinateSystem { get; }
    /// <summary>业务局部坐标及定义单位；未绑定时为空。</summary>
    public Coordinate2D? LocalPosition => CoordinateSystem?.ImageToLocal.Map(new Coordinate2D(ImagePosition.X, ImagePosition.Y));
    /// <summary>按显式空间创建点，不隐式使用恒等定位。</summary>
    /// <param name="frameId">原图身份。</param><param name="position">指定空间坐标。</param><param name="space">空间。</param><param name="coordinates">局部模式所需定位。</param><returns>保留原图及定位的事实。</returns>
    public static VisionPoint Create(string frameId, PointD position, EVisionCoordinateSpace space, VisionCoordinateSystem? coordinates = null)
    {
        ValidatePosition(position);
        if (!Enum.IsDefined(typeof(EVisionCoordinateSpace), space)) throw new ArgumentException("坐标空间无效。");
        if (space == EVisionCoordinateSpace.TemplateLocal)
        {
            if (coordinates == null) throw new InvalidOperationException("局部点必须明确绑定成功定位。");
            var image = coordinates.LocalToImage.Map(new Coordinate2D(position.X, position.Y));
            position = new PointD(image.X, image.Y);
        }
        return new VisionPoint(frameId, position, coordinates);
    }
    /// <summary>在相同原图中显式换定位表达；空表示显式转为原图表达。</summary>
    /// <param name="coordinates">目标同帧定位或空。</param><returns>独立事实，原图位置不变。</returns>
    public VisionPoint InCoordinates(VisionCoordinateSystem? coordinates) => new VisionPoint(FrameId, ImagePosition, coordinates);
    /// <summary>读取指定空间坐标；局部表达缺失时拒绝。</summary>
    /// <param name="space">空间。</param><returns>所选空间坐标。</returns>
    public PointD Position(EVisionCoordinateSpace space)
    {
        if (space == EVisionCoordinateSpace.Image) return ImagePosition;
        if (space != EVisionCoordinateSpace.TemplateLocal) throw new ArgumentException("坐标空间无效。");
        var local = LocalPosition ?? throw new InvalidOperationException("此点没有局部坐标来源。");
        return new PointD(local.X, local.Y);
    }
    internal static void ValidatePosition(PointD point)
    {
        if (double.IsNaN(point.X) || double.IsInfinity(point.X) || double.IsNaN(point.Y) || double.IsInfinity(point.Y)
            || Math.Abs(point.X) > 1e9 || Math.Abs(point.Y) > 1e9) throw new ArgumentException("视觉坐标必须有限且不超过正负十亿。");
    }
    internal static bool SameCoordinates(VisionCoordinateSystem? a, VisionCoordinateSystem? b) =>
        ReferenceEquals(a, b) || a != null && b != null && a.FrameId == b.FrameId
        && a.CoordinateSystemId == b.CoordinateSystemId && a.Definition.Signature == b.Definition.Signature
        && a.ImageWidth == b.ImageWidth && a.ImageHeight == b.ImageHeight
        && a.LocalToImage.M11 == b.LocalToImage.M11 && a.LocalToImage.M12 == b.LocalToImage.M12
        && a.LocalToImage.M21 == b.LocalToImage.M21 && a.LocalToImage.M22 == b.LocalToImage.M22
        && a.LocalToImage.Tx == b.LocalToImage.Tx && a.LocalToImage.Ty == b.LocalToImage.Ty;
}
