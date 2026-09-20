using System;
using System.Threading;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>精确搜索范围和线圆测量随动。</summary>
[TestClass]
public sealed class LocatedRangeTests
{
    private static readonly byte[] Pattern = { 30, 200, 70, 100, 180, 90, 240, 10, 130, 50, 160, 40, 230, 80, 210 };

    /// <summary>候选中心在ROI内但采样足迹触及孔洞时不得入选。</summary>
    [TestMethod]
    public void BothLocators_RejectFootprintHoles_AndFindNextValidCandidate()
    {
        var pixels = new byte[40 * 30];
        for (int y = 0; y < 3; y++) for (int x = 0; x < 5; x++)
        { pixels[(2 + y) * 40 + 2 + x] = Pattern[y * 5 + x]; pixels[(12 + y) * 40 + 22 + x] = Pattern[y * 5 + x]; }
        using var image = VisionImage.CopyFrom(new ImageInfo(40, 30, EPixelLayout.Gray8), pixels);
        using var templateImage = VisionImage.CopyFrom(new ImageInfo(5, 3, EPixelLayout.Gray8), Pattern);
        using var frame = new ImageFrame("scene", image); using var template = new ImageFrame("template", templateImage);
        var mask = InspectionMask.Compose(image, Array.Empty<Geometry>(), new Geometry[] { new RectangleGeometry(new PointD(2.5, 2.5), 1, 1) });
        var search = new PixelBounds(0, 0, 40, 30);
        var translation = new OpenCvTemplateLocator().Locate(frame, search, template, new PixelBounds(0, 0, 5, 3), .9999, regionMask: mask);
        Assert.IsTrue(translation.Found); Assert.AreEqual(22, translation.Bounds!.Value.X); Assert.AreEqual(12, translation.Bounds.Value.Y);
        var pose = new OpenCvTemplatePoseLocator().Locate(frame, template, search, new TemplatePoseOptions(new[] { 0d }, new[] { 1d }, .9999), regionMask: mask);
        Assert.IsTrue(pose.Found); Assert.AreEqual(24.5, pose.Transform!.Center.X, 1e-6);
        var empty = new RegionGeometry(Array.Empty<RegionRun>());
        Assert.IsFalse(new OpenCvTemplateLocator().Locate(frame, search, template, new PixelBounds(0, 0, 5, 3), 0, regionMask: empty).Found);
        Assert.IsFalse(new OpenCvTemplatePoseLocator().Locate(frame, template, search, new TemplatePoseOptions(new[] { 0d }, new[] { 1d }, 0), regionMask: empty).Found);
        Assert.ThrowsExactly<OperationCanceledException>(() => new OpenCvTemplatePoseLocator().Locate(frame, template, search,
            new TemplatePoseOptions(new[] { 0d }, new[] { 1d }), new CancellationToken(true), mask));
    }

    /// <summary>父姿态下的平移搜索保留实际旋转轮廓，不把外接框冒充模板。</summary>
    [TestMethod]
    public void TranslationInParentPose_ReportsActualGeometryAndLocalCenter()
    {
        var pixels = new byte[40 * 30];
        for (int y = 0; y < 3; y++) for (int x = 0; x < 5; x++) pixels[(10 + x) * 40 + 29 - y] = Pattern[y * 5 + x];
        using var image = VisionImage.CopyFrom(new ImageInfo(40, 30, EPixelLayout.Gray8), pixels);
        using var templateImage = VisionImage.CopyFrom(new ImageInfo(5, 3, EPixelLayout.Gray8), Pattern);
        using var frame = new ImageFrame("scene", image); using var template = new ImageFrame("template", templateImage);
        var parent = new LocatedCoordinateSystem("parent", "signature", frame.FrameId, 40, 30, new TemplatePoseTransform(20, 20, new PointD(20, 15), Math.PI / 2, 1));
        var region = InspectionMask.Compose(image, new Geometry[] { new RectangleGeometry(new PointD(28.5, 12.5), 3, 5) }, Array.Empty<Geometry>());
        var result = new OpenCvTemplateLocator().Locate(frame, new PixelBounds(0, 0, 40, 30), template, new PixelBounds(0, 0, 5, 3), .9999,
            regionMask: region, searchCoordinates: parent);
        Assert.IsTrue(result.Found); Assert.AreEqual(Math.PI / 2, result.Transform!.AngleRadians, 1e-8);
        var geometry = (RectangleGeometry)result.MatchGeometry!;
        Assert.AreEqual(5d, geometry.Width); Assert.AreEqual(3d, geometry.Height); Assert.AreEqual(3, result.Bounds!.Value.Width);
        Assert.AreEqual(7.5, result.LocatedCenter!.LocalPosition.X, 1e-8); Assert.AreEqual(1.5, result.LocatedCenter.LocalPosition.Y, 1e-8);
    }

