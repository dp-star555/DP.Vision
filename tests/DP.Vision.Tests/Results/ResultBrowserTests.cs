using System;
using System.Linq;
using System.Threading.Tasks;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>经公开视图入口验证集合替换、选择、预算及源所有权，不建模流程。</summary>
[TestClass]
public sealed class ResultBrowserTests
{
    private static IImageSource Image() =>
        VisionImage.CopyFrom(new ImageInfo(2, 2, EPixelLayout.Gray8), new byte[4]);

    private static CanvasLayer Layer(string id = "roi", bool visible = true) =>
        new CanvasLayer(
            id,
            ELayerKind.Roi,
            new[] { new Visual("box", new RectangleGeometry(new PointD(1, 1), 1, 1), VisionColors.Red) },
            visible: visible,
            name: "检测范围"
        );

    private static VisionView View(IImageSource source, string id = "raw", params CanvasLayer[] layers) =>
        new VisionView(
            id,
            id,
            "image-" + id,
            source,
            new GeometryOverlay("image-" + id, layers.Length == 0 ? new[] { Layer() } : layers)
        );

    /// <summary>客户释放源后会话仍持有独立租约，结束时归还缓冲池。</summary>
    [TestMethod]
    public void ReplacementOwnsOnlyItsLease()
    {
        using var pool = new FrameBufferPool(new ImageInfo(2, 2, EPixelLayout.Gray8), 1, 4);
        pool.TryRent(out var rented);
        using var writer = rented!;
        var source = writer.Publish();
        using var browser = new ResultBrowserSession();
        Assert.IsTrue(browser.SetViews(new[] { View(source) }));
        source.Dispose();
        Assert.IsFalse(pool.TryRent(out _));
        Assert.AreEqual(4L, browser.Snapshot.RetainedPixelBytes);
        Assert.AreEqual("检测范围", browser.Snapshot.Layers[0].Name);
        browser.Dispose();
        Assert.IsTrue(pool.TryRent(out var returned));
        returned!.Dispose();
    }

