using System;
using System.Linq;
using System.Threading;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>UI编辑与不可变后台Region的边界测试。</summary>
[TestClass]
public sealed class RoiLayerTests
{
    /// <summary>后台程序集不导出编辑器类型，也不引用UI。</summary>
    [TestMethod]
    public void RoiTypesBelongOnlyToUiAssembly()
    {
        var core = typeof(RegionGeometry).Assembly;
        Assert.IsFalse(
            core.GetExportedTypes()
                .Any(t => t.Name.StartsWith("Roi", StringComparison.Ordinal) || t.Name == "IVisionCanvas")
        );
        Assert.IsFalse(core.GetReferencedAssemblies().Any(a => a.Name == "DP.Vision.UI"));
        Assert.AreEqual("DP.Vision.UI", typeof(RoiEditor).Assembly.GetName().Name);
        Assert.AreEqual(typeof(RoiEditor).Assembly, typeof(RoiDefinition).Assembly);
        Assert.AreEqual(typeof(RoiEditor).Assembly, typeof(IVisionCanvas).Assembly);
    }

    /// <summary>后台快照不受后续选择或编辑文档替换影响。</summary>
    [TestMethod]
    public void ConfirmedRegionDoesNotTrackEditorChanges()
    {
        var roi = new RoiDefinition("a", new RectangleGeometry(new PointD(3, 3), 4, 4));
        var editor = new RoiEditor();
        editor.Load(new RoiDocument(new[] { roi }));
        var region = roi.ToRegion(20, 20);
        Assert.AreEqual(16L, region.AreaPixels);
        var next = new RoiDefinition("a", new RectangleGeometry(new PointD(12, 12), 2, 2));
        var other = next.ToRegion(20, 20);
        Assert.IsTrue(region.Contains(new PointD(1.5, 1.5)));
        Assert.IsFalse(other.Contains(new PointD(1.5, 1.5)));
        editor.Load(new RoiDocument(Array.Empty<RoiDefinition>()));
        Assert.AreEqual(16L, region.AreaPixels);
    }

    /// <summary>复制已有Region拓扑，不填孔，也不通过外接矩形合并对象。</summary>
    [TestMethod]
    public void RegionHolesSurviveConfirmation()
    {
        var shape = new RegionGeometry(
            new[]
            {
                new RegionRun(1, 1, 5),
                new RegionRun(2, 1, 2),
                new RegionRun(2, 4, 5),
                new RegionRun(3, 1, 5),
            }
        );
        var copy = new RoiDefinition("hole", shape).ToRegion(10, 10);
        Assert.AreNotSame(shape, copy);
        Assert.AreEqual(shape.AreaPixels, copy.AreaPixels);
        Assert.IsFalse(copy.Contains(new PointD(2.5, 2.5)));
    }

    /// <summary>旋转和椭圆Region按真实形状成员关系生成，不按外接矩形生成。</summary>
    [TestMethod]
    public void EllipseIsNotItsBoundingBox()
    {
        var region = new RoiDefinition("ellipse", new EllipseGeometry(new PointD(5, 5), 4, 2)).ToRegion(
            10,
            10
        );
        Assert.IsTrue(region.AreaPixels > 0 && region.AreaPixels < 32);
        Assert.IsFalse(region.Contains(new PointD(1.5, 3.5)));
    }

    /// <summary>只有显式填充的轮廓才能转换为像素区域。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ContourFillMustBeExplicit(bool closed, bool filled)
    {
        var roi = new RoiDefinition(
            "polygon",
            new ContourGeometry(
                new[] { new PointD(1, 1), new PointD(4, 1), new PointD(4, 4), new PointD(1, 4) },
                closed,
                filled
            )
        );
        if (filled)
        {
            Assert.AreEqual(9L, roi.ToRegion(10, 10).AreaPixels);
        }
        else
        {
            Assert.ThrowsExactly<ArgumentException>(() => roi.ToRegion(10, 10));
        }
    }

    /// <summary>转换不静默裁剪，不忽略取消，也不超过调用方工作预算。</summary>
    [TestMethod]
    public void InvalidSubmissionFailsExplicitly()
    {
        var roi = new RoiDefinition("a", new RectangleGeometry(new PointD(5, 5), 10, 10));
        Assert.ThrowsExactly<ArgumentException>(() => roi.ToRegion(9, 9));
        Assert.ThrowsExactly<ArgumentException>(() => roi.ToRegion(10, 10, 10));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            roi.ToRegion(10, 10, token: new CancellationToken(true))
        );
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new RoiDefinition("off", roi.Shape, enabled: false).ToRegion(10, 10)
        );
    }
}
