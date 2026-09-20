using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>可移植所有权、几何及显示规划的行为测试。</summary>
[TestClass]
public sealed class VisionTests
{
    /// <summary>调用方修改数据不能改变源像素。</summary>
    [TestMethod]
    public void ImageIsOwned()
    {
        var pixels = new byte[] { 1, 2 };
        using var image = VisionImage.CopyFrom(new ImageInfo(2, 1, EPixelLayout.Gray8), pixels);
        pixels[0] = 99;
        var read = new byte[2];
        image.CopyTo(0, read, 0, 2);
        Assert.AreEqual((byte)1, read[0]);
    }

    /// <summary>保留的租约在源释放后仍然有效。</summary>
    [TestMethod]
    public void LeaseSurvivesDispose()
    {
        var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[] { 42 });
        using var other = image.Retain();
        image.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => image.Retain());
        var bytes = new byte[1];
        other.CopyTo(0, bytes, 0, 1);
        Assert.AreEqual((byte)42, bytes[0]);
    }

    /// <summary>缓冲池不能覆盖算法或显示仍持有的图像。</summary>
    [TestMethod]
    public void PoolEnforcesReaderLifetime()
    {
        using var pool = new FrameBufferPool(new ImageInfo(2, 1, EPixelLayout.Gray8), 1, 2);
        Assert.IsTrue(pool.TryRent(out var writer));
        writer!.Write(0, new byte[] { 5, 6 }, 0, 2);
        var image = writer.Publish();
        using var other = image.Retain();
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.Write(0, new byte[] { 0 }, 0, 1));
        image.Dispose();
        Assert.IsFalse(pool.TryRent(out _));
        other.Dispose();
        Assert.IsTrue(pool.TryRent(out var again));
        using (again)
        {
            using var cleared = again!.Publish();
            var bytes = new byte[2];
            cleared.CopyTo(0, bytes, 0, 2);
            Assert.AreEqual((byte)0, bytes[0]);
        }
    }

    /// <summary>销毁缓冲池不使活动读取者失效。</summary>
    [TestMethod]
    public void PoolDisposalPreservesOutstandingRead()
    {
        var pool = new FrameBufferPool(new ImageInfo(1, 1, EPixelLayout.Gray8), 1, 1);
        pool.TryRent(out var writer);
        writer!.Write(0, new byte[] { 88 }, 0, 1);
        using var image = writer.Publish();
        pool.Dispose();
        var data = new byte[1];
        image.CopyTo(0, data, 0, 1);
        Assert.AreEqual((byte)88, data[0]);
        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.TryRent(out _));
    }

    /// <summary>任何分配之前先校验预算。</summary>
    [TestMethod]
    public void PoolBudgetRejectsOversize()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new FrameBufferPool(new ImageInfo(16384, 16384, EPixelLayout.Gray8), 4, 1024)
        );
    }

    /// <summary>行读取不能越界。</summary>
    [TestMethod]
    public void CopyRejectsBadRange()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[1]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            image.CopyTo(int.MaxValue, new byte[1], 0, 1)
        );
    }

    /// <summary>只保留最新帧的邮箱及时释放被丢弃的池化帧。</summary>
    [TestMethod]
    public void PreviewDropsOnlyOwnLease()
    {
        using var pool = new FrameBufferPool(new ImageInfo(1, 1, EPixelLayout.Gray8), 2, 2);
        using var box = new LatestFrameMailbox();
        for (int n = 0; n < 100; n++)
        {
            Assert.IsTrue(pool.TryRent(out var writer));
            using (writer)
            {
                using var image = writer!.Publish();
                using var source = image.Retain();
                using var packet = new CanvasFrame("f" + n, n, source);
                Assert.IsTrue(box.Post(packet));
            }
        }

        using var latest = box.Take();
        Assert.AreEqual(99L, latest!.Sequence);
    }

    /// <summary>旧序号不能替换新预览。</summary>
    [TestMethod]
    public void PreviewRejectsOutOfOrder()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[1]);
        using var source = image.Retain();
        using var box = new LatestFrameMailbox();
        using var newer = new CanvasFrame("b", 20, source);
        using var older = new CanvasFrame("a", 19, source);
        Assert.IsTrue(box.Post(newer));
        Assert.IsFalse(box.Post(older));
        using var taken = box.Take();
        Assert.AreEqual("b", taken!.FrameId);
    }

    /// <summary>绑定帧的证据不能静默跨图像标识复用。</summary>
    [TestMethod]
    public void OverlayMismatchRejected()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[1]);
        using var source = image.Retain();
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CanvasFrame("a", 1, source, new GeometryOverlay("b", Array.Empty<CanvasLayer>()))
        );
    }

    /// <summary>并发预览生产者仍保留最大序号。</summary>
    [TestMethod]
    public void ConcurrentPreviewIsBounded()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[1]);
        using var source = image.Retain();
        using var box = new LatestFrameMailbox();
        Parallel.For(
            0,
            200,
            i =>
            {
                using var frame = new CanvasFrame("f" + i, i, source);
                box.Post(frame);
            }
        );
        using var result = box.Take();
        Assert.AreEqual(199L, result!.Sequence);
    }

    /// <summary>Gray8显示保持每像素一个字节。</summary>
    [TestMethod]
    public void GrayDisplayDoesNotExpand()
    {
        using var image = VisionImage.CopyFrom(
            new ImageInfo(2, 1, EPixelLayout.Gray8),
            new byte[] { 0, 255 }
        );
        var display = DisplayPixels.From(image, new CanvasOptions());
        Assert.AreEqual(EPixelLayout.Gray8, display.Layout);
        Assert.AreEqual(2, display.Bytes.Length);
    }

    /// <summary>通道顺序和Alpha语义明确。</summary>
    [TestMethod]
    public void RgbaConvertedWithoutChangingSource()
    {
        using var image = VisionImage.CopyFrom(
            new ImageInfo(1, 1, EPixelLayout.Rgba32),
            new byte[] { 1, 2, 3, 4 }
        );
        var display = DisplayPixels.From(image, new CanvasOptions());
        CollectionAssert.AreEqual(new byte[] { 3, 2, 1, 4 }, display.Bytes);
        var source = new byte[4];
        image.CopyTo(0, source, 0, 4);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, source);
    }

    /// <summary>Gray16窗口映射仅影响显示。</summary>
    [TestMethod]
    public void Gray16MappingIsExplicit()
    {
        using var image = VisionImage.CopyFrom(
            new ImageInfo(3, 1, EPixelLayout.Gray16),
            new byte[] { 0, 0, 0, 128, 255, 255 }
        );
        var display = DisplayPixels.From(image, new CanvasOptions());
        CollectionAssert.AreEqual(new byte[] { 0, 127, 255 }, display.Bytes);
        Assert.AreEqual(EPixelLayout.Gray16, image.Info.Layout);
    }

    /// <summary>第0级精确保留Region孔洞和分离游程。</summary>
    [TestMethod]
    public void RegionMaskRetainsHoles()
    {
        var region = new RegionGeometry(
            new[]
            {
                new RegionRun(0, 0, 5),
                new RegionRun(1, 0, 1),
                new RegionRun(1, 4, 5),
                new RegionRun(2, 0, 5),
                new RegionRun(4, 8, 9),
            }
        );
        Assert.AreEqual(13L, region.AreaPixels);
        Assert.IsFalse(region.Contains(new PointD(2.5, 1.5)));
        Assert.IsTrue(region.Contains(new PointD(8.5, 4.5)));
        var mask = RegionMask.Tile(region, 0, 0, 10, 5);
        Assert.AreEqual((byte)0, mask[12]);
        Assert.AreEqual((byte)255, mask[48]);
        Assert.AreEqual(13, mask.Count(b => b == 255));
    }

    /// <summary>验证游程排序及排他右端约定。</summary>
    [TestMethod]
    public void InvalidRegionRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new RegionGeometry(new[] { new RegionRun(0, 0, 5), new RegionRun(0, 4, 6) })
        );
        Assert.ThrowsExactly<ArgumentException>(() => new RegionGeometry(new[] { default(RegionRun) }));
        Assert.AreEqual(0L, new RegionGeometry(Array.Empty<RegionRun>()).AreaPixels);
    }

    /// <summary>旋转矩形ROI使用局部坐标判断。</summary>
    [TestMethod]
    public void RotatedRectangleHit()
    {
        var roi = new RectangleGeometry(new PointD(10, 10), 8, 2, Math.PI / 2);
        Assert.IsTrue(roi.Contains(new PointD(10, 13)));
        Assert.IsFalse(roi.Contains(new PointD(13, 10)));
    }

    /// <summary>椭圆和多边形ROI语义与XLD笔画语义不同。</summary>
    [TestMethod]
    public void StandardRoiShapes()
    {
        Assert.IsTrue(new EllipseGeometry(new PointD(5, 5), 3, 2).Contains(new PointD(5, 5)));
        var points = new[] { new PointD(0, 0), new PointD(10, 0), new PointD(10, 10), new PointD(0, 10) };
        Assert.IsTrue(new ContourGeometry(points, true, true).Contains(new PointD(5, 5)));
        Assert.IsFalse(new ContourGeometry(points, true).Contains(new PointD(5, 5), 1));
    }

    /// <summary>LOD保留端点，不修改原始证据。</summary>
    [TestMethod]
    public void LodPreservesOriginal()
    {
        var contour = new ContourGeometry(
            Enumerable.Range(0, 10000).Select(i => new PointD(i, .01 * Math.Sin(i)))
        );
        var simple = ContourLod.Simplify(contour, .1);
        Assert.AreEqual(2, simple.Count);
        Assert.AreEqual(10000, contour.Points.Count);
        Assert.AreEqual(contour.Points[0].X, simple[0].X);
        Assert.AreEqual(contour.Points[9999].X, simple[1].X);
    }

    /// <summary>默认显示及放大检查保留全部细节，闭合轮廓始终精确。</summary>
    [TestMethod]
    public void LodIsOptInAndExactAtOneToOne()
    {
        Assert.AreEqual(0.0, CanvasPlanning.LodTolerance(new CanvasOptions(), .01));
        Assert.AreEqual(0.0, CanvasPlanning.LodTolerance(new CanvasOptions(true), 1));
        var contour = new ContourGeometry(
            new[] { new PointD(0, 0), new PointD(10, 0), new PointD(10, 10) },
            true
        );
        Assert.AreSame(contour.Points, ContourLod.Simplify(contour, 100));
    }

    /// <summary>最坏情况下简化会安全回退，不会无限阻塞。</summary>
    [TestMethod]
    public void LodWorkIsBounded()
    {
        var contour = new ContourGeometry(Enumerable.Range(0, 1000).Select(i => new PointD(i, i % 2)));
        Assert.AreSame(contour.Points, ContourLod.Simplify(contour, .1, 1));
    }

    /// <summary>量化不能超过要求的屏幕误差。</summary>
    [TestMethod]
    public void LodScreenErrorBudget()
    {
        var options = new CanvasOptions(true, .5);
        foreach (double scale in new[] { .01, .2, .51, .99 })
        {
            Assert.IsTrue(CanvasPlanning.LodTolerance(options, scale) * scale <= .5);
        }
    }

    /// <summary>16K适配仅请求小型可见分级图块，不生成完整768MiB的BGR位图。</summary>
    [TestMethod]
    public void LargeImagePlanningIsBounded()
    {
        var info = new ImageInfo(16384, 16384, EPixelLayout.Gray8);
        var view = new CanvasViewport();
        view.Fit(info.Width, info.Height, 1000, 700);
        var tiles = CanvasPlanning.Tiles(info, view, 1000, 700, 256);
        Assert.IsTrue(tiles.Count <= 64);
        Assert.IsTrue(tiles.All(t => t.Level >= 4));
        view.Zoom(1 / view.Scale, new PointD(500, 350));
        Assert.IsTrue(CanvasPlanning.Tiles(info, view, 1000, 700, 256).Count <= 20);
    }

    /// <summary>图块边缘尺寸和全分辨率字节保持精确。</summary>
    [TestMethod]
    public void TiledSourcePreservesPixels()
    {
        var bytes = Enumerable.Range(0, 65 * 17).Select(i => (byte)(i % 251)).ToArray();
        using var image = VisionImage.CopyFrom(new ImageInfo(65, 17, EPixelLayout.Gray8), bytes);
        using var source = image.Retain();
        using var tile = source.ReadTile(0, 1, 0, 64);
        Assert.AreEqual(1, tile.Info.Width);
        var copy = new byte[17];
        tile.CopyTo(0, copy, 0, 17);
        for (int y = 0; y < 17; y++)
        {
            Assert.AreEqual(bytes[y * 65 + 64], copy[y]);
        }
    }

    /// <summary>LRU逐出和释放行为明确。</summary>
    [TestMethod]
    public void CacheNeverExceedsBudget()
    {
        int released = 0;
        using var cache = new RenderCache<object>(10, _ => released++);
        cache.Add("a", new object(), 6);
        cache.Add("b", new object(), 6);
        Assert.AreEqual(1, released);
        Assert.AreEqual(6L, cache.Bytes);
        Assert.IsFalse(cache.TryGet("a", out _));
        Assert.IsFalse(cache.Add("huge", new object(), 11));
    }

    /// <summary>共享视口保持缩放锚点下的原图位置不变。</summary>
    [TestMethod]
    public void ZoomIsAnchored()
    {
        var view = new CanvasViewport();
        view.Fit(8192, 8192, 800, 600);
        var anchor = new PointD(145, 200);
        var before = view.ToImage(anchor);
        view.Zoom(2, anchor);
        var after = view.ToImage(anchor);
        Assert.AreEqual(before.X, after.X, 1e-8);
        Assert.AreEqual(before.Y, after.Y, 1e-8);
    }

    /// <summary>空XLD即使不绘制笔画，其对象标识仍可通过适配保留。</summary>
    [TestMethod]
    public void EmptyContourIsRetained()
    {
        var contour = new ContourGeometry(Array.Empty<PointD>());
        Assert.AreEqual(0, contour.Points.Count);
        Assert.IsFalse(contour.Contains(new PointD(0, 0)));
        Assert.AreSame(contour.Points, ContourLod.Simplify(contour, 1));
    }

    /// <summary>同步渲染也推进邮箱序号水位。</summary>
    [TestMethod]
    public void DisplayWatermarkRejectsLatePreview()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[1]);
        using var source = image.Retain();
        using var packet = new CanvasFrame("f", 9, source);
        using var box = new LatestFrameMailbox();
        box.Post(packet);
        box.AdvanceTo(10);
        Assert.IsNull(box.Take());
        Assert.IsFalse(box.Post(packet));
    }

    /// <summary>无效命中容差不能反转椭圆或矩形的几何语义。</summary>
    [TestMethod]
    public void InvalidToleranceRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new EllipseGeometry(new PointD(0, 0), 2, 3).Contains(new PointD(0, 0), double.NaN)
        );
    }

    /// <summary>大幅缩小仍保持有限且有界的规划工作量。</summary>
    [TestMethod]
    public void ExtremeZoomOutIsSafe()
    {
        var info = new ImageInfo(16384, 16384, EPixelLayout.Gray8);
        var view = new CanvasViewport();
        view.Fit(info.Width, info.Height, 1000, 700);
        view.Zoom(1e-12, new PointD(500, 350));
        Assert.IsTrue(CanvasPlanning.Tiles(info, view, 1000, 700, 256).Count <= 64);
    }

    /// <summary>视口完全移出图像时不请求图块，而不是报请求过大错误。</summary>
    [TestMethod]
    public void OffImageViewportHasNoTiles()
    {
        var info = new ImageInfo(1000, 1000, EPixelLayout.Gray8);
        var view = new CanvasViewport();
        view.Fit(1000, 1000, 800, 600);
        view.Pan(100000, 100000);
        Assert.AreEqual(0, CanvasPlanning.Tiles(info, view, 800, 600, 256).Count);
    }

    /// <summary>可移植程序集独立于视觉厂商和UI框架。</summary>
    [TestMethod]
    public void CoreHasNoVendorOrUiReferences()
    {
        foreach (var assembly in typeof(CanvasFrame).Assembly.GetReferencedAssemblies())
        {
            Assert.IsFalse(assembly.Name!.Contains("Halcon"));
            Assert.IsFalse(assembly.Name.Contains("LabelInspection"));
            Assert.IsFalse(assembly.Name.Contains("Drawing"));
            Assert.IsFalse(assembly.Name.Contains("Presentation"));
        }
    }
}
