using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>画布交互共用逻辑：视口操作只取消拖动、多边形右键结束、标注避让。</summary>
[TestClass]
public sealed class CanvasInteractionTests
{
    /// <summary>平移/缩放时保留逐点绘制中的多边形顶点，随后仍可结束为闭合填充轮廓。</summary>
    [TestMethod]
    public void ViewChangeKeepsPendingPolygon()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Polygon };
        editor.PointerDown(new PointD(10, 10), 1);
        editor.PointerDown(new PointD(40, 10), 1);
        editor.PointerDown(new PointD(40, 30), 1);
        editor.CancelDrag();
        Assert.IsTrue(editor.IsEditing);

        // 画布右键单击调用Finish：最后一点与首点闭合。
        Assert.IsTrue(editor.Finish());
        var contour = (ContourGeometry)editor.Document.Rois[0].Shape;
        Assert.IsTrue(contour.Closed);
        Assert.IsTrue(contour.Filled);
        Assert.AreEqual(3, contour.Points.Count);
    }

    /// <summary>进行中的拖动在视口变化时照旧取消，不提交文档。</summary>
    [TestMethod]
    public void ViewChangeCancelsDrag()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Rectangle };
        editor.PointerDown(new PointD(10, 10), 1);
        editor.PointerMove(new PointD(40, 30));
        editor.CancelDrag();
        Assert.IsFalse(editor.IsEditing);
        editor.PointerUp(new PointD(40, 30));
        Assert.AreEqual(0, editor.Document.Rois.Count);
    }

    /// <summary>没有待定顶点时右键单击不做任何事。</summary>
    [TestMethod]
    public void FinishWithoutPendingVerticesDoesNothing()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Polygon };
        Assert.IsFalse(editor.Finish());
        Assert.AreEqual(0, editor.Document.Rois.Count);
    }

    /// <summary>同一位置的标注依次下移，互不重叠；不相交的标注保持原位。</summary>
    [TestMethod]
    public void CaptionsAtSameAnchorStack()
    {
        var layout = new CaptionLayout();
        Assert.AreEqual(10.0, layout.Place(5, 10, 80, 16));
        Assert.AreEqual(26.0, layout.Place(5.4, 10.2, 40, 16));
        Assert.AreEqual(42.0, layout.Place(5, 10, 80, 16));
        Assert.AreEqual(10.0, layout.Place(200, 10, 80, 16));
    }
}
