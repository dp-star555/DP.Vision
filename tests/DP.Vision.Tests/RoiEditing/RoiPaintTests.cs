using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>涂抹层：笔刷栅格化、取反、包含/排除合成规则，以及涂抹与几何ROI共存时的编辑行为。</summary>
[TestClass]
public sealed class RoiPaintTests
{
    private static RoiEditor Painter(double radius = 2)
    {
        var editor = new RoiEditor { Tool = ERoiTool.Brush, BrushRadius = radius };
        editor.SetPaintArea(40, 30);
        return editor;
    }

    private static void Stroke(RoiEditor editor, params PointD[] points)
    {
        editor.PointerDown(points[0], 1);
        foreach (var p in points.Skip(1))
        {
            editor.PointerMove(p);
        }

        editor.PointerUp(points[points.Length - 1]);
    }

    private static RegionGeometry Rect(int x, int y, int w, int h)
    {
        return new RegionGeometry(Enumerable.Range(y, h).Select(r => new RegionRun(r, x, x + w)));
    }

    /// <summary>水平笔画：端点为圆、中间为矩形带，距离恰为半径的像素中心计入。</summary>
    [TestMethod]
    public void StrokeCoversCapsule()
    {
        var region = RegionRasterizer.Stroke(new[] { new PointD(10.5, 10.5), new PointD(30.5, 10.5) }, 2, 40, 30);
        CollectionAssert.Contains(region.Runs.ToList(), new RegionRun(10, 8, 33));
        CollectionAssert.Contains(region.Runs.ToList(), new RegionRun(12, 10, 31));
        Assert.IsFalse(region.Runs.Any(r => r.Row < 8 || r.Row > 12));
    }

