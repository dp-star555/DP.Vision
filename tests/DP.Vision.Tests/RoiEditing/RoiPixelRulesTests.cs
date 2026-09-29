using System;
using System.Linq;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>整像素编辑规则：创建、移动、八个控制点缩放、最小边长、原图边界及最小面积优先的选择。</summary>
[TestClass]
public sealed class RoiPixelRulesTests
{
    private const int W = 200,
        H = 120;

    private static RoiEditor Editor(params (int X, int Y, int W, int H)[] rects)
    {
        var editor = new RoiEditor { PixelRules = new RoiPixelRules(W, H) };
        editor.Load(
            new RoiDocument(
                rects.Select(
                    (r, i) =>
                        new RoiDefinition(
                            i.ToString(),
                            new RectangleGeometry(new PointD(r.X + r.W / 2.0, r.Y + r.H / 2.0), r.W, r.H),
                            ERoiConstraint.AxisAligned
                        )
                )
            )
        );
        return editor;
    }

    private static (int X, int Y, int W, int H) Rect(Geometry g)
    {
        var b = g.Bounds;
        return (
            (int)Math.Round(b.X),
            (int)Math.Round(b.Y),
            (int)Math.Round(b.Width),
            (int)Math.Round(b.Height)
        );
    }

    // 对照：标签检测图像视图原有的整像素编辑公式（点已取整并限制在原图内）。
    private static (int X, int Y, int W, int H) Oracle(
        (int X, int Y, int W, int H) box,
        int handle,
        int dx,
        int dy
    )
    {
        if (handle < 0)
        {
            return (
                Math.Max(0, Math.Min(W - box.W, box.X + dx)),
                Math.Max(0, Math.Min(H - box.H, box.Y + dy)),
                box.W,
                box.H
            );
        }

        int l = box.X,
            t = box.Y,
            r = l + box.W,
            b = t + box.H;
        if (handle == 0 || handle == 6 || handle == 7)
        {
            l = Math.Max(0, Math.Min(r - 4, l + dx));
        }

        if (handle == 2 || handle == 3 || handle == 4)
        {
            r = Math.Min(W, Math.Max(l + 4, r + dx));
        }

        if (handle == 0 || handle == 1 || handle == 2)
        {
            t = Math.Max(0, Math.Min(b - 4, t + dy));
        }

        if (handle == 4 || handle == 5 || handle == 6)
        {
            b = Math.Min(H, Math.Max(t + 4, b + dy));
        }

        return (l, t, r - l, b - t);
    }

    /// <summary>绘制按取整、限制在原图内的两角点成框；小于最小边长的不创建。</summary>
    [TestMethod]
    public void CreationSnapsClampsAndIgnoresTinyBoxes()
    {
        var editor = Editor();
        editor.Tool = ERoiTool.Rectangle;
        editor.PointerDown(new PointD(30.4, 20.6), 1);
        editor.PointerUp(new PointD(10.2, 250));
        Assert.AreEqual((10, 21, 20, 99), Rect(editor.Document.Rois.Single().Shape));

        editor.Tool = ERoiTool.Rectangle;
        editor.PointerDown(new PointD(50, 50), 1);
        editor.PointerMove(new PointD(53, 60));
        Assert.IsNotNull(editor.Preview);
        editor.PointerUp(new PointD(53, 60));
        Assert.AreEqual(1, editor.Document.Rois.Count);
    }

    /// <summary>移动与八个控制点缩放与原有整像素公式逐一一致（随机位移，含越界与反向拖过对边）。</summary>
    [TestMethod]
    public void EditsMatchPixelFormula()
    {
        var random = new Random(5);
        for (int trial = 0; trial < 300; trial++)
        {
            int w = random.Next(4, 80),
                h = random.Next(4, 60),
                x = random.Next(0, W - w + 1),
                y = random.Next(0, H - h + 1);
            int handle = random.Next(-1, 8);
            var editor = Editor((x, y, w, h));
            editor.Select("0");
            PointD start;
            if (handle < 0)
            {
                start = new PointD(x + w / 2, y + h / 2);
            }
            else
            {
                start = editor.Handles(1).Single(p => p.Index == handle).Position;
                start = new PointD(Math.Round(start.X), Math.Round(start.Y));
            }

            int dx = random.Next(-120, 121),
                dy = random.Next(-90, 91);
            var end = new PointD(start.X + dx, start.Y + dy);
            Assert.IsTrue(editor.PointerDown(start, 0.6), $"trial {trial}");
            editor.PointerUp(end);
            var clampedEnd = new PointD(Math.Max(0, Math.Min(W, end.X)), Math.Max(0, Math.Min(H, end.Y)));
            var expected = Oracle(
                (x, y, w, h),
                handle,
                (int)(clampedEnd.X - start.X),
                (int)(clampedEnd.Y - start.Y)
            );
            Assert.AreEqual(
                expected,
                Rect(editor.Document.Rois.Single().Shape),
                $"trial {trial} handle {handle}"
            );
        }
    }

    /// <summary>取整后未改变的拖动不产生历史；嵌套时选中面积最小的命中ROI。</summary>
    [TestMethod]
    public void UnchangedDragAndNestedSelection()
    {
        var editor = Editor((0, 0, 100, 100), (20, 20, 10, 10));
        editor.PointerDown(new PointD(25, 25), 0.5);
        Assert.AreEqual("1", editor.SelectedId);
        editor.PointerUp(new PointD(25.3, 25.2));
        Assert.IsFalse(editor.CanUndo);
        editor.PointerDown(new PointD(60, 60), 0.5);
        Assert.AreEqual("0", editor.SelectedId);
        editor.PointerUp(new PointD(60, 60));

        var continuous = new RoiEditor();
        continuous.Load(editor.Document);
        continuous.PointerDown(new PointD(25, 25), 0.5);
        Assert.AreEqual("1", continuous.SelectedId, "without pixel rules the topmost (last) ROI is hit");
    }

    /// <summary>整像素规则下命中按像素范围（含边缘）判断，点击框外附近不会选中；控制点仍按容差。</summary>
    [TestMethod]
    public void HitIsExactWhileHandlesUseTolerance()
    {
        var editor = Editor((20, 20, 30, 20));
        Assert.IsFalse(editor.PointerDown(new PointD(52, 30), 5));
        Assert.IsNull(editor.SelectedId);
        Assert.IsTrue(editor.PointerDown(new PointD(50, 40), 5));
        Assert.AreEqual("0", editor.SelectedId);
        editor.PointerUp(new PointD(50, 40));
        Assert.IsTrue(editor.PointerDown(new PointD(53, 43), 5), "bottom-right handle within tolerance");
        editor.PointerUp(new PointD(60, 50));
        Assert.AreEqual(
            (20, 20, 37, 27),
            Rect(editor.Document.Rois.Single().Shape),
            "edge moves by the pointer delta from where the handle was grabbed"
        );
    }
}
