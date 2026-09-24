using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>WinForms/WPF画布共用逻辑：帧生命周期、视口与编辑器输入映射、命中测试和缓存清理。</summary>
[TestClass]
public sealed class CanvasCoreTests
{
    private readonly List<object> _disposedPaths = new List<object>();
    private int _invalidations;
    private int _captureReleases;

    private CanvasCore<object> Core()
    {
        return new CanvasCore<object>(
            () => _invalidations++,
            () => _captureReleases++,
            () => (800, 600),
            p => _disposedPaths.Add(p)
        );
    }

    private static CanvasFrame Frame(string id, long sequence, GeometryOverlay? overlay = null)
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(100, 80, EPixelLayout.Gray8), new byte[8000]);
        return new CanvasFrame(id, sequence, image, overlay);
    }

    private static PointD Screen(CanvasViewport view, PointD image)
    {
        return new PointD(view.Origin.X + image.X * view.Scale, view.Origin.Y + image.Y * view.Scale);
    }

    private static void DrawPendingTriangle(CanvasCore<object> core)
    {
        core.Editor = new RoiEditor { Tool = ERoiTool.Polygon };
        foreach (var p in new[] { new PointD(10, 10), new PointD(60, 10), new PointD(60, 50) })
        {
            core.MouseDown(ECanvasButton.Left, Screen(core.Viewport, p), 1);
            core.MouseUp(ECanvasButton.Left, Screen(core.Viewport, p), 4, 4);
        }
    }

    /// <summary>首帧自动适配；旧序号被忽略；换帧身份时像素版本递增。</summary>
    [TestMethod]
    public void PresentFitsAndOrdersFrames()
    {
        using var core = Core();
        using var first = Frame("a", 1);
        core.Present(first);
        Assert.IsTrue(core.FitMode);
        Assert.AreEqual("a", core.Frame!.FrameId);
        long revision = core.Revision;
        using var older = Frame("old", 0);
        core.Present(older);
        Assert.AreEqual("a", core.Frame.FrameId);
        using var next = Frame("b", 2);
        core.Present(next);
        Assert.AreEqual("b", core.Frame.FrameId);
        Assert.AreEqual(revision + 1, core.Revision);
    }

    /// <summary>适应窗口（Home、窗口尺寸变化）不再丢弃正在绘制的多边形顶点。</summary>
    [TestMethod]
    public void FitToWindowKeepsPendingPolygon()
    {
        using var core = Core();
        using var frame = Frame("a", 1);
        core.Present(frame);
        DrawPendingTriangle(core);
        Assert.IsTrue(core.KeyDown(ECanvasKey.Home, false, false));
        core.Resized();
        Assert.IsTrue(core.Editor!.IsEditing);
        Assert.IsTrue(core.KeyDown(ECanvasKey.Enter, false, false));
        Assert.AreEqual(1, core.Editor.Document.Rois.Count);
    }

    /// <summary>右键单击结束多边形；右键拖动只平移，保留待定顶点。</summary>
    [TestMethod]
    public void RightClickFinishesAndRightDragPans()
    {
        using var core = Core();
        using var frame = Frame("a", 1);
        core.Present(frame);
        DrawPendingTriangle(core);
        var origin = core.Viewport.Origin;
        Assert.IsTrue(core.MouseDown(ECanvasButton.Right, new PointD(300, 300), 1).Capture);
        core.MouseMove(new PointD(340, 320));
        Assert.IsFalse(core.MouseUp(ECanvasButton.Right, new PointD(340, 320), 4, 4));
        Assert.AreEqual(origin.X + 40, core.Viewport.Origin.X, 1e-9);
        Assert.IsFalse(core.FitMode);
        Assert.IsTrue(core.Editor!.IsEditing);

        core.MouseDown(ECanvasButton.Right, new PointD(300, 300), 1);
        Assert.IsTrue(core.MouseUp(ECanvasButton.Right, new PointD(302, 301), 4, 4));
        var contour = (ContourGeometry)core.Editor.Document.Rois[0].Shape;
        Assert.IsTrue(contour.Closed);
        Assert.AreEqual(3, contour.Points.Count);
    }

    /// <summary>左键拖动要求捕获；捕获丢失时取消未提交的拖动并不产生文档。</summary>
    [TestMethod]
    public void LeftDragCapturesAndLostCaptureCancels()
    {
        using var core = Core();
        using var frame = Frame("a", 1);
        core.Present(frame);
        core.Editor = new RoiEditor { Tool = ERoiTool.Rectangle };
        var (capture, handled) = core.MouseDown(ECanvasButton.Left, new PointD(100, 100), 1);
        Assert.IsTrue(capture);
        Assert.IsTrue(handled);
        core.MouseMove(new PointD(200, 180));
        core.CaptureLost();
        Assert.IsFalse(core.Editor.IsEditing);
        Assert.AreEqual(0, core.Editor.Document.Rois.Count);
    }

    /// <summary>键盘命令映射到编辑器：Ctrl+Z撤销，Ctrl+Shift+Z与Ctrl+Y重做，Delete删除选中。</summary>
    [TestMethod]
    public void KeyboardCommands()
    {
        using var core = Core();
        using var frame = Frame("a", 1);
        core.Present(frame);
        DrawPendingTriangle(core);
        core.KeyDown(ECanvasKey.Enter, false, false);
        var editor = core.Editor!;
        Assert.IsTrue(core.KeyDown(ECanvasKey.Z, true, false));
        Assert.AreEqual(0, editor.Document.Rois.Count);
        Assert.IsTrue(core.KeyDown(ECanvasKey.Z, true, true));
        Assert.AreEqual(1, editor.Document.Rois.Count);
        core.KeyDown(ECanvasKey.Z, true, false);
        Assert.IsTrue(core.KeyDown(ECanvasKey.Y, true, false));
        Assert.AreEqual(1, editor.Document.Rois.Count);
        editor.Select(editor.Document.Rois[0].Id);
        Assert.IsTrue(core.KeyDown(ECanvasKey.Delete, false, false));
        Assert.AreEqual(0, editor.Document.Rois.Count);
        Assert.IsFalse(core.KeyDown(ECanvasKey.Other, false, false));
        Assert.IsFalse(core.KeyDown(ECanvasKey.Z, false, false));
    }

    /// <summary>命中测试使用精确源几何，遵守图层显隐覆盖。</summary>
    [TestMethod]
    public void HitTestRespectsVisibility()
    {
        using var core = Core();
        var visual = new Visual("box", new RectangleGeometry(new PointD(50, 40), 20, 20));
        var layer = new CanvasLayer("facts", ELayerKind.Annotation, new[] { visual });
        var overlay = new GeometryOverlay("a", new[] { layer });
        using var frame = Frame("a", 1, overlay);
        core.Present(frame);
        var client = Screen(core.Viewport, new PointD(50, 40));
        Assert.AreSame(visual, core.HitTest(client));
        core.SetLayerVisible("facts", false);
        Assert.IsNull(core.HitTest(client));
    }

    /// <summary>路径按几何与容差缓存；编辑层几何消失后其路径被释放，叠加层路径保留到换帧。</summary>
    [TestMethod]
    public void PathCacheTracksLiveGeometry()
    {
        using var core = Core();
        var fact = new RectangleGeometry(new PointD(50, 40), 20, 20);
        var overlay = new GeometryOverlay(
            "a",
            new[] { new CanvasLayer("facts", ELayerKind.Annotation, new[] { new Visual("f", fact) }) }
        );
        using var frame = Frame("a", 1, overlay);
        core.Present(frame);
        int builds = 0;
        Func<Geometry, double, object> build = (g, t) =>
        {
            builds++;
            return new object();
        };
        var factPath = core.GetPath(fact, 1, build);
        Assert.AreSame(factPath, core.GetPath(fact, 1, build));
        Assert.AreEqual(1, builds);

        core.Editor = new RoiEditor { Tool = ERoiTool.Rectangle };
        core.MouseDown(ECanvasButton.Left, Screen(core.Viewport, new PointD(10, 10)), 1);
        core.MouseMove(Screen(core.Viewport, new PointD(30, 30)));
        var draft = core.ActiveLayers().Last().Visuals.Single(v => v.Id == "draft").Geometry;
        var draftPath = core.GetPath(draft, 1, build);
        core.MouseMove(Screen(core.Viewport, new PointD(35, 35)));
        CollectionAssert.Contains(_disposedPaths, draftPath);
        CollectionAssert.DoesNotContain(_disposedPaths, factPath);

        using var next = Frame("b", 2);
        core.Present(next);
        CollectionAssert.Contains(_disposedPaths, factPath);
    }

    /// <summary>同一Region的掩码键稳定，不同Region或颜色得到不同键。</summary>
    [TestMethod]
    public void MaskKeysIdentifyRegions()
    {
        using var core = Core();
        var a = new RegionGeometry(new[] { new RegionRun(1, 1, 5) });
        var b = new RegionGeometry(new[] { new RegionRun(1, 1, 5) });
        var overlay = new GeometryOverlay(
            "a",
            new[]
            {
                new CanvasLayer("r", ELayerKind.Region, new[] { new Visual("a", a), new Visual("b", b) }),
            }
        );
        using var frame = Frame("a", 1, overlay);
        core.Present(frame);
        var request = CanvasPlanning.Tiles(frame.Info, core.Viewport, 800, 600, 256)[0];
        var va = overlay.Layers[0].Visuals[0];
        var vb = overlay.Layers[0].Visuals[1];
        Assert.AreEqual(core.MaskKey(va, a, request), core.MaskKey(va, a, request));
        Assert.AreNotEqual(core.MaskKey(va, a, request), core.MaskKey(vb, b, request));
        var recolored = new Visual("a", a, VisionColors.Blue);
        Assert.AreNotEqual(core.MaskKey(va, a, request), core.MaskKey(recolored, a, request));
    }

    /// <summary>关闭后生产者提交返回false；释放后编辑器解除关联。</summary>
    [TestMethod]
    public void DisposeClosesMailboxAndDetachesEditor()
    {
        var core = Core();
        var editor = new RoiEditor();
        core.Editor = editor;
        using var frame = Frame("a", 1);
        core.Dispose();
        Assert.IsFalse(core.PostFrame(frame));
        int before = _invalidations;
        editor.Tool = ERoiTool.Polygon;
        Assert.AreEqual(before, _invalidations);
    }
}
