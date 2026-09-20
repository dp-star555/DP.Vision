using System;
using System.Linq;
using System.Threading;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>新增算子的合成真值、边界和资源契约回归。</summary>
[TestClass]
public sealed class OperatorExpansionTests
{
    /// <summary>位深映射、独立租约和取消。</summary>
    [TestMethod]
    public void Preprocessing_PreservesSourceAndUsesExplicitDepthMapping()
    {
        var processor = new OpenCvImagePreprocessor();
        using var source = VisionImage.CopyFrom(new ImageInfo(3, 1, EPixelLayout.Gray16), new byte[] { 0, 0, 0, 128, 255, 255 });
        Assert.ThrowsExactly<NotSupportedException>(() => processor.Process(source, new ImagePreprocessingOptions(EImagePreprocessing.Grayscale)));
        using var converted = processor.Process(source, new ImagePreprocessingOptions(EImagePreprocessing.Gray16ToGray8, gain: 1d / 257));
        CollectionAssert.AreEqual(new byte[] { 0, 128, 255 }, Pixels(converted));
        using var inverted = processor.Process(converted, new ImagePreprocessingOptions(EImagePreprocessing.Invert));
        CollectionAssert.AreEqual(new byte[] { 255, 127, 0 }, Pixels(inverted));
        inverted.Dispose(); CollectionAssert.AreEqual(new byte[] { 0, 128, 255 }, Pixels(converted));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 128, 255, 255 }, Pixels(source));
        Assert.ThrowsExactly<OperationCanceledException>(() => processor.Process(source, new ImagePreprocessingOptions(EImagePreprocessing.Gray16ToGray8), new CancellationToken(true)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImagePreprocessingOptions(EImagePreprocessing.Gaussian, kernelSize: 4));
    }

    /// <summary>真实滤波与饱和增益。</summary>
    [TestMethod]
    public void Preprocessing_RealGaussianMedianAndSaturatingGain()
    {
        var values = new byte[25]; values[12] = 255;
        using var source = VisionImage.CopyFrom(new ImageInfo(5, 5, EPixelLayout.Gray8), values);
        var processor = new OpenCvImagePreprocessor();
        using var median = processor.Process(source, new ImagePreprocessingOptions(EImagePreprocessing.Median));
        Assert.IsTrue(Pixels(median).All(x => x == 0));
        using var gaussian = processor.Process(source, new ImagePreprocessingOptions(EImagePreprocessing.Gaussian));
        var pixels = Pixels(gaussian); Assert.IsTrue(pixels[12] > 0 && pixels[12] < 255); Assert.AreEqual(pixels[11], pixels[13]);
        using var gain = processor.Process(source, new ImagePreprocessingOptions(EImagePreprocessing.GainOffset, gain: 2, offset: 10));
        Assert.AreEqual((byte)255, Pixels(gain)[12]); Assert.AreEqual((byte)10, Pixels(gain)[0]);
    }

    /// <summary>精确孔洞、边界、空区域和身份。</summary>
    [TestMethod]
    public void Region_HolesMorphologyBorderAndIdentityAreExplicit()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(3, 3, EPixelLayout.Gray8), new byte[] { 255,255,255,255,0,255,255,255,255 });
        using var frame = new ImageFrame("ring", image);
        var processor = new OpenCvRegionProcessor();
        var ring = processor.Threshold(frame, new PixelBounds(0, 0, 3, 3), 255, 255);
        Assert.AreEqual(8L, ring.Area); Assert.IsFalse(ring.Region.Contains(new PointD(1.5, 1.5)));
        var filled = processor.Morphology(ring, ERegionMorphology.FillHoles);
        Assert.AreEqual(9L, filled.Area); Assert.AreEqual("ring", filled.FrameId);
        var eroded = processor.Morphology(filled, ERegionMorphology.Erode);
        Assert.AreEqual(1L, eroded.Area); Assert.IsTrue(eroded.Region.Contains(new PointD(1.5, 1.5)));
        Assert.AreEqual(0L, processor.Morphology(eroded, ERegionMorphology.Open).Area);
        Assert.AreEqual(9L, processor.Morphology(eroded, ERegionMorphology.Dilate).Area);
        Assert.AreEqual(8L, processor.Morphology(ring, ERegionMorphology.Close, radius: 0).Area);
        using var other = new ImageFrame("other", image);
        Assert.ThrowsExactly<ArgumentException>(() => ring.ValidateFrame(other));
        var empty = processor.Threshold(frame, new PixelBounds(1, 1, 1, 1), 255, 255);
        Assert.AreEqual(0L, empty.Area); Assert.AreEqual(0L, processor.Morphology(empty, ERegionMorphology.FillHoles).Area);
        Assert.ThrowsExactly<OperationCanceledException>(() => processor.Morphology(ring, ERegionMorphology.Dilate, token: new CancellationToken(true)));
    }

    /// <summary>交集不得替换为外接框。</summary>
    [TestMethod]
    public void Region_IntersectionPreservesExclusionsAndOriginalCoordinates()
    {
        var a = new RegionGeometry(new[] { new RegionRun(2, 1, 8), new RegionRun(3, 1, 8) });
        var b = new RegionGeometry(new[] { new RegionRun(2, 2, 3), new RegionRun(2, 5, 7), new RegionRun(3, 2, 4) });
        var result = RegionAnalysisResult.Intersect(a, b);
        Assert.AreEqual(5L, result.AreaPixels); Assert.IsFalse(result.Contains(new PointD(4.5, 2.5)));
        Assert.IsTrue(result.Contains(new PointD(5.5, 2.5))); Assert.AreEqual(2, result.Runs[0].Row);
    }

    /// <summary>栅格几何真值与筛选语义。</summary>
    [TestMethod]
    public void BlobFeatures_ExactCellPerimeterMomentsAndSelection()
    {
        var rectangle = new BlobObservation(new RegionGeometry(new[] { new RegionRun(10, 20, 24), new RegionRun(11, 20, 24) }));
        Assert.AreEqual(8L, rectangle.Area); Assert.AreEqual(12L, rectangle.Features.GridPerimeter);
        Assert.AreEqual(2d, rectangle.Features.Elongation, 1e-10); Assert.AreEqual(0d, rectangle.Features.OrientationRadians, 1e-10);
        var ring = new BlobObservation(new RegionGeometry(new[] { new RegionRun(0, 0, 3), new RegionRun(1, 0, 1), new RegionRun(1, 2, 3), new RegionRun(2, 0, 3) }));
        Assert.AreEqual(16L, ring.Features.GridPerimeter);
        var split = new BlobObservation(new RegionGeometry(new[] { new RegionRun(0, 0, 1), new RegionRun(0, 1, 3) }));
        Assert.AreEqual(8L, split.Features.GridPerimeter); Assert.AreEqual(3d, split.Features.Elongation, 1e-10);
        var selector = new BlobSelector(); var observations = new BlobAnalysisResult("frame", new[] { rectangle, ring });
        var selected = selector.Select(observations, new BlobSelectionOptions(maximumElongation: 1.1));
        Assert.AreEqual(1, selected.Count); Assert.AreSame(ring, selected.Blobs[0]); Assert.AreEqual("frame", selected.FrameId);
        Assert.AreEqual(0, selector.Select(observations, new BlobSelectionOptions(minimumArea: 100)).Count);
    }

    /// <summary>非整数边缘插值及方向反转。</summary>
    [TestMethod]
    public void Caliper_InterpolatesNonintegerEdgeAndPreservesPolarity()
    {
        var pixels = new byte[64 * 12]; const double edgeX = 20.25;
        for (int y = 0; y < 12; y++) for (int x = 0; x < 64; x++) pixels[y * 64 + x] = (byte)Math.Round(255 / (1 + Math.Exp(-(x + .5 - edgeX) / 1.2)));
        using var image = VisionImage.CopyFrom(new ImageInfo(64, 12, EPixelLayout.Gray8), pixels); using var frame = new ImageFrame("edge", image);
        var measurer = new CaliperMeasurer();
        var forward = measurer.Measure(frame, new CaliperOptions(new PointD(.5, 5.5), new PointD(63.5, 5.5), polarity: ECaliperPolarity.Rising));
        Assert.AreEqual(1, forward.Count); Assert.AreEqual(edgeX, forward.Edges[0].Position.X, .15);
        Assert.IsTrue(Math.Abs(forward.Edges[0].Position.X - Math.Round(forward.Edges[0].Position.X)) > .05);
        Assert.AreEqual(64, forward.Profile.Count); Assert.AreEqual("edge", forward.FrameId);
        var reverse = measurer.Measure(frame, new CaliperOptions(new PointD(63.5, 5.5), new PointD(.5, 5.5), polarity: ECaliperPolarity.Falling));
        Assert.AreEqual(1, reverse.Count); Assert.AreEqual(forward.Edges[0].Position.X, reverse.Edges[0].Position.X, 1e-9);
        Assert.IsTrue(reverse.Edges[0].Gradient < 0);
        Assert.AreEqual(0, measurer.Measure(frame, new CaliperOptions(new PointD(.5, 5.5), new PointD(63.5, 5.5), polarity: ECaliperPolarity.Falling)).Count);
        Assert.ThrowsExactly<ArgumentException>(() => measurer.Measure(frame, new CaliperOptions(new PointD(.5, .5), new PointD(63.5, .5), halfWidth: 1)));
        for (int y = 0; y < 12; y++) for (int x = 0; x < 64; x++)
            pixels[y * 64 + x] = (byte)Math.Round(255 * (1 / (1 + Math.Exp(-(x + .5 - 20.25) / 1.2)) - 1 / (1 + Math.Exp(-(x + .5 - 42.75) / 1.2))));
        using var stripeImage = VisionImage.CopyFrom(new ImageInfo(64, 12, EPixelLayout.Gray8), pixels);
        using var stripe = new ImageFrame("stripe", stripeImage);
        var pair = measurer.Measure(stripe, new CaliperOptions(new PointD(.5, 5.5), new PointD(63.5, 5.5)));
        Assert.AreEqual(2, pair.Count); Assert.IsTrue(pair.Edges[0].Gradient > 0 && pair.Edges[1].Gradient < 0);
        Assert.AreEqual(22.5, pair.Edges[1].Distance - pair.Edges[0].Distance, .3);
    }

    /// <summary>离群点、重复性、退化和预算。</summary>
    [TestMethod]
    public void RobustLine_RejectsOutliersAndDegeneracyDeterministically()
    {
        var points = Enumerable.Range(0, 20).Select(i => new PointD(i, 2 * i + 3)).Concat(new[] { new PointD(4, 100), new PointD(9, -100) }).ToArray();
        var fitter = new RobustLineFitter(); var line = fitter.Fit("line", points, .1);
        Assert.AreEqual(20, line.InlierCount); Assert.AreEqual(0d, line.RmsError, 1e-10);
        Assert.AreEqual(2 * line.A.X + 3, line.A.Y, 1e-10); Assert.AreEqual(2 * line.B.X + 3, line.B.Y, 1e-10);
        CollectionAssert.AreEqual(line.InlierIndices.ToArray(), fitter.Fit("line", points, .1).InlierIndices.ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => fitter.Fit("same", Enumerable.Repeat(new PointD(1, 1), 4).ToArray()));
        Assert.ThrowsExactly<InvalidOperationException>(() => fitter.Fit("isotropic", new[] { new PointD(0, 0), new PointD(1, 0), new PointD(1, 1), new PointD(0, 1) }, distanceThreshold: 10));
        Assert.ThrowsExactly<ArgumentException>(() => fitter.Fit("budget", points, iterations: 1025));
        Assert.ThrowsExactly<OperationCanceledException>(() => fitter.Fit("cancel", points, token: new CancellationToken(true)));
    }

    /// <summary>用独立逆映射合成旋转/尺度真值，验证匹配及正反坐标变换。</summary>
    [TestMethod]
    [DataRow(1d, 0d)]
    [DataRow(1d, Math.PI / 2)]
    [DataRow(2d, Math.PI / 2)]
    [DataRow(1d, Math.PI / 4)]
    public void Pose_SearchesRotationScaleAndMapsCoordinates(double scale, double angle)
    {
        var pixels = new byte[] { 30,200,70,100,180,90,255,10,130,50,160,40,230,80,210 };
        using var templateImage = VisionImage.CopyFrom(new ImageInfo(5, 3, EPixelLayout.Gray8), pixels);
        using var template = new ImageFrame("template", templateImage);
        int w = (int)Math.Ceiling(scale * (Math.Abs(Math.Cos(angle)) * 5 + Math.Abs(Math.Sin(angle)) * 3) - 1e-10);
        int h = (int)Math.Ceiling(scale * (Math.Abs(Math.Sin(angle)) * 5 + Math.Abs(Math.Cos(angle)) * 3) - 1e-10);
        var expected = new TemplatePoseTransform(5, 3, new PointD(12 + w / 2d, 10 + h / 2d), angle, scale);
        var image = Enumerable.Repeat((byte)5, 32 * 32).ToArray();
        for (int y = 10; y < 10 + h; y++) for (int x = 12; x < 12 + w; x++)
        {
            var p = expected.ToTemplate(new Coordinate2D(x + .5, y + .5));
            if (p.X < 0 || p.X >= 5 || p.Y < 0 || p.Y >= 3) continue; // 空白角保持非模板背景，不能参与分数。
            double sx = Math.Max(0, Math.Min(4, p.X - .5)), sy = Math.Max(0, Math.Min(2, p.Y - .5));
            int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy), nx = Math.Min(4, ix + 1), ny = Math.Min(2, iy + 1);
            double fx = sx - ix, fy = sy - iy;
            image[y * 32 + x] = (byte)Math.Round((pixels[iy * 5 + ix] * (1 - fx) + pixels[iy * 5 + nx] * fx) * (1 - fy)
                + (pixels[ny * 5 + ix] * (1 - fx) + pixels[ny * 5 + nx] * fx) * fy);
        }
        using var source = VisionImage.CopyFrom(new ImageInfo(32, 32, EPixelLayout.Gray8), image); using var frame = new ImageFrame("scene", source);
        var locator = new OpenCvTemplatePoseLocator();
        var result = locator.Locate(frame, template, new PixelBounds(0, 0, 32, 32), new TemplatePoseOptions(new[] { 0d, Math.PI / 2, Math.PI / 4 }, new[] { 1d, 2d }, .999));
        Assert.IsTrue(result.Found); Assert.AreEqual(angle, result.Transform!.AngleRadians, 1e-10); Assert.AreEqual(scale, result.Transform.Scale, 1e-10);
        Assert.AreEqual(expected.Center.X, result.Transform.Center.X, 1e-10); Assert.AreEqual(expected.Center.Y, result.Transform.Center.Y, 1e-10);
        var point = new Coordinate2D(1.25, .75); var restored = result.Transform.ToTemplate(result.Transform.ToImage(point));
        Assert.AreEqual(point.X, restored.X, 1e-10); Assert.AreEqual(point.Y, restored.Y, 1e-10);
        Assert.ThrowsExactly<InvalidOperationException>(() => locator.Locate(frame, template, new PixelBounds(0, 0, 32, 32),
            new TemplatePoseOptions(new[] { 0d }, new[] { 1d }, maximumWork: 1)));
        var absent = locator.Locate(frame, template, new PixelBounds(0, 0, 6, 6), new TemplatePoseOptions(new[] { 0d }, new[] { 1d }, .999));
        Assert.IsFalse(absent.Found); Assert.IsNull(absent.Transform);
    }

    private static byte[] Pixels(IImageSource source) { var data = new byte[source.Info.ByteLength]; source.CopyTo(0, data, 0, data.Length); return data; }
}
