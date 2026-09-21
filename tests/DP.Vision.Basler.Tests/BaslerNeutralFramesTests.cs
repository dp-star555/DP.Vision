using System;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// 像素落地的回归。OnDemand 与 BufferedExternal 共用这一条路径，
/// 因此这里锁住的是"两条路径都不会各自漂移"这件事。
/// </summary>
[TestClass]
public sealed class BaslerNeutralFramesTests
{
    /// <summary>
    /// 即使格式不需要转换，也必须经过设备侧转换器：pylon 的行填充（PaddingX）只在转换器里被处理，
    /// 为了省一次拷贝改成直接复制会让带填充的帧逐行错位。
    /// </summary>
    [TestMethod]
    public void DirectFormat_StillGoesThroughDeviceConverter()
    {
        var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var grab = new FakeGrabFrame("Mono8", 4, 2, pixels);

        using var image = BaslerNeutralFrames.Copy(grab);

        Assert.IsTrue(grab.Converted, "直接可复制的格式也必须走设备侧转换器，否则行填充会被忽略。");
        Assert.AreEqual("Mono8", grab.TargetPixelFormat);
        Assert.AreEqual(EPixelLayout.Gray8, image.Info.Layout);
        Assert.AreEqual(8, image.Info.ByteLength);
    }

    /// <summary>转换结果尺寸与中立布局不一致时明确拒绝，而不是按错步长解释像素。</summary>
    [TestMethod]
    public void SizeMismatch_IsRejected()
    {
        var grab = new FakeGrabFrame("Mono8", 4, 2, new byte[8], conversionSizeOverride: 12);

        var failure = Assert.ThrowsExactly<VisionDataException>(() => BaslerNeutralFrames.Copy(grab));

        StringAssert.Contains(failure.Message, "尺寸与中立布局不一致");
    }

    /// <summary>映射表之外的格式一律拒绝，不做"猜一个最接近的格式"。</summary>
    [TestMethod]
    public void UnsupportedFormat_IsRejected()
    {
        var grab = new FakeGrabFrame("BayerRG12", 4, 2, new byte[8]);

        var failure = Assert.ThrowsExactly<VisionDataException>(() => BaslerNeutralFrames.Copy(grab));

        StringAssert.Contains(failure.Message, "不在本Provider的显式支持范围内");
    }
}
