using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>鲁棒圆拟合：剔除离群点后圆心、半径精确；共线或证据不足明确失败。</summary>
[TestClass]
public sealed class RobustCircleFitterTests
{
    /// <summary>圆上12点加3个离群点：离群点不进入内点，圆心半径精确，局部坐标按尺度换算。</summary>
    [TestMethod]
    public void Fit_RejectsOutliers_AndRecoversCircle()
    {
        var points = Enumerable.Range(0, 12).Select(i => i * Math.PI / 6)
            .Select(a => new PointD(40.25 + 17.5 * Math.Cos(a), 30.75 + 17.5 * Math.Sin(a))).ToList();
        points.Add(new PointD(40, 30)); points.Add(new PointD(80, 10)); points.Add(new PointD(45, 49));
        var result = new RobustCircleFitter().Fit("frame", points, distanceThreshold: .2);
        Assert.AreEqual(12, result.InlierCount);
        CollectionAssert.AreEqual(Enumerable.Range(0, 12).ToArray(), result.InlierIndices.ToArray());
        Assert.AreEqual(40.25, result.Center.X, 1e-9); Assert.AreEqual(30.75, result.Center.Y, 1e-9);
        Assert.AreEqual(17.5, result.Radius, 1e-9); Assert.AreEqual(35, result.Diameter, 1e-9);
        Assert.AreEqual(0, result.RmsError, 1e-9);
        Assert.IsNull(result.LocalRadius);
        var system = new VisionCoordinateSystem(new VisionCoordinateDefinition("part", "零件"), "frame", 100, 60,
            CoordinateMatrix2D.FromAffine(2, 0, 0, 0, 2, 0), "test");
        var local = result.InCoordinates(system);
        Assert.AreEqual(8.75, local.LocalRadius!.Value, 1e-9);
        Assert.AreEqual(20.125, local.LocatedCenter!.LocalPosition.X, 1e-9);
        Assert.AreEqual("frame", local.MeasuredCenter.FrameId);
    }

    /// <summary>带噪声的圆弧（四分之一圆）仍能稳定拟合。</summary>
    [TestMethod]
    public void Fit_QuarterArcWithNoise_StaysClose()
    {
        var points = Enumerable.Range(0, 20).Select(i => i * Math.PI / 2 / 19)
            .Select((a, i) => new PointD(100 + (50 + (i % 2 == 0 ? .05 : -.05)) * Math.Cos(a), 100 + 50 * Math.Sin(a))).ToArray();
        var result = new RobustCircleFitter().Fit("frame", points, distanceThreshold: .5);
        Assert.AreEqual(20, result.InlierCount);
        Assert.AreEqual(100, result.Center.X, .2); Assert.AreEqual(100, result.Center.Y, .2); Assert.AreEqual(50, result.Radius, .2);
    }

    /// <summary>共线点没有有效圆；内点不足或参数越界明确报错。</summary>
    [TestMethod]
    public void Fit_RejectsDegenerateOrInsufficientEvidence()
    {
        var fitter = new RobustCircleFitter();
        var line = Enumerable.Range(0, 6).Select(i => new PointD(i, 2 * i)).ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(() => fitter.Fit("frame", line));
        var sparse = new[] { new PointD(0, 0), new PointD(10, 0), new PointD(0, 10), new PointD(50, 50) };
        Assert.ThrowsExactly<InvalidOperationException>(() => fitter.Fit("frame", sparse, .1, minimumInliers: 4));
        Assert.ThrowsExactly<ArgumentException>(() => fitter.Fit("frame", sparse.Take(2).ToArray()));
        Assert.ThrowsExactly<ArgumentException>(() => fitter.Fit("frame", sparse, distanceThreshold: 0));
    }
}
