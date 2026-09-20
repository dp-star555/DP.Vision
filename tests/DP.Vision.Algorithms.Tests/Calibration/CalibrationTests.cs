using System;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>独立标定能力回归测试。</summary>
[TestClass]
public sealed class CalibrationTests
{
    /// <summary>验证大偏移九点标定。</summary>
    [TestMethod]
    public void NinePoints_RecoverAffineWithLargeSourceOffset()
    {
        var samples = Enumerable.Range(0, 9).Select(i =>
        {
            double x = 1000000 + i % 3 * 10, y = 2000000 + i / 3 * 20;
            return new CalibrationSample(new Coordinate2D(x, y), new Coordinate2D(2 * x + 3 * y + 7, -x + 4 * y - 11));
        }).ToArray();
        var result = CalibrationSolver.SolveAffine(samples);
        Assert.AreEqual(2, result.M11, 1e-9);
        Assert.AreEqual(3, result.M12, 1e-9);
        Assert.AreEqual(-1, result.M21, 1e-9);
        Assert.AreEqual(4, result.M22, 1e-9);
        Assert.AreEqual(7, result.Tx, 1e-6);
        Assert.AreEqual(-11, result.Ty, 1e-6);
        Assert.AreEqual(0, result.RmsError, 1e-6);
    }

    /// <summary>验证退化和非法输入拒绝。</summary>
    [TestMethod]
    public void DegenerateAndNonFiniteInputs_AreRejected()
    {
        var line = Enumerable.Range(0, 3).Select(i => new CalibrationSample(new Coordinate2D(i, i), new Coordinate2D(i, i))).ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(() => CalibrationSolver.SolveAffine(line));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Coordinate2D(double.NaN, 0));
        Assert.ThrowsExactly<ArgumentException>(() => CalibrationSolver.FitRotationCenter(new[] { new Coordinate2D(0, 0) }));
    }

    /// <summary>验证旋转中心和角度约定。</summary>
    [TestMethod]
    public void Rotation_UsesTargetCenterAndExplicitRadians()
    {
        var center = new Coordinate2D(10, 20);
        var circle = Enumerable.Range(0, 8).Select(i => new Coordinate2D(
            center.X + 5 * Math.Cos(i * Math.PI / 4), center.Y + 5 * Math.Sin(i * Math.PI / 4))).ToArray();
        var result = CalibrationSolver.SolveAffine(IdentitySamples(), circle);
        var rotated = result.TransformWithRotation(new Coordinate2D(15, 20), Math.PI / 2);
        Assert.AreEqual(10, rotated.X, 1e-9);
        Assert.AreEqual(25, rotated.Y, 1e-9);
        Assert.ThrowsExactly<InvalidOperationException>(() => CalibrationSolver.SolveAffine(IdentitySamples())
            .TransformWithRotation(new Coordinate2D(1, 0), 1));
    }

    /// <summary>验证已取消请求不进入求解。</summary>
    [TestMethod]
    public void Cancellation_IsObservedBeforeSolving()
    {
        Assert.ThrowsExactly<OperationCanceledException>(() => CalibrationSolver.SolveAffine(
            IdentitySamples(), token: new CancellationToken(true)));
    }

    private static CalibrationSample[] IdentitySamples() => new[]
    {
        new CalibrationSample(new Coordinate2D(0, 0), new Coordinate2D(0, 0)),
        new CalibrationSample(new Coordinate2D(1, 0), new Coordinate2D(1, 0)),
        new CalibrationSample(new Coordinate2D(0, 1), new Coordinate2D(0, 1))
    };
}
