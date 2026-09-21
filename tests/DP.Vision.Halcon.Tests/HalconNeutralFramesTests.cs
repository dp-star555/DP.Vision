using System;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// 中立像素落地的回归：主动单次采集与外部回调长连接共用这一份实现，
/// 因此布局判断、预算校验与取消都必须在这里被咬住，而不是只在 SDK 分支里。
/// </summary>
[TestClass]
public sealed class HalconNeutralFramesTests
{
    /// <summary>单通道 byte 落到 Gray8，像素原样搬运。</summary>
    [TestMethod]
    public void SingleChannelByte_LandsOnGray8()
    {
        var frame = new FakeHalconGrabFrame(3, 2, "byte", new byte[] { 1, 2, 3, 4, 5, 6 });

        using var source = HalconNeutralFrames.Copy(frame);

        Assert.AreEqual(EPixelLayout.Gray8, source.Info.Layout);
        Assert.AreEqual(3, source.Info.Width);
        Assert.AreEqual(2, source.Info.Height);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, Read(source, 6));
        CollectionAssert.AreEqual(new[] { 6 }, frame.CopyLengths.ToArray());
    }

    /// <summary>uint2 不做隐式降位深；字节序原样保留。</summary>
    [TestMethod]
    public void SingleChannelUint2_LandsOnGray16()
    {
        var frame = new FakeHalconGrabFrame(
            2, 2, "uint2", new byte[] { 0, 0, 0, 1, 255, 255, 232, 3 });

        using var source = HalconNeutralFrames.Copy(frame);

        Assert.AreEqual(EPixelLayout.Gray16, source.Info.Layout);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 255, 255, 232, 3 }, Read(source, 8));
        CollectionAssert.AreEqual(new[] { 8 }, frame.CopyLengths.ToArray());
    }

    /// <summary>三通道 byte 必须交错成 BGR；HALCON 的通道顺序是 0 红、1 绿、2 蓝。</summary>
    [TestMethod]
    public void ThreeChannelByte_IsInterleavedAsBgr()
    {
        var frame = new FakeHalconGrabFrame(
            2, 1, "byte",
            new byte[] { 200, 201 },
            new byte[] { 100, 101 },
            new byte[] { 50, 51 });

        using var source = HalconNeutralFrames.Copy(frame);

        Assert.AreEqual(EPixelLayout.Bgr24, source.Info.Layout);
        CollectionAssert.AreEqual(new byte[] { 50, 100, 200, 51, 101, 201 }, Read(source, 6));
        CollectionAssert.AreEqual(new[] { 2, 2, 2 }, frame.CopyLengths.ToArray());
    }

    /// <summary>不支持的像素类型明确拒绝，而不是按默认布局猜。</summary>
    /// <param name="channelCount">通道数。</param>
    /// <param name="pixelTypeName">像素类型名。</param>
    [TestMethod]
    [DataRow(1, "real")]
    [DataRow(1, "int4")]
    [DataRow(2, "byte")]
    [DataRow(4, "byte")]
    [DataRow(3, "uint2")]
    public void UnsupportedLayout_IsRejected(int channelCount, string pixelTypeName)
    {
        var channels = Enumerable.Range(0, channelCount)
            .Select(_ => new byte[channelCount == 3 ? 2 : 4])
            .ToArray();
        var frame = new FakeHalconGrabFrame(2, 2, pixelTypeName, channels);

        Assert.ThrowsExactly<NotSupportedException>(() => HalconNeutralFrames.Copy(frame));
        Assert.AreEqual(0, frame.DisposeCount, "中立层不拥有设备帧，不得释放它。");
    }

    /// <summary>超出像素复制预算时明确失败，而不是分配一个巨大的缓冲。</summary>
    [TestMethod]
    public void BudgetExceeded_IsRejected()
    {
        var frame = new FakeHalconGrabFrame(2, 2, "uint2", new byte[8]);

        Assert.ThrowsExactly<InvalidOperationException>(() => HalconNeutralFrames.Copy(frame, maximumBytes: 7));
        Assert.AreEqual(0, frame.DisposeCount);
    }

    /// <summary>已取消的令牌在任何复制动作之前就失败。</summary>
    [TestMethod]
    public void CancelledToken_FailsBeforeCopying()
    {
        var frame = new FakeHalconGrabFrame(2, 2, "byte", new byte[4]);

        Assert.ThrowsExactly<OperationCanceledException>(
            () => HalconNeutralFrames.Copy(frame, new CancellationToken(true)));
        Assert.AreEqual(0, frame.CopyLengths.Count);
    }

    /// <summary>参数校验必须发生在任何 SDK/设备动作之前。</summary>
    [TestMethod]
    public void NullFrame_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => HalconNeutralFrames.Copy(null!));
    }

    /// <summary>复制完成后中立图像必须独立于设备帧：设备帧释放后仍可读。</summary>
    [TestMethod]
    public void CopiedImage_OutlivesTheDeviceFrame()
    {
        var frame = new FakeHalconGrabFrame(2, 1, "byte", new byte[] { 7, 9 });

        var source = HalconNeutralFrames.Copy(frame);
        frame.Dispose();

        using (source)
        {
            CollectionAssert.AreEqual(new byte[] { 7, 9 }, Read(source, 2));
        }
    }

    private static byte[] Read(IImageSource source, int count)
    {
        var buffer = new byte[count];
        source.CopyTo(0, buffer, 0, count);
        return buffer;
    }
}
