using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>结果按角色给出显示项，图上只写短标签；旧的显示几何由显示项统一导出。</summary>
[TestClass]
public sealed class VisionDisplayTests
{
    /// <summary>距离：两个最近点为标记，测量线带“数值 单位”的短标签；为零时只剩一个点。</summary>
    [TestMethod]
    public void DistanceShowsMarkersAndLabeledMeasurement()
    {
        VisionPoint P(double x, double y) => new VisionPoint("frame", new PointD(x, y));
        var measurer = new GeometryMeasurer();
        var items = measurer.PointToPoint(P(0, 0), P(3, 4)).DisplayItems;
        Assert.AreEqual(3, items.Count);
        Assert.AreEqual(2, items.Count(i => i.Marker.HasValue && i.Role == EVisionDisplayRole.Result));
        var line = items.Single(i => i.Role == EVisionDisplayRole.Measurement);
        Assert.AreEqual("5.000 image-px", line.Label);
        Assert.IsNull(line.Detail);
        var touching = measurer.PointToPoint(P(1, 1), P(1, 1));
        Assert.AreEqual(1, touching.DisplayItems.Count);
        Assert.AreEqual(1, touching.DisplayGeometry.Count);
        Assert.IsTrue(touching.DisplayItems[0].Marker.HasValue);
    }

    /// <summary>坐标轴：原点标记带标签，X、Y 轴各有角色；标签不能写成长摘要。</summary>
    [TestMethod]
    public void AxesAndLabelLimits()
    {
        var axes = VisionDisplay.Axes(CoordinateMatrix2D.FromAffine(0, -1, 10, 1, 0, 20), 5, "工件/v1").ToArray();
        Assert.AreEqual(new PointD(10, 20), axes[0].Marker!.Value);
        Assert.AreEqual("工件/v1", axes[0].Label);
        var x = (ContourGeometry)axes.Single(a => a.Role == EVisionDisplayRole.AxisX).Geometry!;
        Assert.AreEqual(10, x.Points[1].X, 1e-9); Assert.AreEqual(25, x.Points[1].Y, 1e-9);
        Assert.IsNotNull(axes.Single(a => a.Role == EVisionDisplayRole.AxisY).Geometry);
        Assert.ThrowsExactly<ArgumentException>(() => VisionDisplayItem.Point(new PointD(0, 0), label: new string('x', 65)));
        Assert.IsInstanceOfType(VisionDisplayItem.Point(new PointD(1, 2)).ToGeometry(), typeof(EllipseGeometry));
    }
}
