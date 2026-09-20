using System;
using System.Threading;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.Vision.Algorithms.Tests;

/// <summary>实际像素测量、定位及区域掩码测试。</summary>
[TestClass]
public sealed class MeasurementLocationMaskTests
{
    /// <summary>直线返回原图边缘中心坐标，不返回裁图局部坐标。</summary>
    [TestMethod]
    public void Line_UsesOriginalPixelCenters()
    {
        var pixels = new byte[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 32; x < 64; x++) pixels[y * 64 + x] = 255;
        using var image = VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), pixels);
        using var frame = new ImageFrame("line", image);
        var result = new OpenCvEdgeMeasurer().Measure(frame, new PixelBounds(8, 8, 48, 48), new EdgeMeasurementOptions(EEdgeModel.Line));
        Assert.AreEqual(31.5, result.A.X, .1); Assert.AreEqual(31.5, result.B.X, .1);
        Assert.AreEqual(0, result.RmsError, .01); Assert.IsTrue(result.PointCount > 30);
    }

    /// <summary>实际圆边缘拟合有可复核圆心/半径/RMS。</summary>
    [TestMethod]
    public void Circle_FitsRealEdges()
    {
        using var mat = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(mat, new Point(32, 32), 15, Scalar.All(255), -1);
        var pixels = new byte[4096]; System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        using var image = VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), pixels);
        using var frame = new ImageFrame("circle", image);
        var result = new OpenCvEdgeMeasurer().Measure(frame, new PixelBounds(0, 0, 64, 64), new EdgeMeasurementOptions(EEdgeModel.Circle));
        Assert.AreEqual(32.5, result.A.X, 1); Assert.AreEqual(32.5, result.A.Y, 1);
        Assert.AreEqual(15, result.Radius, 1); Assert.IsTrue(result.RmsError < 1);
    }

    /// <summary>定位只做声明的平移，空检出仍完成。</summary>
    [TestMethod]
    public void Template_ExactPositionAndNormalEmptyResult()
    {
        var pixels = new byte[20 * 20];
        for (int y = 7; y < 12; y++) for (int x = 8; x < 13; x++) pixels[y * 20 + x] = 255;
        var template = new byte[25]; for (int i = 0; i < 25; i++) template[i] = 255;
        using var image = VisionImage.CopyFrom(new ImageInfo(20, 20, EPixelLayout.Gray8), pixels);
        using var pattern = VisionImage.CopyFrom(new ImageInfo(5, 5, EPixelLayout.Gray8), template);
        using var frame = new ImageFrame("image", image); using var reference = new ImageFrame("template", pattern);
        var locator = new OpenCvTemplateLocator();
        var result = locator.Locate(frame, new PixelBounds(3, 2, 16, 16), reference, new PixelBounds(0, 0, 5, 5), .999);
        Assert.IsTrue(result.Found); Assert.AreEqual(8, result.Bounds!.Value.X); Assert.AreEqual(7, result.Bounds.Value.Y);
        using var blankImage = VisionImage.CopyFrom(new ImageInfo(20, 20, EPixelLayout.Gray8), new byte[400]);
        using var blank = new ImageFrame("blank", blankImage);
        var empty = locator.Locate(blank, new PixelBounds(0, 0, 20, 20), reference, new PixelBounds(0, 0, 5, 5), .99);
        Assert.IsFalse(empty.Found); Assert.IsNull(empty.Bounds); Assert.AreEqual(EAlgorithmStatus.Completed, empty.Status);
        Assert.ThrowsExactly<OperationCanceledException>(() => locator.Locate(frame, new PixelBounds(0, 0, 20, 20), reference,
            new PixelBounds(0, 0, 5, 5), token: new CancellationToken(true)));
    }

    /// <summary>空白图像不能伪造测量结果。</summary>
    [TestMethod]
    public void BlankMeasurement_IsRejected()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(16, 16, EPixelLayout.Gray8), new byte[256]);
        using var frame = new ImageFrame("blank", image);
        Assert.ThrowsExactly<InvalidOperationException>(() => new OpenCvEdgeMeasurer().Measure(frame,
            new PixelBounds(0, 0, 16, 16), new EdgeMeasurementOptions(EEdgeModel.Line)));
    }

    /// <summary>包含/排除掩码保留孔洞，不以外接框替代；颜色只统计实际选区。</summary>
    [TestMethod]
    public void Mask_HolesAffectBothBlobAndColor()
    {
        var pixels = new byte[25]; pixels[12] = 255;
        using var image = VisionImage.CopyFrom(new ImageInfo(5, 5, EPixelLayout.Gray8), pixels);
        using var frame = new ImageFrame("masked", image);
        var mask = InspectionMask.Compose(image,
            new Geometry[] { new RectangleGeometry(new PointD(2.5, 2.5), 3, 3) },
            new Geometry[] { new RectangleGeometry(new PointD(2.5, 2.5), 1, 1) });
        var blob = new OpenCvBlobAnalyzer().Analyze(frame, new PixelBounds(0, 0, 5, 5), new BlobOptions(0, 255), regionMask: mask);
        Assert.AreEqual(8L, blob.Blobs[0].Area); Assert.IsFalse(blob.Blobs[0].Region.Contains(new PointD(2.5, 2.5)));
        var color = new RgbColorAnalyzer().Analyze(frame, new PixelBounds(0, 0, 5, 5), regionMask: mask);
        Assert.AreEqual(8L, color.PixelCount); Assert.AreEqual(0d, color.Red);
        Assert.ThrowsExactly<ArgumentException>(() => InspectionMask.Compose(image,
            new Geometry[] { new RectangleGeometry(new PointD(0, 0), 3, 3) }, Array.Empty<Geometry>()));
    }
}
