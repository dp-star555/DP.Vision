using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 持续接收的交付与停止语义回归（ACQUISITION_RUNTIME_V1 §V1-A）。
/// <para>
/// 这些用例锁定的是<see cref="IVisionStreamingAcquisitionDevice"/>与<see cref="IVisionProviderFrameSink"/>的契约；
/// 假设备只是载体，契约本身适用于后续真实厂商Adapter。
/// </para>
/// </summary>
[TestClass]
public sealed class StreamingFrameSinkContractTests
{
    /// <summary>帧按到达顺序交付，设备序号原样透传，且每帧的真实图像资源都被释放。</summary>
    [TestMethod]
    public async Task Stream_DeliversFramesInArrivalOrder()
    {
        var counter = new DisposalCounter();
        var device = new FakeStreamingVisionDevice(Identity(), seed => new TrackingImageSource(counter));
        var sink = new RecordingFrameSink();
        var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        Assert.IsTrue(device.Emit(101));
        Assert.IsTrue(device.Emit(102));
        Assert.IsTrue(device.Emit(103));

        CollectionAssert.AreEqual(new long[] { 101, 102, 103 }, sink.ReceivedSequences.ToArray());
        Assert.AreEqual(3, sink.PublishCount);
        // 观察真实资源：接收方自己维护的计数器即使不释放也会自增，用它断言等于自证。
        Assert.AreEqual(3, counter.Disposes, "每一帧的中立图像都必须被释放，不能因为接收而泄漏。");
        await stream.DisposeAsync();
    }

    /// <summary>一台设备同时只允许一条接收流；重复布防明确拒绝而不是静默替换接收方。</summary>
    [TestMethod]
    public async Task Stream_SecondArmingIsRejected()
    {
        var device = new FakeStreamingVisionDevice(Identity());
        var first = new RecordingFrameSink();
        var second = new RecordingFrameSink();
        var stream = await device.StartStreamAsync(first, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await device.StartStreamAsync(second, CancellationToken.None));

        Assert.AreEqual(1, device.StreamStartCount);
        Assert.IsTrue(device.Emit(1));
        Assert.AreEqual(1, first.PublishCount);
        Assert.AreEqual(0, second.PublishCount, "被拒绝的接收方不得收到任何帧。");
        await stream.DisposeAsync();
    }

    /// <summary>设备声明接收结束后不得再交付帧。</summary>
    [TestMethod]
    public async Task Stream_NoDeliveryAfterComplete()
    {
        var device = new FakeStreamingVisionDevice(Identity());
        var sink = new RecordingFrameSink();
        var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        Assert.IsTrue(device.Emit(1));
        Assert.IsTrue(device.CompleteStream());
        Assert.IsFalse(device.Emit(2), "Complete 之后不得再交付帧。");

        Assert.AreEqual(1, sink.PublishCount);
        Assert.AreEqual(1, sink.CompletedCount);
        Assert.AreEqual(0, sink.Violations.Count, string.Join("；", sink.Violations));
        await stream.DisposeAsync();
    }

    /// <summary>释放接收流之后不得再交付帧。</summary>
    [TestMethod]
    public async Task Stream_NoDeliveryAfterStreamDisposed()
    {
        var device = new FakeStreamingVisionDevice(Identity());
        var sink = new RecordingFrameSink();
        var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        Assert.IsTrue(device.Emit(1));
        await stream.DisposeAsync();

        Assert.IsFalse(device.IsStreaming);
        Assert.IsFalse(device.Emit(2), "接收流释放后不得再交付帧。");
        Assert.AreEqual(1, sink.PublishCount);
        Assert.AreEqual(1, device.StreamDisposeCount);
    }

    /// <summary>重复释放接收流是幂等的，不会重复计数或再次触碰接收方。</summary>
    [TestMethod]
    public async Task Stream_DisposeIsIdempotent()
    {
        var device = new FakeStreamingVisionDevice(Identity());
        var sink = new RecordingFrameSink();
        var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        Assert.AreEqual(1, device.StreamDisposeCount);
    }

    /// <summary>接收方拒绝时帧仍然被释放，且异常不得抛回SDK回调线程。</summary>
    [TestMethod]
    public async Task Stream_RejectedFrameIsDisposedAndFailureDoesNotEscape()
    {
        var counter = new DisposalCounter();
        var device = new FakeStreamingVisionDevice(Identity(), seed => new TrackingImageSource(counter));
        var sink = new RecordingFrameSink { RejectFrames = true };
        var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        // 不抛异常：模拟厂商回调线程上"异常逃逸"通常直接崩进程。
        Assert.IsTrue(device.Emit(1));

        Assert.AreEqual(1, sink.PublishCount);
        Assert.AreEqual(1, counter.Disposes, "接收方拒绝后仍必须释放帧，否则每帧都会泄漏。");
        Assert.AreEqual(1, device.SinkFailures.Count);
        StringAssert.Contains(device.SinkFailures[0], "InvalidOperationException");
        await stream.DisposeAsync();
    }

    /// <summary>
    /// 释放接收流必须等待已经进入的回调退出：回调进行中时设备仍处于布防状态。
    /// <para>
    /// 观察点放在交付过程内部（<see cref="RecordingFrameSink.AfterDelivery"/>）：
    /// 若在 <c>Emit</c> 返回后再读，锁已释放，会把竞态误判成实现缺陷。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Stream_DisposeWaitsForInFlightCallback()
    {
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var device = new FakeStreamingVisionDevice(Identity());
        var streamingWhenCallbackReturned = false;
        var sink = new RecordingFrameSink(entered, release)
        {
            AfterDelivery = () => streamingWhenCallbackReturned = device.IsStreaming
        };
        var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        var callback = Task.Run(() => device.Emit(1));

        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "回调未在预期时间内进入。");
        var dispose = Task.Run(async () => await stream.DisposeAsync());

        // 给释放动作一个启动窗口，使下面的断言真的在观察"是否被回调挡住"。
        var disposeCompletedEarly = SpinWait.SpinUntil(() => dispose.IsCompleted, TimeSpan.FromMilliseconds(200));
        Assert.IsFalse(disposeCompletedEarly, "释放接收流不得与进行中的回调并发完成。");

        release.Set();
        await callback;
        await dispose;

        Assert.IsTrue(streamingWhenCallbackReturned, "回调返回时设备必须仍处于布防状态。");
        Assert.IsFalse(device.IsStreaming);
        Assert.AreEqual(1, sink.DisposedCount);
    }

    /// <summary>释放设备会一并结束接收流，避免"设备已释放但回调仍在进入"。</summary>
    [TestMethod]
    public async Task Device_DisposeEndsStream()
    {
        var device = new FakeStreamingVisionDevice(Identity());
        var sink = new RecordingFrameSink();
        await device.StartStreamAsync(sink, CancellationToken.None);

        await device.DisposeAsync();

        Assert.IsFalse(device.IsStreaming);
        Assert.IsFalse(device.Emit(1));
        Assert.AreEqual(1, device.StreamDisposeCount);
        Assert.AreEqual(1, device.DisposeCount);
    }

    /// <summary>未布防时推进回调是无操作，不产生帧也不抛异常。</summary>
    [TestMethod]
    public void Stream_EmitWithoutArmingIsNoOp()
    {
        var device = new FakeStreamingVisionDevice(Identity());

        Assert.IsFalse(device.Emit(1));
        Assert.IsFalse(device.CompleteStream());
        Assert.AreEqual(0, device.StreamStartCount);
    }

    private static VisionDeviceIdentity Identity() =>
        new VisionDeviceIdentity("dp.fake", "top", "camera:serial:A");
}
