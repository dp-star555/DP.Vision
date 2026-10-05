using System;
using System.Linq;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>定位坐标系的矩阵、精确范围和双坐标事实验收。</summary>
[TestClass]
public sealed class VisionCoordinateSystemTests
{
    /// <summary>真实栅格化与逆映射真值一致，不能用外接框替代。</summary>
    [TestMethod]
    [DataRow(0d, 1d)]
    [DataRow(Math.PI / 2, 1d)]
    [DataRow(Math.PI / 4, 1.5d)]
    [DataRow(-.73d, .6d)]
    public void RegionFollowsPose_AndPreservesHole(double angle, double scale)
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), new byte[4096]);
        using var frame = new ImageFrame("current", image);
        var pose = new TemplatePoseTransform(20, 16, new PointD(32, 32), angle, scale);
        var system = TestCoordinates.FromPose("part", frame.FrameId, 64, 64, pose);
        var include = new RectangleGeometry(new PointD(12, 8), 14, 10, .2);
        var hole = new EllipseGeometry(new PointD(12, 8), 2, 2);
        var region = system.ResolveRegion(frame, new Geometry[] { include }, new Geometry[] { hole });
        Assert.IsTrue(region.AreaPixels > 0);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            var p = new PointD(x + .5, y + .5); var local = pose.ToTemplate(new Coordinate2D(p.X, p.Y));
            var q = new PointD(local.X, local.Y);
            Assert.AreEqual(include.Contains(q) && !hole.Contains(q), region.Contains(p), $"{x},{y}");
        }
        var original = (RectangleGeometry)system.ToLocalGeometry(system.ToImageGeometry(include));
        Assert.AreEqual(include.Center.X, original.Center.X, 1e-10); Assert.AreEqual(include.Center.Y, original.Center.Y, 1e-10);
        Assert.AreEqual(include.Width, original.Width, 1e-10); Assert.AreEqual(include.Angle, original.Angle, 1e-10);
        var mapped = system.LocalToImage.Map(new Coordinate2D(2.25, 3.75));
        var expected = pose.ToImage(new Coordinate2D(2.25, 3.75));
        Assert.AreEqual(expected.X, mapped.X, 1e-10); Assert.AreEqual(expected.Y, mapped.Y, 1e-10);
        var back = system.ImageToLocal.Map(mapped); Assert.AreEqual(2.25, back.X, 1e-10); Assert.AreEqual(3.75, back.Y, 1e-10);
    }

    /// <summary>独立矩阵契约可表达非平移变换且能反演；退化矩阵须明确拒绝。</summary>
    [TestMethod]
    public void AffineMatrixRoundTripsAndRejectsSingularInput()
    {
        var matrix = CoordinateMatrix2D.FromAffine(0, -2, 30, 2, 0, 8);
        var actual = matrix.Map(new Coordinate2D(3, 4));
        Assert.AreEqual(22d, actual.X);
        Assert.AreEqual(14d, actual.Y);
        var restored = matrix.Inverse().Map(actual);
        Assert.AreEqual(3d, restored.X, 1e-10);
        Assert.AreEqual(4d, restored.Y, 1e-10);
        Assert.ThrowsExactly<ArgumentException>(() => CoordinateMatrix2D.FromAffine(1, 2, 0, 2, 4, 0));
        var unchanged = CoordinateMatrix2D.Identity.Map(new Coordinate2D(5, 9));
        Assert.AreEqual(5d, unchanged.X);
        Assert.AreEqual(9d, unchanged.Y);
    }

    /// <summary>通用仿射矩阵将矩形变为四边形，不能用外接矩形吞掉不属于ROI的像素。</summary>
    [TestMethod]
    public void AffineShearMapsRoiToFilledContour()
    {
        var source = new RectangleGeometry(new PointD(4, 4), 4, 4);
        var matrix = CoordinateMatrix2D.FromAffine(1, 1, 0, 0, 1, 0);
        var mapped = matrix.MapGeometry(source);
        Assert.IsTrue(mapped.Closed);
        Assert.IsTrue(mapped.Filled);
        Assert.AreEqual(4, mapped.Points.Count);
        Assert.IsTrue(mapped.Contains(new PointD(8, 4)));
        Assert.IsFalse(mapped.Contains(new PointD(4.1, 4))); // 在外接框内，但不属于剪切后的ROI
        var restored = matrix.Inverse().MapGeometry(mapped);
        Assert.IsTrue(restored.Contains(new PointD(4, 4)));
        Assert.ThrowsExactly<NotSupportedException>(() => matrix.MapGeometry(new EllipseGeometry(new PointD(4, 4), 2, 2)));
    }

    /// <summary>通用仿射采样支持剪切；无变换使用恒等矩阵，越界不伪造像素。</summary>
    [TestMethod]
    public void AffineSamplerHandlesShearAndIdentity()
    {
        var pixels = Enumerable.Range(0, 64).Select(x => (byte)x).ToArray();
        using var source = VisionImage.CopyFrom(new ImageInfo(8, 8, EPixelLayout.Gray8), pixels);
        using var sheared = AffineImageResampler.Nearest(source,
            CoordinateMatrix2D.FromAffine(1, 1, 0, 0, 1, 0), 4, 4);
        var values = new byte[16];
        sheared.CopyTo(0, values, 0, values.Length);
        Assert.AreEqual((byte)12, values[1 * 4 + 2]);
        using var unchanged = AffineImageResampler.Nearest(source, CoordinateMatrix2D.Identity, 8, 8);
        var roundTrip = new byte[64];
        unchanged.CopyTo(0, roundTrip, 0, roundTrip.Length);
        CollectionAssert.AreEqual(pixels, roundTrip);
        Assert.ThrowsExactly<ArgumentException>(() => AffineImageResampler.Nearest(source,
            CoordinateMatrix2D.FromAffine(1, 0, 7, 0, 1, 0), 4, 4));
    }

    /// <summary>摆正ROI只是展示副本；90度位姿下逐像素与矩阵回映保持一致，原图不改变。</summary>
    [TestMethod]
    public void PreviewRectifiesPose_WithoutChangingOriginalOrLosingCoordinates()
    {
        var pixels = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        using var source = VisionImage.CopyFrom(new ImageInfo(4, 4, EPixelLayout.Gray8), pixels);
        using var frame = new ImageFrame("rotated", source);
        var pose = new TemplatePoseTransform(4, 4, new PointD(2, 2), Math.PI / 2, 1);
        var system = TestCoordinates.FromPose("template", frame.FrameId, 4, 4, pose);
        using var preview = LocatedRoiPreview.Create(frame, system, new PixelBounds(0, 0, 4, 4));
        var actual = new byte[16];
        preview.Image.CopyTo(0, actual, 0, actual.Length);
        CollectionAssert.AreEqual(new byte[] { 3, 7, 11, 15, 2, 6, 10, 14, 1, 5, 9, 13, 0, 4, 8, 12 }, actual);
        Assert.AreEqual(frame.FrameId, preview.SourceFrameId);
        var originalPoint = preview.PreviewToImage.Map(new Coordinate2D(.5, .5));
        Assert.AreEqual(3.5, originalPoint.X, 1e-10);
        Assert.AreEqual(.5, originalPoint.Y, 1e-10);
        var after = new byte[16];
        source.CopyTo(0, after, 0, after.Length);
        CollectionAssert.AreEqual(pixels, after);
    }

    /// <summary>预览不能静默填充越界区域或使用其他帧的定位结果。</summary>
    [TestMethod]
    public void PreviewRejectsOutOfFrameAndForeignLocation()
    {
        using var source = VisionImage.CopyFrom(new ImageInfo(4, 4, EPixelLayout.Gray8), new byte[16]);
        using var frame = new ImageFrame("current", source);
        var outside = TestCoordinates.FromPose("template", frame.FrameId, 4, 4,
            new TemplatePoseTransform(4, 4, new PointD(3.5, 2), 0, 1));
        Assert.ThrowsExactly<ArgumentException>(() => LocatedRoiPreview.Create(frame, outside, new PixelBounds(0, 0, 4, 4)));
        var foreign = TestCoordinates.FromPose("template", "old", 4, 4,
            new TemplatePoseTransform(4, 4, new PointD(2, 2), 0, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => LocatedRoiPreview.Create(frame, foreign, new PixelBounds(0, 0, 4, 4)));
    }

    /// <summary>帧、定义和越界拒绝。</summary>
    [TestMethod]
    public void FrameAndDefinitionIdentity_AreValidated()
    {
        using var a = VisionImage.CopyFrom(new ImageInfo(2, 2, EPixelLayout.Gray8), new byte[] { 1, 2, 3, 4 });
        using var frame = new ImageFrame("a", a); using var other = new ImageFrame("b", a);
        var system = TestCoordinates.FromPose("definition", "a", 2, 2, new TemplatePoseTransform(2, 2, new PointD(1, 1), 0, 1));
        system.ValidateDefinition(frame, "definition", 1, system.Definition.Signature);
        Assert.ThrowsExactly<InvalidOperationException>(() => system.ValidateFrame(other));
        Assert.ThrowsExactly<InvalidOperationException>(() => system.ValidateDefinition(frame, "another", 1, system.Definition.Signature));
        Assert.ThrowsExactly<InvalidOperationException>(() => system.ValidateDefinition(frame, "definition", 1, "changed"));
        Assert.ThrowsExactly<ArgumentException>(() => system.ResolveRegion(frame, Array.Empty<Geometry>(), Array.Empty<Geometry>()));
        var outside = new RectangleGeometry(new PointD(5, 5), 2, 2);
        Assert.ThrowsExactly<ArgumentException>(() => system.ResolveRegion(frame, new[] { outside }, Array.Empty<Geometry>()));
    }

    /// <summary>亚像素卡尺沿定位方向采样，事实保持图像单位并提供局部点。</summary>
    [TestMethod]
    public void CaliperAndLine_HaveExplicitImageAndLocalResults()
    {
        var values = Enumerable.Range(0, 4096).Select(i => (byte)Math.Round(255 / (1 + Math.Exp(-(i / 64 + .5 - 32.25))))).ToArray();
        using var image = VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), values);
        using var frame = new ImageFrame("frame", image);
        var pose = new TemplatePoseTransform(20, 20, new PointD(32, 32), Math.PI / 2, 1.5);
        var system = TestCoordinates.FromPose("part", frame.FrameId, 64, 64, pose);
        var start = system.LocalToImage.Map(new Coordinate2D(0, 10)); var end = system.LocalToImage.Map(new Coordinate2D(20, 10));
        var result = new CaliperMeasurer().Measure(frame, new CaliperOptions(new PointD(start.X, start.Y), new PointD(end.X, end.Y), bandSampleStep: 1.5)).InCoordinates(system);
        Assert.AreEqual(1, result.Count); Assert.AreEqual(32.25, result.Edges[0].Position.Y, .1);
        Assert.AreEqual(10, result.LocatedEdges![0].LocalPosition.Y, 1e-10);
        Assert.AreEqual(10 + .25 / 1.5, result.LocatedEdges[0].LocalPosition.X, .1);
        var line = new RobustLineFitter().Fit(frame.FrameId, new[] { new PointD(20, 32), new PointD(30, 32), new PointD(40, 32) }).InCoordinates(system);
        Assert.AreEqual(10, line.LocatedA!.LocalPosition.X, 1e-10); Assert.AreEqual(0, line.LocalRmsError!.Value, 1e-10);
        var foreign = TestCoordinates.FromPose("part", "other", 64, 64, pose);
        Assert.ThrowsExactly<InvalidOperationException>(() => result.InCoordinates(foreign));
    }
}