    /// <summary>笔画与逐像素“到折线距离不超过半径”的暴力判定完全一致，越界部分被裁去。</summary>
    [TestMethod]
    public void StrokeMatchesBruteForceAndClips()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 40; trial++)
        {
            int count = random.Next(1, 6);
            var points = Enumerable
                .Range(0, count)
                .Select(_ => new PointD(random.NextDouble() * 50 - 5, random.NextDouble() * 40 - 5))
                .ToArray();
            double radius = .5 + random.NextDouble() * 6;
            var region = RegionRasterizer.Stroke(points, radius, 40, 30);
            for (int y = 0; y < 30; y++)
            {
                for (int x = 0; x < 40; x++)
                {
                    var c = new PointD(x + .5, y + .5);
                    bool expected = Enumerable
                        .Range(0, Math.Max(1, count - 1))
                        .Any(i => Distance(c, points[i], points[Math.Min(i + 1, count - 1)]) <= radius);
                    Assert.AreEqual(expected, region.Contains(c), $"trial {trial} ({x},{y})");
                }
            }
        }
    }

    private static double Distance(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X,
            dy = b.Y - a.Y,
            l = dx * dx + dy * dy;
        double t = l == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l));
        double x = a.X + t * dx - p.X,
            y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>取反限定在原图范围内，两次取反回到原Region。</summary>
    [TestMethod]
    public void ComplementWithinImage()
    {
        var region = Rect(1, 1, 2, 2);
        var inverted = region.Complement(4, 3);
        Assert.AreEqual(12 - 4, inverted.AreaPixels);
        CollectionAssert.AreEqual(region.Runs.ToList(), inverted.Complement(4, 3).Runs.ToList());
        Assert.AreEqual(12, new RegionGeometry(Array.Empty<RegionRun>()).Complement(4, 3).AreaPixels);
    }

    /// <summary>合成规则：包含并集减排除并集，排除优先、与顺序无关；没有包含时以整幅图为基底。</summary>
    [TestMethod]
    public void ComposeExcludeWins()
    {
        var a = new RectangleGeometry(new PointD(10, 10), 10, 10);
        var hole = new RectangleGeometry(new PointD(10, 10), 4, 4);
        var region = RegionRasterizer.Compose(40, 30, new Geometry[] { a, hole }, new Geometry[] { hole });
        Assert.AreEqual(100 - 16, region.AreaPixels);
        var swapped = RegionRasterizer.Compose(40, 30, new Geometry[] { hole, a }, new Geometry[] { hole });
        CollectionAssert.AreEqual(region.Runs.ToList(), swapped.Runs.ToList());
        Assert.AreEqual(1200 - 16, RegionRasterizer.Compose(40, 30, Array.Empty<Geometry>(), new Geometry[] { hole }).AreaPixels);
    }

    /// <summary>画笔写入当前用途的涂抹层；一次笔画一次撤销；画排除会从包含涂抹层去掉同一位置。</summary>
    [TestMethod]
    public void BrushPaintsAndOverridesOtherLayer()
    {
        var editor = Painter();
        Stroke(editor, new PointD(10, 10), new PointD(20, 10));
        var include = editor.PaintRegion(ERoiPurpose.Include)!;
        Assert.IsTrue(include.AreaPixels > 0);
        Assert.AreEqual(ERoiTool.Brush, editor.Tool);
        Assert.IsFalse(editor.IsEditing);

        editor.PaintPurpose = ERoiPurpose.Exclude;
        Stroke(editor, new PointD(15, 5), new PointD(15, 15));
        var exclude = editor.PaintRegion(ERoiPurpose.Exclude)!;
        Assert.AreEqual(0, editor.PaintRegion(ERoiPurpose.Include)!.Intersect(exclude).AreaPixels);

        editor.Undo();
        Assert.IsNull(editor.PaintRegion(ERoiPurpose.Exclude));
        CollectionAssert.AreEqual(include.Runs.ToList(), editor.PaintRegion(ERoiPurpose.Include)!.Runs.ToList());
    }

    /// <summary>橡皮只擦涂抹层，几何ROI保持不变；擦空的涂抹层从文档中删除。</summary>
    [TestMethod]
    public void EraserLeavesGeometry()
    {
        var editor = Painter(3);
        var rect = new RoiDefinition("box", new RectangleGeometry(new PointD(20, 15), 10, 10));
        editor.Load(new RoiDocument(new[] { rect }));
        editor.Tool = ERoiTool.Brush;
        Stroke(editor, new PointD(20, 15));
        editor.Tool = ERoiTool.Eraser;
        editor.BrushRadius = 8;
        Stroke(editor, new PointD(20, 15));
        Assert.AreEqual(1, editor.Document.Rois.Count);
        Assert.AreSame(rect, editor.Document.Rois[0]);
    }

    /// <summary>取反、互换、清空都只作用于涂抹层，各自形成一个撤销事务。</summary>
    [TestMethod]
    public void InvertSwapClear()
    {
        var editor = Painter();
        Stroke(editor, new PointD(5, 5));
        long area = editor.PaintRegion(ERoiPurpose.Include)!.AreaPixels;
        Assert.IsTrue(editor.InvertPaint(ERoiPurpose.Include));
        Assert.AreEqual(1200 - area, editor.PaintRegion(ERoiPurpose.Include)!.AreaPixels);
        Assert.IsTrue(editor.SwapPaintPurpose());
        Assert.IsNull(editor.PaintRegion(ERoiPurpose.Include));
        Assert.AreEqual(1200 - area, editor.PaintRegion(ERoiPurpose.Exclude)!.AreaPixels);
        Assert.IsTrue(editor.ClearPaint());
        Assert.AreEqual(0, editor.Document.Rois.Count);
        Assert.IsFalse(editor.ClearPaint());
        editor.Undo();
        editor.Undo();
        Assert.AreEqual(1200 - area, editor.PaintRegion(ERoiPurpose.Include)!.AreaPixels);
    }

    /// <summary>拍平：选中几何ROI按像素并入同用途的涂抹层并删除原ROI；开放折线被拒绝。</summary>
    [TestMethod]
    public void FlattenMovesGeometryIntoPaint()
    {
        var editor = Painter();
        editor.Load(
            new RoiDocument(
                new[]
                {
                    new RoiDefinition("hole", new RectangleGeometry(new PointD(10, 10), 4, 4), ERoiPurpose.Exclude),
                    new RoiDefinition("line", new ContourGeometry(new[] { new PointD(1, 1), new PointD(5, 5) })),
                }
            )
        );
        editor.Select("hole");
        Assert.IsTrue(editor.FlattenSelected());
        Assert.AreEqual(16, editor.PaintRegion(ERoiPurpose.Exclude)!.AreaPixels);
        Assert.IsFalse(editor.Document.Rois.Any(r => r.Id == "hole"));
        Assert.IsNull(editor.SelectedId);

        editor.Select("line");
        Assert.IsFalse(editor.FlattenSelected());
        Assert.IsNotNull(editor.ValidationError);
        Assert.AreEqual(2, editor.Document.Rois.Count);
    }

    /// <summary>没有原图尺寸时拒绝涂抹；涂抹层的用途不能通过元数据修改。</summary>
    [TestMethod]
    public void PaintNeedsAreaAndKeepsPurpose()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Brush };
        Assert.IsFalse(editor.PointerDown(new PointD(5, 5), 1));
        Assert.IsNotNull(editor.ValidationError);
        Assert.IsFalse(editor.InvertPaint(ERoiPurpose.Include));

        editor.SetPaintArea(40, 30);
        Stroke(editor, new PointD(5, 5));
        editor.Select(RoiEditor.PaintIncludeId);
        editor.SetSelectedMetadata(ERoiPurpose.Exclude, true);
        Assert.AreEqual(ERoiPurpose.Include, editor.Document.Rois[0].Purpose);
        Assert.IsNotNull(editor.ValidationError);
        editor.SetSelectedMetadata(ERoiPurpose.Include, false);
        Assert.IsFalse(editor.Document.Rois[0].Enabled);
    }

    /// <summary>画笔悬停显示笔刷圈，笔画中显示草稿Region；草稿不进入文档。</summary>
    [TestMethod]
    public void BrushPreview()
    {
        var editor = Painter(4);
        editor.PointerMove(new PointD(10, 10));
        var cursor = editor.DisplayLayer().Visuals.Single(v => v.Id == "brush");
        Assert.AreEqual(4, ((EllipseGeometry)cursor.Geometry).RadiusX);
        editor.PointerDown(new PointD(10, 10), 1);
        editor.PointerMove(new PointD(20, 10));
        Assert.IsInstanceOfType(editor.DisplayLayer().Visuals.Single(v => v.Id == "draft").Geometry, typeof(RegionGeometry));
        Assert.AreEqual(0, editor.Document.Rois.Count);
        editor.Cancel();
        Assert.AreEqual(0, editor.Document.Rois.Count);
    }

    /// <summary>文档有效区域：几何包含减涂抹排除；空文档为整幅图；全部禁用和开放折线报错并指出标识。</summary>
    [TestMethod]
    public void DocumentEffectiveRegion()
    {
        var editor = Painter();
        editor.Load(new RoiDocument(new[] { new RoiDefinition("box", new RectangleGeometry(new PointD(10, 10), 10, 10)) }));
        editor.Tool = ERoiTool.Brush;
        editor.PaintPurpose = ERoiPurpose.Exclude;
        Stroke(editor, new PointD(10, 10));
        var hole = editor.PaintRegion(ERoiPurpose.Exclude)!;
        Assert.AreEqual(100 - hole.AreaPixels, editor.Document.ToRegion(40, 30).AreaPixels);

        Assert.AreEqual(1200, new RoiDocument(Array.Empty<RoiDefinition>()).ToRegion(40, 30).AreaPixels);
        var disabled = new RoiDocument(new[] { new RoiDefinition("a", new RectangleGeometry(new PointD(5, 5), 2, 2), ERoiPurpose.Include, false) });
        Assert.ThrowsExactly<InvalidOperationException>(() => disabled.ToRegion(40, 30));
        var open = new RoiDocument(new[] { new RoiDefinition("折线", new ContourGeometry(new[] { new PointD(1, 1), new PointD(5, 5) })) });
        StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() => open.ToRegion(40, 30)).Message, "折线");
    }

    /// <summary>涂抹层随ROI文档XML完整往返。</summary>
    [TestMethod]
    public void PaintLayersRoundTripXml()
    {
        var editor = Painter();
        Stroke(editor, new PointD(10, 10), new PointD(20, 12));
        var restored = RoiDocumentXml.Deserialize(RoiDocumentXml.Serialize(editor.Document));
        var roi = restored.Rois.Single();
        Assert.AreEqual(RoiEditor.PaintIncludeId, roi.Id);
        CollectionAssert.AreEqual(editor.PaintRegion(ERoiPurpose.Include)!.Runs.ToList(), ((RegionGeometry)roi.Shape).Runs.ToList());
    }

    /// <summary>弹窗会话：提交后重新合成有效区域并生成带叠加的帧；合成失败时状态栏给出原因。</summary>
    [TestMethod]
    public void SessionComposesEffectiveRegion()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(40, 30, EPixelLayout.Gray8), new byte[1200]);
        using var session = new RoiMaskSession(image);
        Assert.AreEqual(1200, session.Effective!.AreaPixels);
        int frames = 0;
        session.FrameChanged += (_, __) => frames++;
        Stroke(session.Editor, new PointD(10, 10));
        Assert.AreEqual(1, frames);
        Assert.AreEqual(session.Editor.PaintRegion(ERoiPurpose.Include)!.AreaPixels, session.Effective!.AreaPixels);
        using (var frame = session.CreateFrame())
        {
            Assert.AreSame(session.Effective, frame.Overlay!.Layers.Single().Visuals.Single().Geometry);
        }

        session.ShowEffective = false;
        using (var hidden = session.CreateFrame())
        {
            Assert.AreEqual(0, hidden.Overlay!.Layers.Single().Visuals.Count);
        }

        session.Editor.Load(new RoiDocument(new[] { new RoiDefinition("线", new ContourGeometry(new[] { new PointD(1, 1), new PointD(5, 5) })) }));
        Assert.IsNull(session.Effective);
        StringAssert.Contains(session.Status, "线");
    }

    /// <summary>画布显示图像时为编辑器设置涂抹范围；左键拖动画笔经画布提交一次涂抹。</summary>
    [TestMethod]
    public void CanvasDrivesBrush()
    {
        using var core = new CanvasCore<object>(() => { }, () => { }, () => (400, 300), _ => { });
        using var image = VisionImage.CopyFrom(new ImageInfo(40, 30, EPixelLayout.Gray8), new byte[1200]);
        using var frame = new CanvasFrame("a", 1, image);
        var editor = new RoiEditor { Tool = ERoiTool.Brush };
        core.Editor = editor;
        core.Present(frame);
        Assert.AreEqual(40, editor.PaintWidth);
        var view = core.Viewport;
        PointD Screen(double x, double y) => new PointD(view.Origin.X + x * view.Scale, view.Origin.Y + y * view.Scale);
        Assert.IsTrue(core.MouseDown(ECanvasButton.Left, Screen(10, 10), 1).Capture);
        core.MouseMove(Screen(20, 10));
        core.MouseUp(ECanvasButton.Left, Screen(20, 10), 4, 4);
        Assert.IsTrue(editor.PaintRegion(ERoiPurpose.Include)!.Contains(new PointD(15.5, 10.5)));
        Assert.IsTrue(editor.CanUndo);
    }

    /// <summary>工具下拉列表覆盖每个工具恰好一次；面积列表不含折线和点；画笔/橡皮标记为涂抹工具。</summary>
    [TestMethod]
    public void ToolChoicesCoverEveryTool()
    {
        CollectionAssert.AreEquivalent(
            Enum.GetValues(typeof(ERoiTool)).Cast<ERoiTool>().ToList(),
            RoiToolChoice.All.Select(c => c.Tool).ToList()
        );
        Assert.IsNull(RoiToolChoice.Find(RoiToolChoice.Areas, ERoiTool.Polyline));
        Assert.IsNull(RoiToolChoice.Find(RoiToolChoice.Areas, ERoiTool.Point));
        Assert.IsTrue(RoiToolChoice.Find(RoiToolChoice.Areas, ERoiTool.Brush)!.IsPaint);
        Assert.IsFalse(RoiToolChoice.Find(RoiToolChoice.Areas, ERoiTool.Polygon)!.IsPaint);
        Assert.AreEqual("多边形", RoiToolChoice.Find(RoiToolChoice.All, ERoiTool.Polygon)!.ToString());
    }
}