    /// <summary>完整替换删除旧视图，稳定键仍存在时保留选择。</summary>
    [TestMethod]
    public void CompleteReplacementRemovesOldViewsAndPreservesSelectionWhenPossible()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession();
        browser.SetViews(new[] { View(image), View(image, "binary") });
        browser.SelectView("binary");
        browser.SetViews(new[] { View(image, "binary"), View(image, "processed") });
        Assert.AreEqual("binary", browser.Snapshot.ViewId);
        CollectionAssert.AreEqual(
            new[] { "binary", "processed" },
            browser.Snapshot.Views.Select(v => v.Id).ToArray()
        );
        browser.SetViews(new[] { View(image) });
        Assert.AreEqual("raw", browser.Snapshot.ViewId);
        Assert.AreEqual(1, browser.Snapshot.Views.Count);
    }

    /// <summary>显隐偏好按视图隔离，不修改原始图层默认值。</summary>
    [TestMethod]
    public void LayerPreferencesAreScopedToViewAndDoNotAlterDefaults()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession();
        var original = Layer();
        browser.SetViews(new[] { View(image, "raw", original), View(image, "binary") });
        browser.SetLayerVisible("roi", false);
        Assert.IsTrue(original.Visible);
        browser.SelectView("binary");
        Assert.IsTrue(browser.Snapshot.Layers[0].Visible);
        browser.SelectView("raw");
        Assert.IsFalse(browser.Snapshot.Layers[0].Visible);
        browser.SetViews(new[] { View(image) });
        Assert.IsFalse(browser.Snapshot.Layers[0].Visible);
        browser.ResetLayerVisibility();
        Assert.IsTrue(browser.Snapshot.Layers[0].Visible);
    }

    /// <summary>预算优先保护所选视图，淘汰像素时保留可选元数据。</summary>
    [TestMethod]
    public void PixelBudgetEvictsPreviewNotMetadata()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession(new ResultBrowserOptions(previewBytes: 8));
        browser.SetViews(new[] { View(image, "a"), View(image, "b"), View(image, "c") });
        Assert.AreEqual(8L, browser.Snapshot.RetainedPixelBytes);
        Assert.AreEqual(3, browser.Snapshot.Views.Count);
        Assert.IsTrue(browser.Snapshot.HasImage);
        browser.SelectView("b");
        Assert.IsFalse(browser.Snapshot.HasImage);
        Assert.AreEqual(1, browser.Snapshot.Layers.Count);
        StringAssert.Contains(browser.Snapshot.Status, "预览未保留");
        browser.SelectView("c");
        Assert.IsTrue(browser.Snapshot.HasImage);
    }

    /// <summary>单图超预算时不占有源租约。</summary>
    [TestMethod]
    public void OversizedPreviewDoesNotPinPool()
    {
        using var pool = new FrameBufferPool(new ImageInfo(2, 2, EPixelLayout.Gray8), 1, 4);
        pool.TryRent(out var rented);
        using var writer = rented!;
        var source = writer.Publish();
        using var browser = new ResultBrowserSession(new ResultBrowserOptions(previewBytes: 3));
        Assert.IsTrue(browser.SetViews(new[] { View(source) }));
        source.Dispose();
        Assert.IsFalse(browser.Snapshot.HasImage);
        Assert.AreEqual(0L, browser.Snapshot.RetainedPixelBytes);
        Assert.IsTrue(pool.TryRent(out var returned));
        returned!.Dispose();
    }

    /// <summary>源保留失败时回收全部临时租约，旧集合不受影响。</summary>
    [TestMethod]
    public void RetainFailureRollsBackWholeCollection()
    {
        using var oldImage = Image();
        using var browser = new ResultBrowserSession();
        browser.SetViews(new[] { View(oldImage, "old") });
        long version = browser.Version;
        using var pool = new FrameBufferPool(new ImageInfo(2, 2, EPixelLayout.Gray8), 1, 4);
        pool.TryRent(out var rented);
        using var writer = rented!;
        var good = writer.Publish();
        var dead = Image();
        var views = new[] { View(good), View(dead, "binary") };
        dead.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => browser.SetViews(views));
        good.Dispose();
        Assert.AreEqual(version, browser.Version);
        Assert.AreEqual("old", browser.Snapshot.ViewId);
        Assert.AreEqual(4L, browser.Snapshot.RetainedPixelBytes);
        Assert.IsTrue(pool.TryRent(out var returned));
        returned!.Dispose();
    }

    /// <summary>清空会话释放图像，但保留用户显隐偏好。</summary>
    [TestMethod]
    public void ClearDropsImagesButKeepsVisibilityPreferences()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession();
        browser.SetViews(new[] { View(image) });
        browser.SetLayerVisible("roi", false);
        browser.Clear();
        Assert.IsFalse(browser.Snapshot.HasImage);
        Assert.AreEqual(0, browser.Snapshot.Views.Count);
        Assert.AreEqual(0L, browser.Snapshot.RetainedPixelBytes);
        Assert.AreEqual("暂无视图", browser.Snapshot.Status);
        browser.SetViews(new[] { View(image) });
        Assert.IsFalse(browser.Snapshot.Layers[0].Visible);
    }

    /// <summary>视图容量超限拒绝整个更新。</summary>
    [TestMethod]
    public void ViewCapacityRejectsWithoutReplacing()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession(new ResultBrowserOptions(maximumViews: 1));
        browser.SetViews(new[] { View(image) });
        Assert.IsFalse(browser.SetViews(new[] { View(image), View(image, "binary") }));
        Assert.AreEqual(1, browser.Snapshot.Views.Count);
    }

    /// <summary>累计几何超限时原集合保持不变。</summary>
    [TestMethod]
    public void GeometryBudgetRejectsWithoutReplacing()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession(new ResultBrowserOptions(maximumGeometryElements: 4));
        browser.SetViews(new[] { View(image) });
        Assert.IsFalse(browser.SetViews(new[] { View(image), View(image, "binary") }));
        Assert.AreEqual(1, browser.Snapshot.Views.Count);
    }

    /// <summary>并发替换不混合不同提交的视图。</summary>
    [TestMethod]
    public void ConcurrentReplacementsAreAtomicCollections()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession();
        Parallel.For(
            0,
            100,
            i =>
            {
                Assert.IsTrue(browser.SetViews(new[] { View(image, i + "-a"), View(image, i + "-b") }));
                var snapshot = browser.Snapshot;
                Assert.AreEqual(2, snapshot.Views.Count);
                Assert.AreEqual(snapshot.Views[0].Id.Split('-')[0], snapshot.Views[1].Id.Split('-')[0]);
            }
        );
        Assert.AreEqual(100L, browser.Version);
        Assert.AreEqual(8L, browser.Snapshot.RetainedPixelBytes);
    }

    /// <summary>同名新集合也不接受旧界面复选事件。</summary>
    [TestMethod]
    public void ReplacedCollectionRejectsOldUiEventsEvenWithSameIds()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession();
        browser.SetViews(new[] { View(image) });
        var old = browser.Snapshot;
        browser.SetViews(new[] { View(image) });
        Assert.IsFalse(browser.TrySetLayerVisible(old, "roi", false));
        Assert.IsFalse(browser.TrySelectView(old, "raw"));
        Assert.IsFalse(browser.TrySetAllLayersVisible(old, false));
        Assert.IsTrue(browser.Snapshot.Layers[0].Visible);
        var current = browser.Snapshot;
        Assert.IsTrue(browser.TrySetLayerVisible(current, "roi", false));
    }

    /// <summary>其他浏览器快照不能修改当前浏览器。</summary>
    [TestMethod]
    public void OtherBrowsersSnapshotCannotEditThisBrowser()
    {
        using var image = Image();
        using var a = new ResultBrowserSession();
        using var b = new ResultBrowserSession();
        a.SetViews(new[] { View(image) });
        b.SetViews(new[] { View(image) });
        Assert.IsFalse(b.TrySetLayerVisible(a.Snapshot, "roi", false));
    }

    /// <summary>最小偏好容量仍保证当前128层的全选操作完整。</summary>
    [TestMethod]
    public void AllLayersWorkAtPreferenceLimit()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession(new ResultBrowserOptions(maximumPreferences: 128));
        var layers = Enumerable.Range(0, 128).Select(i => Layer("layer-" + i)).ToArray();
        browser.SetViews(new[] { View(image, "raw", layers), View(image, "binary") });
        browser.SetAllLayersVisible(false);
        browser.SelectView("binary");
        browser.SetLayerVisible("roi", false);
        browser.SelectView("raw");
        browser.SetAllLayersVisible(false);
        Assert.IsTrue(browser.Snapshot.Layers.All(l => !l.Visible));
        browser.ResetLayerVisibility();
        Assert.IsTrue(browser.Snapshot.Layers.All(l => l.Visible));
    }

    /// <summary>已关闭入口拒绝提交，不读取或保留输入源。</summary>
    [TestMethod]
    public void ClosedSinkRejectsWithoutRetainingDisposedSource()
    {
        var image = Image();
        var views = new[] { View(image) };
        using var browser = new ResultBrowserSession();
        browser.Dispose();
        image.Dispose();
        Assert.IsFalse(browser.SetViews(views));
    }

    /// <summary>预算校验指出准确的参数名。</summary>
    /// <param name="pixels">像素预算。</param>
    /// <param name="views">视图容量。</param>
    /// <param name="geometry">几何预算。</param>
    /// <param name="preferences">偏好容量。</param>
    /// <param name="parameter">预期无效参数名。</param>
    [TestMethod]
    [DataRow(0L, 128, 2000000L, 4096, "previewBytes")]
    [DataRow(4L, 0, 2000000L, 4096, "maximumViews")]
    [DataRow(4L, 257, 2000000L, 4096, "maximumViews")]
    [DataRow(4L, 128, 0L, 4096, "maximumGeometryElements")]
    [DataRow(4L, 128, 2000001L, 4096, "maximumGeometryElements")]
    [DataRow(4L, 128, 2000000L, 127, "maximumPreferences")]
    [DataRow(4L, 128, 2000000L, 16385, "maximumPreferences")]
    public void InvalidBudgetsNameTheirParameter(
        long pixels,
        int views,
        long geometry,
        int preferences,
        string parameter
    )
    {
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ResultBrowserOptions(pixels, views, geometry, preferences)
        );
        Assert.AreEqual(parameter, error.ParamName);
    }

    /// <summary>空输入、重复键及异帧叠加均拒绝。</summary>
    [TestMethod]
    public void NullAndDuplicateInputsAreRejected()
    {
        using var image = Image();
        using var browser = new ResultBrowserSession();
        Assert.ThrowsExactly<ArgumentNullException>(() => browser.SetViews(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new VisionView("raw", "原图", "image", null!));
        Assert.ThrowsExactly<ArgumentException>(() => browser.SetViews(new VisionView[] { null! }));
        Assert.ThrowsExactly<ArgumentException>(() => browser.SetViews(new[] { View(image), View(image) }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new VisionView("raw", "原图", "a", image, new GeometryOverlay("b", Array.Empty<CanvasLayer>()))
        );
    }

    /// <summary>公开契约不再包含节点、执行状态及流程运行管理入口。</summary>
    [TestMethod]
    public void VisionPublicSurfaceDoesNotContainWorkflowConcepts()
    {
        var assembly = typeof(VisionView).Assembly;
        foreach (
            string name in new[]
            {
                "NodeDisplayView",
                "NodeDisplayResult",
                "ENodeDisplayState",
                "INodeDisplaySink",
            }
        )
            Assert.IsNull(assembly.GetType("DP.Vision." + name));
        Assert.IsNull(typeof(ResultBrowserSession).GetMethod("BeginRun"));
        Assert.IsNull(typeof(ResultBrowserSession).GetMethod("SelectNode"));
        foreach (string name in new[] { "RunId", "NodeId", "Nodes" })
            Assert.IsNull(typeof(ResultBrowserSnapshot).GetProperty(name));
    }
}
