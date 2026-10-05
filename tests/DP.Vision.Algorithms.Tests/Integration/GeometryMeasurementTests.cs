using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>坐标归属、相似变换及直线距离真值。</summary>
[TestClass]
public sealed class GeometryMeasurementTests
{
    /// <summary>旋转、缩放之后局部距离保持不变，原图距离随尺度改变。</summary>
    [TestMethod]
    [DataRow(0d, 1d)]
    [DataRow(Math.PI / 2, 2d)]
    [DataRow(.73d, 1.5d)]
    [DataRow(-.53d, .6d)]
    public void DistancesRespectCoordinateSpaceAndPose(double angle, double scale)
    {
        var system = System("part", angle, scale);
        VisionPoint P(double x, double y) => VisionPoint.Create("frame", new PointD(x, y), EVisionCoordinateSpace.Local, system);
        var algorithm = new GeometryMeasurer();
        var line = algorithm.GenerateLine(P(0, 0), P(10, 0)); var point = P(12, 3);
        var local = algorithm.PointToLine(point, line, EVisionCoordinateSpace.Local);
        Assert.AreEqual(3, local.Distance, 1e-9); Assert.AreEqual("reference-px", local.Unit);
        Assert.AreEqual(12, local.B.LocalPosition!.Value.X, 1e-9); Assert.AreEqual(0, local.B.LocalPosition.Value.Y, 1e-9);
        Assert.AreEqual(3 * scale, algorithm.PointToLine(point, line).Distance, 1e-9);
        Assert.AreEqual(Math.Sqrt(13), algorithm.PointToLine(point, line, EVisionCoordinateSpace.Local, EVisionLineDistanceMode.Segments).Distance, 1e-9);
        var parallel = algorithm.GenerateLine(P(2, 5), P(12, 5));
        Assert.AreEqual(5, algorithm.LineToLine(line, parallel, EVisionCoordinateSpace.Local).Distance, 1e-9);
        Assert.AreEqual(5 * scale, algorithm.LineToLine(line, parallel).Distance, 1e-9);
        Assert.AreEqual(5, algorithm.PointToPoint(P(0, 0), P(3, 4), EVisionCoordinateSpace.Local).Distance, 1e-9);
        Assert.AreEqual("frame", local.FrameId); Assert.AreSame(system, local.CoordinateSystem);
        Assert.AreEqual(EVisionDistanceKind.PointToLine, local.Kind);
    }
    /// <summary>无限直线、有限线段和端点方向的语义必须独立。</summary>
    [TestMethod]
    public void InfiniteAndSegmentDistancesHaveExplicitSemantics()
    {
        VisionPoint P(double x, double y) => new VisionPoint("frame", new PointD(x, y));
        VisionLine L(double ax, double ay, double bx, double by) => new VisionLine(P(ax, ay), P(bx, by));
        var algorithm = new GeometryMeasurer(); var horizontal = L(0, 0, 2, 0); var vertical = L(3, 1, 3, 3);
        Assert.AreEqual(0, algorithm.LineToLine(horizontal, vertical).Distance, 1e-12);
        var segments = algorithm.LineToLine(horizontal, vertical, mode: EVisionLineDistanceMode.Segments);
        Assert.AreEqual(Math.Sqrt(2), segments.Distance, 1e-12);
        Assert.AreEqual(segments.Distance, algorithm.LineToLine(vertical, horizontal, mode: EVisionLineDistanceMode.Segments).Distance, 1e-12);
        Assert.AreEqual(0, algorithm.LineToLine(horizontal, L(1, -2, 1, 2), mode: EVisionLineDistanceMode.Segments).Distance, 1e-12);
        Assert.AreEqual(2, algorithm.LineToLine(horizontal, L(4, 0, 7, 0), mode: EVisionLineDistanceMode.Segments).Distance, 1e-12);
        Assert.AreEqual(0, algorithm.LineToLine(horizontal, L(4, 0, 7, 0)).Distance, 1e-12);
        Assert.AreEqual(1, algorithm.PointToLine(P(3, 1), horizontal).Distance, 1e-12);
        Assert.AreEqual(Math.Sqrt(2), algorithm.PointToLine(P(3, 1), horizontal, mode: EVisionLineDistanceMode.Segments).Distance, 1e-12);
        Assert.AreEqual(2, algorithm.PointToLine(P(2, 1), L(0, 5, 0, -5)).Distance, 1e-12);
    }
    /// <summary>不同帧、不同局部定义及退化输入不能生成有效测量。</summary>
    [TestMethod]
    public void RejectsMixedFramesDefinitionsAndDegenerateGeometry()
    {
        var a = new VisionPoint("frame", new PointD(0, 0)); var b = new VisionPoint("frame", new PointD(10, 0));
        var algorithm = new GeometryMeasurer(); var line = new VisionLine(a, b);
        Assert.ThrowsExactly<ArgumentException>(() => algorithm.GenerateLine(a, a));
        Assert.ThrowsExactly<InvalidOperationException>(() => algorithm.GenerateLine(a, new VisionPoint("other", new PointD(10, 0))));
        Assert.ThrowsExactly<InvalidOperationException>(() => algorithm.PointToLine(new VisionPoint("other", new PointD(3, 4)), line));
        Assert.ThrowsExactly<InvalidOperationException>(() => algorithm.PointToLine(a, line, EVisionCoordinateSpace.Local));
        var systemA = System("A"); var systemB = System("B");
        var located = line.InCoordinates(systemA); var point = a.InCoordinates(systemB);
        Assert.ThrowsExactly<InvalidOperationException>(() => algorithm.PointToLine(point, located));
        var changed = TestCoordinates.FromPose("A", "frame", 100, 100, new TemplatePoseTransform(20, 16, new PointD(51, 50), 0, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => algorithm.GenerateLine(located.A, b.InCoordinates(changed)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VisionPoint("frame", new PointD(double.NaN, 0)));
        Assert.ThrowsExactly<OperationCanceledException>(() => algorithm.GenerateLine(a, b, new CancellationToken(true)));
    }
    /// <summary>转换通过同一原图，不重复乘定位矩阵，也不修改原事实。</summary>
    [TestMethod]
    public void ExplicitConversionPreservesImageAndOriginalEvidence()
    {
        var a = System("A", .3, 2); var b = System("B", -.6, .7);
        var original = VisionPoint.Create("frame", new PointD(3, 4), EVisionCoordinateSpace.Local, a);
        var changed = original.InCoordinates(b);
        Assert.AreEqual(original.ImagePosition, changed.ImagePosition);
        Assert.AreSame(a, original.CoordinateSystem); Assert.AreSame(b, changed.CoordinateSystem);
        var expected = b.ImageToLocal.Map(new Coordinate2D(original.ImagePosition.X, original.ImagePosition.Y));
        Assert.AreEqual(expected.X, changed.LocalPosition!.Value.X, 1e-12);
        var imageOnly = changed.InCoordinates(null); Assert.IsNull(imageOnly.LocalPosition);
        Assert.AreEqual(original.ImagePosition, imageOnly.ImagePosition);
        var fit = new RobustLineFitter().Fit("frame", new[] { new PointD(0, 0), new PointD(1, 0), new PointD(2, 0), new PointD(3, 0) }).InCoordinates(a);
        Assert.AreSame(a, fit.MeasuredLine.CoordinateSystem);
        Assert.AreEqual(fit.A, fit.MeasuredA.ImagePosition);
        var circle = new EdgeMeasurementResult("frame", EEdgeModel.Circle, new PointD(20, 20), new PointD(22, 20), 2, 0, 6);
        Assert.IsNull(circle.MeasuredLine);
    }
    private static VisionCoordinateSystem System(string id, double angle = 0, double scale = 1) =>
        TestCoordinates.FromPose(id, "frame", 100, 100, new TemplatePoseTransform(20, 16, new PointD(50, 50), angle, scale));
}
