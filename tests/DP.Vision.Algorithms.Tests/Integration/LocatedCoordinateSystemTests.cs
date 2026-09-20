using System;
using System.Linq;
using System.Threading;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>定位坐标系的矩阵、精确范围和双坐标事实验收。</summary>
[TestClass]
public sealed class LocatedCoordinateSystemTests
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
        var system = new LocatedCoordinateSystem("part", "signature", frame.FrameId, 64, 64, pose);
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

    /// <summary>帧、定义、模板内容、取消和越界拒绝。</summary>
    [TestMethod]
    public void IdentityAndTemplateContent_AreNotFrameAliases()
    {
        using var a = VisionImage.CopyFrom(new ImageInfo(2, 2, EPixelLayout.Gray8), new byte[] { 1, 2, 3, 4 });
        using var b = VisionImage.CopyFrom(new ImageInfo(2, 2, EPixelLayout.Gray8), new byte[] { 1, 2, 3, 5 });
        using var frame = new ImageFrame("a", a); using var other = new ImageFrame("b", a);
        string signature = LocatedCoordinateSystem.ComputeTemplateSignature(a);
        using var retained = a.Retain(); Assert.AreEqual(signature, LocatedCoordinateSystem.ComputeTemplateSignature(retained));
        Assert.AreNotEqual(signature, LocatedCoordinateSystem.ComputeTemplateSignature(b));
        var system = new LocatedCoordinateSystem("definition", signature, "a", 2, 2, new TemplatePoseTransform(2, 2, new PointD(1, 1), 0, 1));
        system.Validate(frame, "definition", signature);
        Assert.ThrowsExactly<InvalidOperationException>(() => system.Validate(other, "definition", signature));
        Assert.ThrowsExactly<InvalidOperationException>(() => system.Validate(frame, "another", signature));
        Assert.ThrowsExactly<InvalidOperationException>(() => system.Validate(frame, "definition", "changed"));
        Assert.ThrowsExactly<ArgumentException>(() => system.ResolveRegion(frame, Array.Empty<Geometry>(), Array.Empty<Geometry>()));
        Assert.ThrowsExactly<OperationCanceledException>(() => LocatedCoordinateSystem.ComputeTemplateSignature(a, new CancellationToken(true)));
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
        var system = new LocatedCoordinateSystem("part", "signature", frame.FrameId, 64, 64,
            new TemplatePoseTransform(20, 20, new PointD(32, 32), Math.PI / 2, 1.5));
        var start = system.LocalToImage.Map(new Coordinate2D(0, 10)); var end = system.LocalToImage.Map(new Coordinate2D(20, 10));
        var result = new CaliperMeasurer().Measure(frame, new CaliperOptions(new PointD(start.X, start.Y), new PointD(end.X, end.Y), bandSampleStep: 1.5)).InCoordinates(system);
        Assert.AreEqual(1, result.Count); Assert.AreEqual(32.25, result.Edges[0].Position.Y, .1);
        Assert.AreEqual(10, result.LocatedEdges![0].LocalPosition.Y, 1e-10);
        Assert.AreEqual(10 + .25 / 1.5, result.LocatedEdges[0].LocalPosition.X, .1);
        var line = new RobustLineFitter().Fit(frame.FrameId, new[] { new PointD(20, 32), new PointD(30, 32), new PointD(40, 32) }).InCoordinates(system);
        Assert.AreEqual(10, line.LocatedA!.LocalPosition.X, 1e-10); Assert.AreEqual(0, line.LocalRmsError!.Value, 1e-10);
        var foreign = new LocatedCoordinateSystem("part", "signature", "other", 64, 64, system.Pose);
        Assert.ThrowsExactly<InvalidOperationException>(() => result.InCoordinates(foreign));
    }
}
