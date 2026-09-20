using System;
using System.Linq;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>已提交轮廓的拓扑编辑保留源证据、顺序及事务边界。</summary>
[TestClass]
public sealed class RoiVertexTests
{
    private static RoiEditor Editor(ContourGeometry shape)
    {
        var editor = new RoiEditor();
        editor.Load(new RoiDocument(new[] { new RoiDefinition("path", shape, ERoiPurpose.Exclude, false) }));
        editor.Select("path");
        return editor;
    }

    private static ContourGeometry Shape(RoiEditor editor)
    {
        return (ContourGeometry)editor.Document.Rois[0].Shape;
    }

    /// <summary>插入点投影到原始边，保留不可变源和元数据。</summary>
    [TestMethod]
    public void InsertProjectsAndUndoes()
    {
        var source = new ContourGeometry(new[] { new PointD(.25, .75), new PointD(10.25, .75) });
        var editor = Editor(source);
        var before = editor.Document;
        int commits = 0;
        editor.DocumentChanged += (_, e) =>
        {
            commits++;
            Assert.AreEqual("insert-vertex", e.Operation);
        };
        Assert.IsTrue(editor.InsertVertex(new PointD(5.25, 1.75), 1));
        Assert.AreEqual(1, commits);
        Assert.AreEqual(3, Shape(editor).Points.Count);
        Assert.AreEqual(.75, Shape(editor).Points[1].Y);
        Assert.AreEqual(5.25, Shape(editor).Points[1].X);
        Assert.AreEqual(2, source.Points.Count);
        Assert.IsFalse(editor.Document.Rois[0].Enabled);
        Assert.AreEqual(ERoiPurpose.Exclude, editor.Document.Rois[0].Purpose);
        // 使用独立历史测试实例，因为上面的事件特意断言插入操作。
        var history = Editor(source);
        var original = history.Document;
        history.InsertVertex(new PointD(5.25, .75), 0);
        history.Undo();
        Assert.AreSame(original, history.Document);
        history.Redo();
        Assert.AreEqual(3, Shape(history).Points.Count);
        Assert.AreEqual(2, ((ContourGeometry)before.Rois[0].Shape).Points.Count);
    }

    /// <summary>闭合边可编辑，开放路径不隐式连接末点与首点。</summary>
    [TestMethod]
    public void ClosingEdgeRespectsTopology()
    {
        var points = new[] { new PointD(0, 0), new PointD(10, 0), new PointD(10, 10) };
        var closed = Editor(new ContourGeometry(points, true, true));
        Assert.IsTrue(closed.InsertVertex(new PointD(5, 5), 0));
        Assert.AreEqual(5d, Shape(closed).Points[3].X);
        Assert.IsTrue(Shape(closed).Filled);
        var open = Editor(new ContourGeometry(points));
        Assert.IsFalse(open.InsertVertex(new PointD(5, 5), 0));
        Assert.IsFalse(open.CanUndo);
    }

    /// <summary>删除首顶点时，显式重复的闭合端点仍保持显式闭合。</summary>
    [TestMethod]
    public void RepeatedClosureRemainsConsistent()
    {
        var editor = Editor(
            new ContourGeometry(
                new[]
                {
                    new PointD(0, 0),
                    new PointD(10, 0),
                    new PointD(10, 10),
                    new PointD(0, 10),
                    new PointD(0, 0),
                },
                true
            )
        );
        Assert.IsTrue(editor.DeleteVertex(new PointD(0, 0), 0));
        var shape = Shape(editor);
        Assert.AreEqual(4, shape.Points.Count);
        Assert.AreEqual(10d, shape.Points[0].X);
        Assert.AreEqual(shape.Points[0].X, shape.Points[3].X);
        Assert.AreEqual(shape.Points[0].Y, shape.Points[3].Y);
        Assert.IsFalse(editor.DeleteVertex(shape.Points[0], 0));
        Assert.IsNotNull(editor.ValidationError);
    }

