using System;
using System.Linq;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>按灰度工作的算法不隐式转换彩色：彩色或16位输入明确报错；显式转换后正常运行。</summary>
[TestClass]
public sealed class GrayOnlyInputTests
{
    [TestMethod]
    [DataRow(EPixelLayout.Bgr24)]
    [DataRow(EPixelLayout.Rgb24)]
    [DataRow(EPixelLayout.Bgra32)]
    [DataRow(EPixelLayout.Gray16)]
    public void GrayOnlyAlgorithms_RejectOtherLayouts(EPixelLayout layout)
    {
        var info = new ImageInfo(32, 24, layout);
        using var image = VisionImage.CopyFrom(info, new byte[info.ByteLength]);
        using var frame = new ImageFrame("frame", image);
        using var grayTemplate = VisionImage.CopyFrom(new ImageInfo(4, 4, EPixelLayout.Gray8), new byte[16]);
        using var template = new ImageFrame("template", grayTemplate);
        var bounds = new PixelBounds(0, 0, 32, 24);
        var errors = new Action[]
        {
            () => new OpenCvTemplateLocator().Locate(frame, bounds, template, new PixelBounds(0, 0, 4, 4)),
            () => new OpenCvTemplatePoseLocator().Locate(frame, template, bounds, new TemplatePoseOptions(0, 0)),
            () => new OpenCvEdgeMeasurer().Measure(frame, bounds, new EdgeMeasurementOptions(EEdgeModel.Line)),
            () => new OpenCvBlobAnalyzer().Analyze(frame, bounds, new BlobOptions(0, 10)),
            () => new OpenCvRegionProcessor().Threshold(frame, bounds, 0, 10),
            () => new CaliperMeasurer().Measure(frame, new CaliperOptions(new PointD(2.5, 12.5), new PointD(29.5, 12.5)))
        };
        foreach (var error in errors)
            StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(error).Message, "只支持8位灰度图像");
    }

    [TestMethod]
    public void ToGray8_UsesLuminanceAndRejectsGray16()
    {
        // 红、绿、蓝、白四个像素，BGR与RGB两种通道顺序得到相同灰度。
        byte[] rgb = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 };
        byte[] bgr = { 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255 };
        using var fromRgb = VisionImage.ToGray8(VisionImage.CopyFrom(new ImageInfo(4, 1, EPixelLayout.Rgb24), rgb));
        using var fromBgr = VisionImage.ToGray8(VisionImage.CopyFrom(new ImageInfo(4, 1, EPixelLayout.Bgr24), bgr));
        var a = new byte[4]; var b = new byte[4];
        fromRgb.CopyTo(0, a, 0, 4); fromBgr.CopyTo(0, b, 0, 4);
        CollectionAssert.AreEqual(new byte[] { 76, 150, 29, 255 }, a);
        CollectionAssert.AreEqual(a, b);
        Assert.AreEqual(EPixelLayout.Gray8, fromRgb.Info.Layout);
        using var deep = VisionImage.CopyFrom(new ImageInfo(2, 1, EPixelLayout.Gray16), new byte[4]);
        Assert.ThrowsExactly<NotSupportedException>(() => VisionImage.ToGray8(deep));
    }
}
