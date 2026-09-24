using System;
using System.Linq;
using System.Xml;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>M4：重复首点的闭合轮廓拖动、参数名、XML无效内容统一为FormatException、撤销重做。</summary>
[TestClass]
public sealed class RoiCleanupTests
{
    private static RoiEditor EditorWithRepeatedClosure()
    {
        var points = new[] { new PointD(10, 10), new PointD(40, 10), new PointD(40, 30), new PointD(10, 10) };
        var editor = new RoiEditor();
        var contour = new ContourGeometry(points, closed: true, filled: true);
        editor.Load(new RoiDocument(new[] { new RoiDefinition("c", contour) }));
        editor.Select("c");
        return editor;
    }

    /// <summary>拖动首点时同步移动重复的尾点，闭合表示保留，顶点数不变。</summary>
    [TestMethod]
    public void DraggingFirstVertexKeepsRepeatedClosure()
    {
        var editor = EditorWithRepeatedClosure();
        Assert.IsTrue(editor.PointerDown(new PointD(10, 10), 1));
        editor.PointerUp(new PointD(5, 5));
        var contour = (ContourGeometry)editor.Document.Rois[0].Shape;
        Assert.AreEqual(4, contour.Points.Count);
        Assert.AreEqual(new PointD(5, 5), contour.Points[0]);
        Assert.AreEqual(new PointD(5, 5), contour.Points[3]);
        Assert.IsTrue(contour.RepeatsFirstPoint);
    }

    /// <summary>拖动中间顶点只移动该点。</summary>
    [TestMethod]
    public void DraggingMiddleVertexMovesOnlyThatPoint()
    {
        var editor = EditorWithRepeatedClosure();
        Assert.IsTrue(editor.PointerDown(new PointD(40, 10), 1));
        editor.PointerUp(new PointD(45, 12));
        var points = ((ContourGeometry)editor.Document.Rois[0].Shape).Points;
        CollectionAssert.AreEqual(
            new[] { new PointD(10, 10), new PointD(45, 12), new PointD(40, 30), new PointD(10, 10) },
            points.ToArray()
        );
    }

    /// <summary>撤销与重做互为镜像，历史计数正确。</summary>
    [TestMethod]
    public void UndoRedoMirror()
    {
        var editor = EditorWithRepeatedClosure();
        var loaded = editor.Document;
        editor.DeleteSelected();
        var deleted = editor.Document;
        editor.Undo();
        Assert.AreSame(loaded, editor.Document);
        Assert.IsTrue(editor.CanRedo);
        Assert.IsFalse(editor.CanUndo);
        editor.Redo();
        Assert.AreSame(deleted, editor.Document);
        Assert.IsTrue(editor.CanUndo);
        Assert.IsFalse(editor.CanRedo);
    }

    /// <summary>容差与定义校验报告实际出错的参数名。</summary>
    [TestMethod]
    public void ValidationReportsOffendingParameter()
    {
        var editor = new RoiEditor();
        Assert.AreEqual(
            "spacing",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.Handles(-1)).ParamName
        );
        Assert.AreEqual(
            "tolerance",
            Assert
                .ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    editor.PointerDown(new PointD(0, 0), double.NaN)
                )
                .ParamName
        );
        var shape = new RectangleGeometry(new PointD(5, 5), 4, 4);
        Assert.AreEqual(
            "id",
            Assert.ThrowsExactly<ArgumentException>(() => new RoiDefinition(" ", shape)).ParamName
        );
        Assert.AreEqual(
            "purpose",
            Assert
                .ThrowsExactly<ArgumentException>(() => new RoiDefinition("a", shape, (ERoiPurpose)9))
                .ParamName
        );
    }

    /// <summary>XML合法但内容无效时统一抛FormatException并保留原因；不是XML时仍为XmlException。</summary>
    [TestMethod]
    public void InvalidXmlContentIsFormatException()
    {
        const string head = "<roi-document version='1' coordinates='image-edges'>";
        const string tail = "</roi-document>";
        string Roi(string id, string shape) =>
            "<roi id='" + id + "' purpose='Include' enabled='true' constraint='None'>" + shape + "</roi>";

        var nan = Assert.ThrowsExactly<FormatException>(() =>
            RoiDocumentXml.Deserialize(
                head + Roi("a", "<rectangle cx='NaN' cy='1' width='2' height='2' angle='0'/>") + tail
            )
        );
        Assert.IsInstanceOfType<ArgumentException>(nan.InnerException);

        string rect = "<rectangle cx='5' cy='5' width='2' height='2' angle='0'/>";
        var duplicate = Assert.ThrowsExactly<FormatException>(() =>
            RoiDocumentXml.Deserialize(head + Roi("a", rect) + Roi("a", rect) + tail)
        );
        Assert.IsInstanceOfType<ArgumentException>(duplicate.InnerException);

        var overflow = Assert.ThrowsExactly<FormatException>(() =>
            RoiDocumentXml.Deserialize(
                head
                    + Roi("a", "<region><run row='99999999999' start='0' end-exclusive='1'/></region>")
                    + tail
            )
        );
        Assert.IsInstanceOfType<OverflowException>(overflow.InnerException);

        Assert.ThrowsExactly<XmlException>(() => RoiDocumentXml.Deserialize(head));
    }
}
