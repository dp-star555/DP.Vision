using System;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>搜索区间的跨界、整周及采样规模约束。</summary>
[TestClass]
public sealed class TemplateSearchRangeTests
{
    /// <summary>父坐标旋转后区间仍保留宽度，不将两个端点单独归一化。</summary>
    [TestMethod]
    public void WrappedAndFullCircleRangesKeepTheirExtent()
    {
        var wrapped = new TemplatePoseOptions(170 * Math.PI / 180, 190 * Math.PI / 180).AngleIntervals();
        Assert.AreEqual(2, wrapped.Count);
        Assert.AreEqual(170 * Math.PI / 180, wrapped[0].Minimum, 1e-10);
        Assert.AreEqual(Math.PI, wrapped[0].Maximum, 1e-10);
        Assert.AreEqual(-Math.PI, wrapped[1].Minimum, 1e-10);
        Assert.AreEqual(-170 * Math.PI / 180, wrapped[1].Maximum, 1e-10);
        var shifted = new TemplatePoseOptions(430 * Math.PI / 180, 450 * Math.PI / 180).AngleIntervals();
        Assert.AreEqual(1, shifted.Count);
        Assert.AreEqual(70 * Math.PI / 180, shifted[0].Minimum, 1e-10);
        Assert.AreEqual(Math.PI / 2, shifted[0].Maximum, 1e-10);
        var full = new TemplatePoseOptions(.8, .8 + 2 * Math.PI).AngleIntervals();
        Assert.AreEqual(1, full.Count);
        Assert.AreEqual(-Math.PI, full[0].Minimum);
        Assert.AreEqual(Math.PI, full[0].Maximum);
    }

    /// <summary>非法范围在调用引擎前失败。</summary>
    [TestMethod]
    [DataRow(1d, 0d, 1d, 1d, .1, .1)]
    [DataRow(0d, 7d, 1d, 1d, .1, .1)]
    [DataRow(double.NaN, 0d, 1d, 1d, .1, .1)]
    [DataRow(0d, double.PositiveInfinity, 1d, 1d, .1, .1)]
    [DataRow(0d, 0d, 2d, 1d, .1, .1)]
    [DataRow(0d, 0d, .09, 1d, .1, .1)]
    [DataRow(0d, 0d, 1d, 11d, .1, .1)]
    [DataRow(0d, 0d, 1d, 1d, 0d, .1)]
    [DataRow(0d, 0d, 1d, 1d, .1, 0d)]
    public void InvalidRangeIsRejected(double minAngle, double maxAngle, double minScale, double maxScale, double angleStep, double scaleStep)
        => Assert.ThrowsExactly<ArgumentException>(() => new TemplatePoseOptions(minAngle, maxAngle, minScale, maxScale,
            angleStepRadians: angleStep, scaleStep: scaleStep));

    /// <summary>采样规模在创建模板候选之前校验，避免小步长耗尽内存。</summary>
    [TestMethod]
    public void OpenCvRejectsExcessiveSamplingBeforeMatching()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(2, 2, EPixelLayout.Gray8), new byte[] { 0, 80, 170, 255 });
        using var frame = new ImageFrame("frame", image);
        var locator = new OpenCvTemplatePoseLocator();
        var axisError = Assert.ThrowsExactly<ArgumentException>(() => locator.Locate(frame, frame, new PixelBounds(0, 0, 2, 2),
            new TemplatePoseOptions(-Math.PI, Math.PI, angleStepRadians: .00001)));
        StringAssert.Contains(axisError.Message, "单轴采样");
        var combinationError = Assert.ThrowsExactly<ArgumentException>(() => locator.Locate(frame, frame, new PixelBounds(0, 0, 2, 2),
            new TemplatePoseOptions(-Math.PI, Math.PI, 1, 2, angleStepRadians: .05, scaleStep: .01)));
        StringAssert.Contains(combinationError.Message, "4096组");
    }
}