    /// <summary>部分圆弧来自真实圆边缘，输出原图和局部半径。</summary>
    [TestMethod]
    public void CircleMeasurement_WithExcludedHalfPlaneKeepsLocalRadius()
    {
        var pixels = new byte[4096];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            if (Math.Pow(x + .5 - 32, 2) + Math.Pow(y + .5 - 32, 2) <= 144) pixels[y * 64 + x] = 255;
        using var image = VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), pixels);
        using var frame = new ImageFrame("circle", image);
        var mask = InspectionMask.Compose(image, Array.Empty<Geometry>(), new Geometry[] { new RectangleGeometry(new PointD(15, 32), 30, 64) });
        var system = new LocatedCoordinateSystem("parent", "signature", frame.FrameId, 64, 64, new TemplatePoseTransform(20, 20, new PointD(32, 32), .7, 1.5));
        var result = new OpenCvEdgeMeasurer().Measure(frame, new PixelBounds(0, 0, 64, 64), new EdgeMeasurementOptions(EEdgeModel.Circle), regionMask: mask).InCoordinates(system);
        Assert.AreEqual(32, result.A.X, .7); Assert.AreEqual(32, result.A.Y, .7); Assert.AreEqual(12, result.Radius, .7);
        Assert.AreEqual(result.Radius / 1.5, result.LocalRadius!.Value, 1e-10);
    }

    /// <summary>掩码只筛选真实Canny点，不把排除边界当作灰度边缘。</summary>
    [TestMethod]
    public void EdgeMask_FiltersActualEvidenceWithoutCreatingBoundaryEdges()
    {
        var pixels = new byte[4096];
        for (int y = 0; y < 64; y++) for (int x = 32; x < 64; x++) pixels[y * 64 + x] = 255;
        using var image = VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), pixels);
        using var frame = new ImageFrame("scene", image);
        var include = new RectangleGeometry(new PointD(31.5, 32), 20, 20, Math.PI / 4);
        var mask = InspectionMask.Compose(image, new Geometry[] { include }, new Geometry[] { new RectangleGeometry(new PointD(32, 32), 30, 4) });
        var measured = new OpenCvEdgeMeasurer().Measure(frame, new PixelBounds(0, 0, 64, 64), new EdgeMeasurementOptions(EEdgeModel.Line), regionMask: mask);
        Assert.AreEqual(31.5, measured.A.X, 1e-8); Assert.AreEqual(31.5, measured.B.X, 1e-8); Assert.IsTrue(measured.PointCount < 30);
        var parent = new LocatedCoordinateSystem("parent", "signature", frame.FrameId, 64, 64, new TemplatePoseTransform(64, 64, new PointD(32, 32), .5, 2));
        var located = measured.InCoordinates(parent); Assert.AreEqual(measured.A.X, located.LocatedA!.ImagePosition.X);
        using var blankImage = VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), new byte[4096]);
        using var blank = new ImageFrame("blank", blankImage);
        Assert.ThrowsExactly<InvalidOperationException>(() => new OpenCvEdgeMeasurer().Measure(blank, new PixelBounds(0, 0, 64, 64),
            new EdgeMeasurementOptions(EEdgeModel.Line), regionMask: mask));
    }
}