    /// <summary>顶点不足的路径修改被拒绝，不改变历史或文档。</summary>
    [TestMethod]
    public void MinimumAndMissDoNotCommit()
    {
        var editor = Editor(new ContourGeometry(new[] { new PointD(0, 0), new PointD(10, 0) }));
        var before = editor.Document;
        Assert.IsFalse(editor.DeleteVertex(new PointD(0, 0), 0));
        Assert.AreSame(before, editor.Document);
        Assert.IsFalse(editor.CanUndo);
        Assert.IsFalse(editor.InsertVertex(new PointD(0, 0), 1));
        Assert.IsFalse(editor.InsertVertex(new PointD(5, 10), 1));
        Assert.AreSame(before, editor.Document);
    }

    /// <summary>容量不足时保留配置和重做分支。</summary>
    [TestMethod]
    public void CapacityRejectionPreservesRedo()
    {
        var editor = Editor(new ContourGeometry(Enumerable.Range(0, 4096).Select(i => new PointD(i * 2, 0))));
        Assert.IsTrue(editor.DeleteVertex(new PointD(2, 0), 0));
        editor.Undo();
        var before = editor.Document;
        Assert.IsTrue(editor.CanRedo);
        Assert.IsFalse(editor.InsertVertex(new PointD(1, 0), 0));
        Assert.AreSame(before, editor.Document);
        Assert.IsNotNull(editor.ValidationError);
        Assert.IsTrue(editor.CanRedo);
    }

    /// <summary>即使单轮廓未超限，仍执行配置总预算检查。</summary>
    [TestMethod]
    public void AggregateBudgetRejectionIsAtomic()
    {
        var contour = new ContourGeometry(Enumerable.Range(0, 4000).Select(i => new PointD(i, 0)));
        var editor = new RoiEditor();
        editor.Load(
            new RoiDocument(Enumerable.Range(0, 25).Select(i => new RoiDefinition("r" + i, contour)))
        );
        editor.Select("r0");
        var before = editor.Document;
        Assert.IsFalse(editor.InsertVertex(new PointD(.5, 0), 0));
        Assert.AreSame(before, editor.Document);
        Assert.IsFalse(editor.CanUndo);
        Assert.IsNotNull(editor.ValidationError);
    }

    /// <summary>只有选中配置可编辑，即使其他轮廓距离很近。</summary>
    [TestMethod]
    public void SelectionIsRequired()
    {
        var editor = Editor(new ContourGeometry(new[] { new PointD(0, 0), new PointD(10, 0) }));
        editor.Select(null);
        Assert.IsFalse(editor.InsertVertex(new PointD(5, 0), 1));
        Assert.IsFalse(editor.CanUndo);
    }

    /// <summary>顶点工具动作立即完成，并共用文档历史。</summary>
    [TestMethod]
    public void ToolsCommitOneAction()
    {
        var editor = Editor(new ContourGeometry(new[] { new PointD(0, 0), new PointD(10, 0) }));
        editor.Tool = ERoiTool.InsertVertex;
        Assert.IsTrue(editor.PointerDown(new PointD(5, 0), 0));
        editor.PointerUp(new PointD(6, 1));
        Assert.IsFalse(editor.IsEditing);
        Assert.AreEqual(5d, Shape(editor).Points[1].X);
        editor.Tool = ERoiTool.DeleteVertex;
        Assert.IsTrue(editor.PointerDown(new PointD(5, 0), 0));
        Assert.AreEqual(2, Shape(editor).Points.Count);
        editor.Undo();
        Assert.AreEqual(3, Shape(editor).Points.Count);
    }

    /// <summary>无效容差在改变任何当前手势前被拒绝。</summary>
    [TestMethod]
    public void InvalidToleranceDoesNotCancel()
    {
        var editor = Editor(new ContourGeometry(new[] { new PointD(0, 0), new PointD(10, 0) }));
        editor.PointerDown(new PointD(5, 0), 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => editor.InsertVertex(new PointD(5, 0), -1));
        Assert.IsTrue(editor.IsEditing);
    }
}
