using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>通用定义、构建、ROI与局部测量的真值验证。</summary>
[TestClass]
public sealed class GeneralCoordinateSystemTests
{
    /// <summary>参考ROI一次保存，下一帧只更换映射。</summary>
    [TestMethod]
    public void ReferenceRoiFollowsDifferentFrameWithoutTemplateOrRedefinition()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(80, 80, EPixelLayout.Gray8), new byte[6400]);
        using var reference = new ImageFrame("reference", image); using var current = new ImageFrame("current", image);
        var definition = new VisionCoordinateDefinition("workpiece", "工件");
        var first = VisionCoordinateBuilder.FromPose(definition, reference, new PointD(10, 20));
        var saved = first.ToLocalGeometry(new RectangleGeometry(new PointD(14, 23), 4, 2));
        var second = VisionCoordinateBuilder.FromPose(definition, current, new PointD(50, 30), Math.PI / 2, 2);
        var roi = (RectangleGeometry)second.ToImageGeometry(saved);
        Assert.AreEqual(44, roi.Center.X, 1e-9); Assert.AreEqual(38, roi.Center.Y, 1e-9);
        Assert.AreEqual(8, roi.Width, 1e-9); Assert.AreEqual(4, roi.Height, 1e-9);
        Assert.AreEqual(Math.PI / 2, roi.Angle, 1e-9);
        second.ValidateDefinition(current, definition.Id, definition.Version, definition.Signature);
        Assert.ThrowsExactly<InvalidOperationException>(() => first.ValidateFrame(current));
        Assert.AreEqual(4, ((RectangleGeometry)saved).Center.X, 1e-9);
    }

    /// <summary>定义变化需要重新确认，同语义来源可互换。</summary>
    [TestMethod]
    public void DefinitionIdentityTracksSemanticsRatherThanNameOrConstructionSource()
    {
        var definition = new VisionCoordinateDefinition("fixture", "夹具");
        Assert.AreEqual(definition.Signature, new VisionCoordinateDefinition("fixture", "新的显示名称").Signature);
        Assert.AreNotEqual(definition.Signature, new VisionCoordinateDefinition("fixture", "夹具", 2).Signature);
        Assert.AreNotEqual(definition.Signature, new VisionCoordinateDefinition("fixture", "夹具", unit: EVisionCoordinateUnit.Millimeter).Signature);
        Assert.AreNotEqual(definition.Signature, new VisionCoordinateDefinition("fixture", "夹具", reference: "template:a").Signature);
        var matrix = CoordinateMatrix2D.FromAffine(1, 0, -20, 0, 1, -10);
        var a = new VisionCoordinateSystem(definition, "frame", 50, 50, matrix, "template");
        var b = new VisionCoordinateSystem(definition, "frame", 50, 50, matrix, "lines");
        Assert.AreEqual(5, new GeometryMeasurer().PointToPoint(new VisionPoint("frame", new PointD(0, 0), a), new VisionPoint("frame", new PointD(3, 4), b), EVisionCoordinateSpace.Local).Distance, 1e-9);
    }

    /// <summary>量纲倍率的两端不应被绝对行列式阈值误判。</summary>
    [TestMethod]
    [DataRow(1e-6)]
    [DataRow(1e6)]
    public void SimilarityScaleLimitsRemainInvertible(double scale)
    {
        var matrix = VisionCoordinateBuilder.PoseMatrix(new PointD(0, 0), .73, scale);
        var p = matrix.Inverse().Map(matrix.Map(new Coordinate2D(3, 4)));
        Assert.AreEqual(3, p.X, 1e-9); Assert.AreEqual(4, p.Y, 1e-9);
    }

    /// <summary>双点及交线构建与同帧、退化检查。</summary>
    [TestMethod]
    public void TwoPointsAndLinesProduceKnownBasisAndRejectDegeneracy()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(80, 80, EPixelLayout.Gray8), new byte[6400]); using var frame = new ImageFrame("frame", image);
        var definition = new VisionCoordinateDefinition("part", "工件", unit: EVisionCoordinateUnit.Millimeter);
        VisionPoint P(double x, double y) => new VisionPoint(frame.FrameId, new PointD(x, y));
        var system = VisionCoordinateBuilder.FromTwoPoints(definition, frame, P(10, 20), P(10, 30), 5);
        Assert.AreEqual(2, system.SimilarityScale, 1e-9); Assert.AreEqual(Math.PI / 2, system.RotationRadians, 1e-9);
        var q = system.LocalToImage.Map(new Coordinate2D(3, 4)); Assert.AreEqual(2, q.X, 1e-9); Assert.AreEqual(26, q.Y, 1e-9);
        var axis = new VisionLine(P(5, 20), P(15, 20)); var cross = new VisionLine(P(10, 0), P(10, 30));
        var intersection = VisionCoordinateBuilder.FromLines(definition, frame, axis, cross, 2);
        Assert.AreEqual(10, intersection.LocalToImage.Tx, 1e-9); Assert.AreEqual(20, intersection.LocalToImage.Ty, 1e-9);
        Assert.ThrowsExactly<ArgumentException>(() => VisionCoordinateBuilder.FromTwoPoints(definition, frame, P(1, 1), P(1, 1), 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => VisionCoordinateBuilder.FromTwoPoints(definition, frame, P(1, 1), new VisionPoint("other", new PointD(2, 2)), 1));
        Assert.ThrowsExactly<ArgumentException>(() => VisionCoordinateBuilder.FromLines(definition, frame, axis, new VisionLine(P(5, 30), P(15, 30))));
    }

    /// <summary>父子组合不重复补偿。</summary>
    [TestMethod]
    public void ParentCompositionAppliesEachMatrixOnce()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(80, 80, EPixelLayout.Gray8), new byte[6400]); using var frame = new ImageFrame("frame", image);
        var parent = VisionCoordinateBuilder.FromPose(new VisionCoordinateDefinition("parent", "父"), frame, new PointD(30, 40), Math.PI / 2, 2);
        var child = VisionCoordinateBuilder.FromParent(new VisionCoordinateDefinition("child", "业务"), frame, parent, VisionCoordinateBuilder.PoseMatrix(new PointD(3, 4), 0, 1));
        var origin = child.LocalToImage.Map(new Coordinate2D(0, 0)); Assert.AreEqual(22, origin.X, 1e-9); Assert.AreEqual(46, origin.Y, 1e-9);
        var q = child.LocalToImage.Map(new Coordinate2D(2, 0)); Assert.AreEqual(22, q.X, 1e-9); Assert.AreEqual(50, q.Y, 1e-9);
    }

    /// <summary>仿射ROI精确性及所选空间距离真值。</summary>
    [TestMethod]
    public void AffineRoisPreserveMembershipAndLocalProjectionUsesLocalMetric()
    {
        var system = new VisionCoordinateSystem(new VisionCoordinateDefinition("measure", "测量", unit: EVisionCoordinateUnit.Millimeter), "frame", 80, 80,
            CoordinateMatrix2D.FromAffine(2, 1, 20, 0, 1, 30));
        Assert.IsFalse(system.IsSimilarity); Assert.ThrowsExactly<NotSupportedException>(() => _ = system.SimilarityScale);
        Geometry[] shapes = { new RectangleGeometry(new PointD(0, 0), 6, 4, .2), new EllipseGeometry(new PointD(1, 2), 3, 2, .4) };
        Assert.IsInstanceOfType<ContourGeometry>(system.ToImageGeometry(shapes[0]));
        foreach (var shape in shapes)
        {
            var mapped = system.ToImageGeometry(shape); var restored = system.ToLocalGeometry(mapped);
            for (int x = -8; x < 10; x++) for (int y = -8; y < 10; y++)
            {
                var p = new PointD(x * .43, y * .37); var q = system.LocalToImage.Map(new Coordinate2D(p.X, p.Y));
                Assert.AreEqual(shape.Contains(p), mapped.Contains(new PointD(q.X, q.Y)));
                Assert.AreEqual(shape.Contains(p), restored.Contains(p));
            }
        }
        VisionPoint P(double x, double y) => VisionPoint.Create("frame", new PointD(x, y), EVisionCoordinateSpace.Local, system);
        var line = new VisionLine(P(0, 0), P(5, 0)); var point = P(1, 2); var measurer = new GeometryMeasurer();
        var local = measurer.PointToLine(point, line, EVisionCoordinateSpace.Local);
        Assert.AreEqual(2, local.Distance, 1e-9); Assert.AreEqual("mm", local.Unit);
        Assert.AreEqual(1, local.B.LocalPosition!.Value.X, 1e-9); Assert.AreEqual(22, local.B.ImagePosition.X, 1e-9);
        var original = measurer.PointToLine(point, line);
        Assert.AreEqual(24, original.B.ImagePosition.X, 1e-9); // 原图垂足与局部垂足不同。
    }

    /// <summary>拟合恢复已知矩阵并拒绝共线。</summary>
    [TestMethod]
    public void CorrespondencesRecoverKnownAffineAndRejectCollinearPoints()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(80, 80, EPixelLayout.Gray8), new byte[6400]); using var frame = new ImageFrame("frame", image);
        var definition = new VisionCoordinateDefinition("calibrated", "标定", unit: EVisionCoordinateUnit.Millimeter);
        var samples = new[] { new Coordinate2D(0, 0), new Coordinate2D(10, 0), new Coordinate2D(0, 10), new Coordinate2D(4, 7) }
            .Select(p => new CalibrationSample(p, new Coordinate2D(2 * p.X + p.Y + 10, 3 * p.Y + 20))).ToArray();
        var system = VisionCoordinateBuilder.FromCorrespondences(definition, frame, samples, out double rms);
        Assert.AreEqual(0, rms, 1e-9); Assert.AreEqual(2, system.LocalToImage.M11, 1e-9); Assert.AreEqual(1, system.LocalToImage.M12, 1e-9); Assert.AreEqual(3, system.LocalToImage.M22, 1e-9);
        Assert.ThrowsExactly<InvalidOperationException>(() => VisionCoordinateBuilder.FromCorrespondences(definition, frame,
            Enumerable.Range(0, 3).Select(i => new CalibrationSample(new Coordinate2D(i, 0), new Coordinate2D(i, 0))).ToArray(), out _));
        Assert.ThrowsExactly<ArgumentException>(() => CoordinateMatrix2D.FromAffine(1, 2, 0, 2, 4, 0));
    }
}
