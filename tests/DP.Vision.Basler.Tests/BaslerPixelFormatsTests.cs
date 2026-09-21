using System;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// 像素格式映射表的回归。该映射是纯逻辑，不依赖 pylon 运行时，也不需要相机，
/// 因此"格式怎么落地"这件事必须在这里被完整钉住，而不是靠现场试出来。
/// </summary>
[TestClass]
public sealed class BaslerPixelFormatsTests
{
    /// <summary>四种可直接复制的格式不做任何转换，字节语义与设备完全一致。</summary>
    /// <param name="source">设备报告的像素格式名。</param>
    /// <param name="layout">期望的中立布局。</param>
    [TestMethod]
    [DataRow("Mono8", EPixelLayout.Gray8)]
    [DataRow("Mono16", EPixelLayout.Gray16)]
    [DataRow("BGR8packed", EPixelLayout.Bgr24)]
    [DataRow("RGB8packed", EPixelLayout.Rgb24)]
    public void DirectFormats_CopyWithoutConversion(string source, EPixelLayout layout)
    {
        var conversion = BaslerPixelFormats.Resolve(source);

        Assert.AreEqual(layout, conversion.Layout);
        Assert.IsFalse(conversion.RequiresConversion, source + " 应直接复制。");
        Assert.AreEqual(source, conversion.TargetPixelFormat);
    }

    /// <summary>单色族一律落到单通道灰度，不把单色复制成三通道。</summary>
    /// <param name="source">设备报告的像素格式名。</param>
    /// <param name="layout">期望的中立布局。</param>
    [TestMethod]
    [DataRow("Mono10", EPixelLayout.Gray16)]
    [DataRow("Mono12", EPixelLayout.Gray16)]
    [DataRow("Mono12p", EPixelLayout.Gray16)]
    [DataRow("Mono1packed", EPixelLayout.Gray8)]
    [DataRow("Mono4packed", EPixelLayout.Gray8)]
    [DataRow("Mono8signed", EPixelLayout.Gray8)]
    public void MonoFamily_ConvertsToSingleChannel(string source, EPixelLayout layout)
    {
        var conversion = BaslerPixelFormats.Resolve(source);

        Assert.AreEqual(layout, conversion.Layout);
        Assert.IsTrue(conversion.RequiresConversion);
    }

    /// <summary>8位彩色族（含Bayer）转成BGR8packed；这是pylon自带的去马赛克路径。</summary>
    /// <param name="source">设备报告的像素格式名。</param>
    [TestMethod]
    [DataRow("BayerRG8")]
    [DataRow("BayerGB8")]
    [DataRow("BayerGR8")]
    [DataRow("BayerBG8")]
    [DataRow("RGB8planar")]
    [DataRow("BGRA8packed")]
    [DataRow("YCbCr422_8")]
    [DataRow("BiColorRGBG8")]
    public void EightBitColorFamily_ConvertsToBgr24(string source)
    {
        var conversion = BaslerPixelFormats.Resolve(source);

        Assert.AreEqual(EPixelLayout.Bgr24, conversion.Layout);
        Assert.AreEqual(BaslerPixelFormats.Bgr8Packed, conversion.TargetPixelFormat);
        Assert.IsTrue(conversion.RequiresConversion);
    }

    /// <summary>
    /// 高位深彩色与三维/置信度等非二维格式必须被拒绝：
    /// 降位到8位会丢动态范围，静默降位会让下游算法结果无法解释。
    /// </summary>
    /// <param name="source">设备报告的像素格式名。</param>
    [TestMethod]
    [DataRow("BayerRG12")]
    [DataRow("BayerBG16")]
    [DataRow("RGB16packed")]
    [DataRow("RGB12planar")]
    [DataRow("BGR10packed")]
    [DataRow("Coord3D_C16")]
    [DataRow("Confidence16")]
    [DataRow("Error8")]
    [DataRow("")]
    [DataRow("   ")]
    public void UnsupportedFormats_AreRejected(string source)
    {
        var failure = Assert.ThrowsExactly<VisionDataException>(() => BaslerPixelFormats.Resolve(source));

        StringAssert.Contains(failure.Message, "像素格式");
    }

    /// <summary>拒绝时必须点出是哪个格式，否则现场无法判断该改哪里。</summary>
    [TestMethod]
    public void Rejection_NamesTheOffendingFormat()
    {
        var failure = Assert.ThrowsExactly<VisionDataException>(() => BaslerPixelFormats.Resolve("BayerRG12"));

        StringAssert.Contains(failure.Message, "BayerRG12");
    }
}
