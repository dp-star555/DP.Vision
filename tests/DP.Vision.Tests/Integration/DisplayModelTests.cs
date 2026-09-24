using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>显示模型：邮箱关闭语义、视口缩放范围、参数名与标识校验。</summary>
[TestClass]
public sealed class DisplayModelTests
{
    /// <summary>画布关闭后生产者仍提交预览时按文档返回false，不抛异常。</summary>
    [TestMethod]
    public void ClosedMailboxRejectsWithoutThrowing()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[1]);
        using var frame = new CanvasFrame("a", 1, image);
        var box = new LatestFrameMailbox();
        box.Dispose();
        Assert.IsFalse(box.Post(frame));
        Assert.IsNull(box.Take());
    }

    /// <summary>极小图像适配窗口后不超过缩放上限，随后放大不会反而缩小。</summary>
    [TestMethod]
    public void FitStaysWithinZoomRange()
    {
        var view = new CanvasViewport();
        view.Fit(2, 2, 1000, 700);
        Assert.AreEqual(128.0, view.Scale);
        view.Zoom(1.25, new PointD(500, 350));
        Assert.AreEqual(128.0, view.Scale);

        view.Fit(1000000, 1, 100, 100);
        Assert.AreEqual(1.0 / 1024, view.Scale);
    }

    /// <summary>各项校验报告实际出错的参数名。</summary>
    [TestMethod]
    public void ValidationReportsOffendingParameter()
    {
        Assert.AreEqual(
            "tileSize",
            Assert
                .ThrowsExactly<ArgumentOutOfRangeException>(() => new CanvasOptions(tileSize: 100))
                .ParamName
        );
        Assert.AreEqual(
            "gray16High",
            Assert
                .ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    new CanvasOptions(gray16Low: 9, gray16High: 9)
                )
                .ParamName
        );

        using var image = VisionImage.CopyFrom(new ImageInfo(40, 40, EPixelLayout.Gray8), new byte[1600]);
        using var frame = new CanvasFrame("a", 0, image);
        Assert.AreEqual(
            "y",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.ReadTile(0, 0, 3, 16)).ParamName
        );
        Assert.AreEqual(
            "size",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.ReadTile(0, 0, 0, 8)).ParamName
        );
        Assert.AreEqual(
            "sequence",
            Assert.ThrowsExactly<ArgumentException>(() => new CanvasFrame("a", -1, image)).ParamName
        );
        Assert.AreEqual(
            "view",
            Assert
                .ThrowsExactly<ArgumentNullException>(() =>
                    CanvasPlanning.Tiles(image.Info, null!, 10, 10, 64)
                )
                .ParamName
        );
        Assert.AreEqual(
            "height",
            Assert
                .ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    RegionMask.Tile(new RegionGeometry(Array.Empty<RegionRun>()), 0, 0, 1, 2000)
                )
                .ParamName
        );
    }

    /// <summary>图层标识与其他标识一样限制为256个字符以内。</summary>
    [TestMethod]
    public void LayerIdentityFollowsSharedRule()
    {
        var visuals = Array.Empty<Visual>();
        Assert.AreEqual("x", new CanvasLayer("x", ELayerKind.Roi, visuals).Id);
        Assert.AreEqual(
            "id",
            Assert
                .ThrowsExactly<ArgumentException>(() =>
                    new CanvasLayer(new string('a', 257), ELayerKind.Roi, visuals)
                )
                .ParamName
        );
        Assert.AreEqual(
            "kind",
            Assert
                .ThrowsExactly<ArgumentException>(() => new CanvasLayer("x", (ELayerKind)99, visuals))
                .ParamName
        );
    }

    /// <summary>显示像素的行字节数与布局一致，RGB转换为BGR。</summary>
    [TestMethod]
    public void DisplayPixelsStrideFollowsLayout()
    {
        using var rgb = VisionImage.CopyFrom(
            new ImageInfo(2, 1, EPixelLayout.Rgb24),
            new byte[] { 1, 2, 3, 4, 5, 6 }
        );
        var display = DisplayPixels.From(rgb, new CanvasOptions());
        Assert.AreEqual(EPixelLayout.Bgr24, display.Layout);
        Assert.AreEqual(6, display.Stride);
        CollectionAssert.AreEqual(new byte[] { 3, 2, 1, 6, 5, 4 }, display.Bytes);

        using var gray16 = VisionImage.CopyFrom(new ImageInfo(3, 1, EPixelLayout.Gray16), new byte[6]);
        Assert.AreEqual(3, DisplayPixels.From(gray16, new CanvasOptions()).Stride);

        using var bgra = VisionImage.CopyFrom(new ImageInfo(2, 1, EPixelLayout.Bgra32), new byte[8]);
        var passthrough = DisplayPixels.From(bgra, new CanvasOptions());
        Assert.AreEqual(EPixelLayout.Bgra32, passthrough.Layout);
        Assert.AreEqual(8, passthrough.Stride);
    }
}
