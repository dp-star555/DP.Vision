using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace DP.Vision.Algorithms;

/// <summary>直线距离语义，禁止把无限直线交点距离当作有限工件间距。</summary>
public enum EVisionLineDistanceMode
{
    /// <summary>无限直线最短距离；不平行时为零。</summary>
    [Description("无限直线")]
    InfiniteLines = 0,
    /// <summary>两个有限线段间的最短距离。</summary>
    [Description("有限线段")]
    Segments = 1
}

/// <summary>带单位空间、原图最近点及来源的距离证据；不是产品判定。</summary>
public sealed class GeometricDistanceResult : IVisionGeometryFact
{
    internal GeometricDistanceResult(VisionPoint a, VisionPoint b, EVisionCoordinateSpace space, EVisionLineDistanceMode mode, EVisionDistanceKind kind)
    {
        A = a; B = b; Space = space; Mode = mode; Kind = kind;
        var pa = a.Position(space); var pb = b.Position(space);
        Distance = Math.Sqrt(Math.Pow(pa.X - pb.X, 2) + Math.Pow(pa.Y - pb.Y, 2));
        if (double.IsNaN(Distance) || double.IsInfinity(Distance)) throw new InvalidOperationException("距离计算溢出。");
    }
    /// <summary>第一对象上的最近点；点到线时为输入点。</summary>
    public VisionPoint A { get; }
    /// <summary>第二对象上的最近点；点到线时为投影或最近端点。</summary>
    public VisionPoint B { get; }
    /// <summary>原图身份。</summary>
    public string FrameId => A.FrameId;
    /// <summary>采用的坐标空间。</summary>
    public EVisionCoordinateSpace Space { get; }
    /// <summary>无限直线或有限线段。</summary>
    public EVisionLineDistanceMode Mode { get; }
    /// <summary>参与测量的对象类型。</summary>
    public EVisionDistanceKind Kind { get; }
    /// <summary>非负距离，单位由 Unit 指明。</summary>
    public double Distance { get; }
    /// <summary>明确的像素单位，不隐式输出毫米。</summary>
    public string Unit => Space == EVisionCoordinateSpace.Image ? "image-px" : CoordinateSystem!.Definition.UnitName;
    /// <summary>共同定位来源；原图测量也可保留来源。</summary>
    public VisionCoordinateSystem? CoordinateSystem => A.CoordinateSystem;
    /// <summary>测量正常完成，与业务阈值无关。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
    /// <inheritdoc/>
    public IReadOnlyList<Geometry> DisplayGeometry => Array.AsReadOnly(new Geometry[] { Distance == 0
        ? (Geometry)new EllipseGeometry(A.ImagePosition, 1, 1) : new ContourGeometry(new[] { A.ImagePosition, B.ImagePosition }) });
    /// <inheritdoc/>
    public string Summary => FormattableString.Invariant($"距离 {Distance:F6} {Unit}；{(Kind == EVisionDistanceKind.PointToPoint ? "点到点" : Kind == EVisionDistanceKind.PointToLine ? "点到线" : "线到线")}；{(Kind == EVisionDistanceKind.PointToPoint ? "点间距" : Mode == EVisionLineDistanceMode.Segments ? "有限线段" : "无限直线")}；完成不等于产品合格。");
}
