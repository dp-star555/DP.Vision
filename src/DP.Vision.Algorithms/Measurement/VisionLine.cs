using System;
using System.Collections.Generic;

namespace DP.Vision.Algorithms;

/// <summary>两个不同的同帧视觉点定义的直线；端点也定义有限线段，不是拟合证据。</summary>
public sealed class VisionLine : IVisionGeometryFact
{
    /// <summary>创建直线；混帧、混局部定义和重合点必须拒绝。</summary>
    /// <param name="a">第一点。</param><param name="b">第二点。</param>
    public VisionLine(VisionPoint a, VisionPoint b)
    {
        A = a ?? throw new ArgumentNullException(nameof(a)); B = b ?? throw new ArgumentNullException(nameof(b));
        if (a.FrameId != b.FrameId) throw new InvalidOperationException("直线端点不能来自不同帧。");
        if (!VisionPoint.SameCoordinates(a.CoordinateSystem, b.CoordinateSystem))
            throw new InvalidOperationException("直线端点的坐标来源不同，请先显式转换到共同坐标系。");
        var dx = b.ImagePosition.X - a.ImagePosition.X; var dy = b.ImagePosition.Y - a.ImagePosition.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < 1e-9) throw new ArgumentException("重合点不能生成直线。");
    }
    /// <summary>同帧第一点。</summary>
    public VisionPoint A { get; }
    /// <summary>同帧第二点。</summary>
    public VisionPoint B { get; }
    /// <summary>来源图像身份。</summary>
    public string FrameId => A.FrameId;
    /// <summary>共同定位来源。</summary>
    public VisionCoordinateSystem? CoordinateSystem => A.CoordinateSystem;
    /// <summary>原图端点间长度，不是无限直线长度。</summary>
    public double ImageLength => Math.Sqrt(Math.Pow(B.ImagePosition.X - A.ImagePosition.X, 2) + Math.Pow(B.ImagePosition.Y - A.ImagePosition.Y, 2));
    /// <inheritdoc/>
    public IReadOnlyList<Geometry> DisplayGeometry => Array.AsReadOnly(new Geometry[] { new ContourGeometry(new[] { A.ImagePosition, B.ImagePosition }) });
    /// <inheritdoc/>
    public string Summary => FormattableString.Invariant($"生成直线；端点间原图长度 {ImageLength:F4}px；坐标来源 {CoordinateSystem?.CoordinateSystemId ?? "原图"}。");
    /// <summary>显式转换表达，原图端点不变。</summary>
    /// <param name="coordinates">同帧目标定位或空。</param><returns>独立直线。</returns>
    public VisionLine InCoordinates(VisionCoordinateSystem? coordinates) => new VisionLine(A.InCoordinates(coordinates), B.InCoordinates(coordinates));
}
