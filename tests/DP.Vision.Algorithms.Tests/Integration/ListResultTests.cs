using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>列表结果的排序与首个元素：连通域按特征或业务坐标排序，条码给出各自位置。</summary>
[TestClass]
public sealed class ListResultTests
{
    private static BlobObservation Blob(int row, int start, int length) =>
        new BlobObservation(new RegionGeometry(new[] { new RegionRun(row, start, start + length) }));

    /// <summary>面积降序、质心升序；同值保持输入顺序；First 为排序首个。</summary>
    [TestMethod]
    public void BlobSelector_SortsSelection_AndFirstIsSortedHead()
    {
        var a = Blob(0, 0, 1); var b = Blob(0, 10, 3); var c = Blob(5, 20, 2); var d = Blob(9, 30, 3);
        var input = new BlobAnalysisResult("frame", new[] { a, b, c, d });
        var selector = new BlobSelector();
        var byArea = selector.Select(input, new BlobSelectionOptions(sortKey: EBlobSortKey.Area, descending: true));
        CollectionAssert.AreEqual(new[] { b, d, c, a }, byArea.Blobs.ToArray());
        Assert.AreSame(b, byArea.First);
        var byY = selector.Select(input, new BlobSelectionOptions(sortKey: EBlobSortKey.CentroidY, descending: true));
        CollectionAssert.AreEqual(new[] { d, c, a, b }, byY.Blobs.ToArray());
        CollectionAssert.AreEqual(new[] { a, b, c, d }, selector.Select(input, new BlobSelectionOptions()).Blobs.ToArray());
        Assert.IsNull(selector.Select(input, new BlobSelectionOptions(minimumArea: 100)).First);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BlobSelectionOptions(sortKey: (EBlobSortKey)99));
    }

    /// <summary>绑定坐标系时质心按业务坐标排序：工件旋转180°后“X升序”与原图顺序相反。</summary>
    [TestMethod]
    public void BlobSelector_SortsCentroidsInBoundCoordinates()
    {
        var left = Blob(4, 2, 2); var middle = Blob(4, 10, 2); var right = Blob(4, 20, 2);
        var system = TestCoordinates.FromPose("part", "frame", 32, 16, new TemplatePoseTransform(4, 4, new PointD(16, 8), Math.PI, 1));
        var input = new BlobAnalysisResult("frame", new[] { middle, left, right }).InCoordinates(system);
        var sorted = new BlobSelector().Select(input, new BlobSelectionOptions(sortKey: EBlobSortKey.CentroidX));
        CollectionAssert.AreEqual(new[] { right, middle, left }, sorted.Blobs.ToArray());
        Assert.AreSame(system, sorted.CoordinateSystem);
    }

    /// <summary>条码位置取定位点平均值；Bounds 仍是搜索范围；引擎不给定位点时位置为空。</summary>
    [TestMethod]
    public void BarcodeObservation_LocationIsLocatorAverage()
    {
        var bounds = new PixelBounds(0, 0, 100, 50);
        var code = new BarcodeObservation("A", "QR_CODE", bounds, null, "", new[] { new PointD(10, 10), new PointD(30, 10), new PointD(10, 30) });
        Assert.AreEqual(3, code.LocatorPoints.Count);
        Assert.AreEqual(50d / 3, code.Location!.Value.X, 1e-12); Assert.AreEqual(50d / 3, code.Location.Value.Y, 1e-12);
        Assert.AreEqual(bounds, code.Bounds);
        var unlocated = new BarcodeObservation("B", "CODE_128", bounds);
        Assert.AreEqual(0, unlocated.LocatorPoints.Count); Assert.IsNull(unlocated.Location);
    }
}
