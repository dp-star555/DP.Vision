using System;
using System.Linq;
using System.Xml;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>事务、几何不变量、历史及严格配置持久化测试。</summary>
[TestClass]
public sealed class RoiEditorTests
{
    private static RoiEditor Rectangle()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Rectangle };
        editor.PointerDown(new PointD(10, 10), 1);
        editor.PointerUp(new PointD(40, 30));
        return editor;
    }

    /// <summary>拖动预览不修改已提交快照，一次释放只产生一条历史。</summary>
    [TestMethod]
    public void PreviewIsNotCommitted()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Rectangle };
        int changes = 0;
        editor.DocumentChanged += (_, __) => changes++;
        var before = editor.Document;
        editor.PointerDown(new PointD(10, 10), 1);
        editor.PointerMove(new PointD(40, 30));
        Assert.AreSame(before, editor.Document);
        Assert.AreEqual(1, editor.DisplayLayer().Visuals.Count);
        Assert.AreEqual(0, changes);
        editor.PointerUp(new PointD(40, 30));
        Assert.AreEqual(1, changes);
        Assert.AreEqual(1, editor.Document.Rois.Count);
        Assert.AreEqual(ERoiConstraint.AxisAligned, editor.Document.Rois[0].Constraint);
        editor.Undo();
        Assert.AreEqual(0, editor.Document.Rois.Count);
        Assert.IsFalse(editor.CanUndo);
    }

    /// <summary>Escape取消创建而不改变历史。</summary>
    [TestMethod]
    public void CancelCreation()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Ellipse };
        editor.PointerDown(new PointD(0, 0), 1);
        editor.PointerMove(new PointD(10, 20));
        editor.Cancel();
        Assert.AreEqual(0, editor.Document.Rois.Count);
        Assert.IsFalse(editor.CanUndo);
        Assert.IsFalse(editor.IsEditing);
    }

    /// <summary>零面积拖动或单击不会创建无效矩形。</summary>
    [TestMethod]
    public void EmptyCreationIsIgnored()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Rectangle };
        editor.PointerDown(new PointD(2, 2), 1);
        editor.PointerUp(new PointD(2, 2));
        Assert.AreEqual(0, editor.Document.Rois.Count);
    }

    /// <summary>移动配置保留标识、约束及原始几何。</summary>
    [TestMethod]
    public void MoveIsImmutable()
    {
        var editor = Rectangle();
        var original = editor.Document.Rois[0];
        editor.PointerDown(new PointD(25, 20), 1);
        editor.PointerMove(new PointD(28, 24));
        Assert.AreSame(original, editor.Document.Rois[0]);
        editor.PointerUp(new PointD(28, 24));
        var moved = (RectangleGeometry)editor.Document.Rois[0].Shape;
        Assert.AreEqual(28.0, moved.Center.X);
        Assert.AreEqual(24.0, moved.Center.Y);
        Assert.AreEqual(original.Id, editor.Document.Rois[0].Id);
        Assert.AreEqual(25.0, ((RectangleGeometry)original.Shape).Center.X);
        editor.Undo();
        Assert.AreSame(original, editor.Document.Rois[0]);
        editor.Redo();
        Assert.AreEqual(28.0, ((RectangleGeometry)editor.Document.Rois[0].Shape).Center.X);
    }

    /// <summary>取消移动后，原文档及事件次数保持不变。</summary>
    [TestMethod]
    public void CancelMove()
    {
        var editor = Rectangle();
        var before = editor.Document;
        int events = 0;
        editor.DocumentChanged += (_, __) => events++;
        editor.PointerDown(new PointD(25, 20), 1);
        editor.PointerMove(new PointD(50, 50));
        editor.Cancel();
        Assert.AreSame(before, editor.Document);
        Assert.AreEqual(0, events);
    }

    /// <summary>缩放旋转矩形时保持对角点固定。</summary>
    [TestMethod]
    public void RotatedResizeAnchorsOppositeCorner()
    {
        var shape = new RectangleGeometry(new PointD(100, 100), 40, 20, .4);
        var editor = new RoiEditor();
        editor.Load(new RoiDocument(new[] { new RoiDefinition("r", shape) }));
        editor.Select("r");
        var corner = editor.Handles(5).Single(h => h.Index == 4).Position;
        editor.PointerDown(corner, 1);
        editor.PointerUp(new PointD(corner.X + 10, corner.Y + 5));
        var after = (RectangleGeometry)editor.Document.Rois[0].Shape;
        Assert.AreEqual(shape.Corners[0].X, after.Corners[0].X, 1e-9);
        Assert.AreEqual(shape.Corners[0].Y, after.Corners[0].Y, 1e-9);
        Assert.AreEqual(.4, after.Angle);
    }

    /// <summary>旋转使用专用控制点，并保留尺寸。</summary>
    [TestMethod]
    public void RotateRectangle()
    {
        var editor = new RoiEditor();
        editor.Load(
            new RoiDocument(
                new[] { new RoiDefinition("r", new RectangleGeometry(new PointD(100, 100), 40, 20)) }
            )
        );
        editor.Select("r");
        editor.PointerDown(editor.Handles(5).Single(h => h.Kind == ERoiHandleKind.Rotation).Position, 1);
        editor.PointerUp(new PointD(150, 100));
        var after = (RectangleGeometry)editor.Document.Rois[0].Shape;
        Assert.AreEqual(Math.PI / 2, after.Angle, 1e-9);
        Assert.AreEqual(40.0, after.Width);
        Assert.AreEqual(20.0, after.Height);
    }

    /// <summary>轴对齐矩形不提供旋转控制点。</summary>
    [TestMethod]
    public void AxisAlignedConstraint()
    {
        var editor = Rectangle();
        Assert.IsFalse(editor.Handles(5).Any(h => h.Kind == ERoiHandleKind.Rotation));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new RoiDefinition(
                "x",
                new RectangleGeometry(new PointD(0, 0), 10, 20, .5),
                constraint: ERoiConstraint.AxisAligned
            )
        );
    }

    /// <summary>调整圆大小不能意外变成椭圆。</summary>
    [TestMethod]
    public void CirclePreservesEqualRadii()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Circle };
        editor.PointerDown(new PointD(100, 100), 1);
        editor.PointerUp(new PointD(120, 100));
        Assert.AreEqual(ERoiConstraint.Circle, editor.Document.Rois[0].Constraint);
        editor.PointerDown(editor.Handles(5).Single().Position, 1);
        editor.PointerUp(new PointD(100, 150));
        var circle = (EllipseGeometry)editor.Document.Rois[0].Shape;
        Assert.AreEqual(50.0, circle.RadiusX);
        Assert.AreEqual(circle.RadiusX, circle.RadiusY);
        Assert.AreEqual(100.0, circle.Center.X);
    }

    /// <summary>完成多边形至少需要三个已确认顶点。</summary>
    [TestMethod]
    public void PolygonCreation()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Polygon };
        editor.PointerDown(new PointD(0, 0), 1);
        editor.PointerDown(new PointD(20, 0), 1);
        Assert.IsFalse(editor.Finish());
        editor.PointerDown(new PointD(20, 20), 1);
        Assert.IsTrue(editor.Finish());
        var polygon = (ContourGeometry)editor.Document.Rois[0].Shape;
        Assert.IsTrue(polygon.Closed && polygon.Filled);
        Assert.AreEqual(3, polygon.Points.Count);
        Assert.IsTrue(polygon.Contains(new PointD(15, 5)));
    }

    /// <summary>折线顶点拖动保留点顺序及开放状态。</summary>
    [TestMethod]
    public void VertexEdit()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Polyline };
        editor.PointerDown(new PointD(0, 0), 1);
        editor.PointerDown(new PointD(20, 0), 1);
        editor.PointerDown(new PointD(20, 20), 1);
        editor.Finish();
        var before = (ContourGeometry)editor.Document.Rois[0].Shape;
        editor.PointerDown(new PointD(20, 0), 1);
        editor.PointerUp(new PointD(22.125, 3.75));
        var after = (ContourGeometry)editor.Document.Rois[0].Shape;
        Assert.AreEqual(22.125, after.Points[1].X);
        Assert.AreEqual(3.75, after.Points[1].Y);
        Assert.AreEqual(20.0, before.Points[1].X);
        Assert.IsFalse(after.Closed);
    }

    /// <summary>退格和工具切换只影响待提交顶点，不改变已保存文档。</summary>
    [TestMethod]
    public void PendingVertexCancellation()
    {
        var editor = new RoiEditor { Tool = ERoiTool.Polygon };
        editor.PointerDown(new PointD(0, 0), 1);
        editor.PointerDown(new PointD(20, 0), 1);
        editor.Backspace();
        Assert.IsFalse(editor.Finish());
        editor.Tool = ERoiTool.Select;
        Assert.IsFalse(editor.IsEditing);
        Assert.AreEqual(0, editor.Document.Rois.Count);
    }

    /// <summary>新提交会清除重做分支，历史数量保持有界。</summary>
    [TestMethod]
    public void BoundedHistoryAndRedoBranch()
    {
        var editor = new RoiEditor(2);
        for (int i = 0; i < 4; i++)
        {
            editor.Tool = ERoiTool.Point;
            editor.PointerDown(new PointD(i * 10, 0), 1);
        }

        editor.Undo();
        editor.Undo();
        Assert.AreEqual(2, editor.Document.Rois.Count);
        Assert.IsFalse(editor.CanUndo);
        Assert.IsTrue(editor.CanRedo);
        editor.Tool = ERoiTool.Point;
        editor.PointerDown(new PointD(100, 0), 1);
        Assert.IsFalse(editor.CanRedo);
    }

    /// <summary>元数据修改及删除可撤销，不替换原始几何。</summary>
    [TestMethod]
    public void MetadataAndDelete()
    {
        var editor = Rectangle();
        var shape = editor.Document.Rois[0].Shape;
        editor.SetSelectedMetadata(ERoiPurpose.Exclude, false);
        Assert.AreSame(shape, editor.Document.Rois[0].Shape);
        Assert.IsFalse(editor.Document.Rois[0].Enabled);
        editor.DeleteSelected();
        Assert.AreEqual(0, editor.Document.Rois.Count);
        editor.Undo();
        Assert.AreEqual(ERoiPurpose.Exclude, editor.Document.Rois[0].Purpose);
    }

    /// <summary>整数Region平移保留精确面积及孔洞。</summary>
    [TestMethod]
    public void RegionMovePreservesMembership()
    {
        var region = new RegionGeometry(
            new[] { new RegionRun(10, 10, 20), new RegionRun(11, 10, 12), new RegionRun(11, 18, 20) }
        );
        var editor = new RoiEditor();
        editor.Load(new RoiDocument(new[] { new RoiDefinition("mask", region) }));
        editor.PointerDown(new PointD(15, 10.5), .1);
        editor.PointerUp(new PointD(18, 12.5));
        var moved = (RegionGeometry)editor.Document.Rois[0].Shape;
        Assert.AreEqual(region.AreaPixels, moved.AreaPixels);
        Assert.IsTrue(moved.Contains(new PointD(18, 12.5)));
        Assert.IsFalse(moved.Contains(new PointD(18, 13.5)));
    }

    /// <summary>交互容量超限时保留配置，而不是从原生鼠标处理器抛出异常。</summary>
    [TestMethod]
    public void CapacityRejectionIsTransactional()
    {
        var editor = new RoiEditor(1);
        editor.Load(
            new RoiDocument(
                Enumerable
                    .Range(0, 512)
                    .Select(i => new RoiDefinition("p" + i, new ContourGeometry(new[] { new PointD(i, 0) })))
            )
        );
        var before = editor.Document;
        editor.Tool = ERoiTool.Point;
        Assert.IsFalse(editor.PointerDown(new PointD(600, 0), 1));
        Assert.AreSame(before, editor.Document);
        Assert.IsNotNull(editor.ValidationError);
        Assert.IsFalse(editor.CanUndo);
    }

    /// <summary>无效Region移动不改变最后提交的像素集合。</summary>
    [TestMethod]
    public void InvalidRegionMoveIsRejected()
    {
        var editor = new RoiEditor();
        editor.Load(
            new RoiDocument(
                new[]
                {
                    new RoiDefinition(
                        "r",
                        new RegionGeometry(new[] { new RegionRun(ImageInfo.MaxDimension, 0, 10) })
                    ),
                }
            )
        );
        var before = editor.Document;
        editor.PointerDown(new PointD(5, ImageInfo.MaxDimension + .5), .1);
        editor.PointerUp(new PointD(5, ImageInfo.MaxDimension + 2.5));
        Assert.AreSame(before, editor.Document);
        Assert.IsNotNull(editor.ValidationError);
        Assert.IsFalse(editor.IsEditing);
    }

    /// <summary>持久化保留亚像素、约束、禁用状态、空对象和Region游程。</summary>
    [TestMethod]
    public void XmlRoundTrip()
    {
        var document = new RoiDocument(
            new[]
            {
                new RoiDefinition(
                    "circle",
                    new EllipseGeometry(new PointD(12.125, 25.75), 3.25, 3.25),
                    ERoiPurpose.Exclude,
                    false,
                    ERoiConstraint.Circle
                ),
                new RoiDefinition("empty-region", new RegionGeometry(Array.Empty<RegionRun>())),
                new RoiDefinition(
                    "xld",
                    new ContourGeometry(new[] { new PointD(.123456789012345, 2.875), new PointD(20, 30) })
                ),
                new RoiDefinition("region", new RegionGeometry(new[] { new RegionRun(-2, 3, 7) })),
            }
        );
        var copy = RoiDocumentXml.Deserialize(RoiDocumentXml.Serialize(document));
        Assert.AreEqual(4, copy.Rois.Count);
        Assert.AreEqual(ERoiConstraint.Circle, copy.Rois[0].Constraint);
        Assert.AreEqual(ERoiPurpose.Exclude, copy.Rois[0].Purpose);
        Assert.IsFalse(copy.Rois[0].Enabled);
        Assert.AreEqual(.123456789012345, ((ContourGeometry)copy.Rois[2].Shape).Points[0].X);
        Assert.AreEqual(4L, ((RegionGeometry)copy.Rois[3].Shape).AreaPixels);
    }

    /// <summary>禁止DTD及实体处理。</summary>
    [TestMethod]
    public void XmlRejectsEntities()
    {
        Assert.ThrowsExactly<XmlException>(() =>
            RoiDocumentXml.Deserialize(
                "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///never-read'>]><roi-document version='1' coordinates='image-edges'>&e;</roi-document>"
            )
        );
    }

    /// <summary>不能静默加载未知版本、坐标系或重复标识。</summary>
    [TestMethod]
    public void InvalidDocumentsRejected()
    {
        Assert.ThrowsExactly<FormatException>(() =>
            RoiDocumentXml.Deserialize("<roi-document version='2' coordinates='image-edges'/>")
        );
        Assert.ThrowsExactly<FormatException>(() =>
            RoiDocumentXml.Deserialize("<roi-document version='1' coordinates='pixel-centers'/>")
        );
        var roi = new RoiDefinition("same", new ContourGeometry(Array.Empty<PointD>()));
        Assert.ThrowsExactly<ArgumentException>(() => new RoiDocument(new[] { roi, roi }));
    }
}
