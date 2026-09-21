using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// 回调边界与停止语义的回归。
/// <para>
/// 这些用例用可控假相机逐帧推动回调（不使用计时器），因此可以在没有相机、甚至没有 pylon 运行时的
/// 机器上确定性地验证三条流式纪律：所有权、回调线程安全、停止等待。
/// 真实相机的长连接与断线行为只能在现场验收中签署，不能用这里的结果替代。
/// </para>
/// </summary>
[TestClass]
public sealed class BaslerStreamSessionTests
{
    /// <summary>布防按顺序打开设备、写入布防参数、开始持续取流。</summary>
    [TestMethod]
    public void Arm_OpensCameraAppliesParametersAndStartsGrab()
    {
        var camera = new FakeStreamCamera();
        var session = new BaslerStreamSession(camera, new RecordingStreamSink());

        session.Arm(EVisionTriggerMode.External, 5000, 3.5);

        Assert.AreEqual(1, camera.OpenCount);
        Assert.AreEqual(EVisionTriggerMode.External, camera.AppliedTriggerMode);
        Assert.AreEqual(5000d, camera.AppliedExposure!.Value);
        Assert.AreEqual(3.5d, camera.AppliedGain!.Value);
        Assert.AreEqual(1, camera.StartGrabCount);
        Assert.IsFalse(session.Stopped);
        CollectionAssert.AreEqual(
            new[] { "open", "apply-parameters", "start-grab" },
            camera.Events.ToArray(),
            "布防的设备侧动作顺序是契约的一部分。");
    }

    /// <summary>回调帧被复制为中立图像，并保留设备序号与观测时刻。</summary>
    [TestMethod]
    public void Frame_IsDeliveredAsNeutralImageWithDeviceSequence()
    {
        var camera = new FakeStreamCamera();
        var sink = new RecordingStreamSink();
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var capturedAt = new DateTimeOffset(2026, 9, 21, 5, 0, 0, TimeSpan.Zero);
        var grab = new FakeGrabFrame("Mono8", 4, 2, pixels, imageNumber: 7, capturedAtUtc: capturedAt);

        Assert.IsTrue(camera.Emit(grab));

        Assert.AreEqual(1, sink.Frames.Count);
        var frame = sink.Frames[0];
        Assert.AreEqual(7L, frame.DeviceSequence, "设备序号必须原样进入元数据，不做重编号。");
        Assert.AreEqual(capturedAt, frame.CapturedAtUtc);
        Assert.AreEqual(EPixelLayout.Gray8, frame.Image.Info.Layout);
        Assert.AreEqual(4, frame.Image.Info.Width);
        Assert.AreEqual(2, frame.Image.Info.Height);

        var copy = new byte[8];
        frame.Image.CopyTo(0, copy, 0, copy.Length);
        CollectionAssert.AreEqual(pixels, copy, "中立图像必须是设备像素的独立副本。");
        Assert.AreEqual(1, grab.DisposeCount, "设备帧必须在回调返回前恰好释放一次。");
        Assert.AreEqual(1L, session.DeliveredCount);
        sink.DisposeFrames();
    }

    /// <summary>宽位深单色落到 Gray16 布局，目标格式由映射表显式决定。</summary>
    [TestMethod]
    public void Frame_WideMonoLandsOnGray16Layout()
    {
        var camera = new FakeStreamCamera();
        var sink = new RecordingStreamSink();
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        var grab = new FakeGrabFrame("Mono12", 2, 2, new byte[8], imageNumber: 1);
        Assert.IsTrue(camera.Emit(grab));

        Assert.AreEqual(EPixelLayout.Gray16, sink.Frames[0].Image.Info.Layout);
        Assert.AreEqual("Mono16", grab.TargetPixelFormat);
        sink.DisposeFrames();
    }

    /// <summary>像素格式不受支持时结束接收并释放帧，而不是静默丢帧。</summary>
    [TestMethod]
    public void UnsupportedPixelFormat_CompletesStreamAndReleasesFrame()
    {
        var camera = new FakeStreamCamera();
        var sink = new RecordingStreamSink();
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        var grab = new FakeGrabFrame("BayerRG12", 4, 2, new byte[8]);
        Assert.IsTrue(camera.Emit(grab));

        Assert.AreEqual(0, sink.Frames.Count);
        Assert.AreEqual(1, sink.Completions.Count, "接收结束只能通知一次。");
        StringAssert.Contains(sink.Completions[0], "像素格式", "结束原因必须指向真正的病灶。");
        Assert.AreEqual(1L, session.ConversionFaultCount);
        Assert.AreEqual(1, grab.DisposeCount, "从未离开会话的帧必须由会话释放。");
        Assert.IsTrue(session.Stopped);

        // 结束之后到达的帧不再交付，且仍被释放。
        var later = new FakeGrabFrame("Mono8", 4, 2, new byte[8]);
        camera.Emit(later);
        Assert.AreEqual(0, sink.Frames.Count);
        Assert.AreEqual(1, later.DisposeCount);
    }

    /// <summary>转换结果尺寸与中立布局不一致时拒绝发布该帧。</summary>
    [TestMethod]
    public void ConversionSizeMismatch_IsRejectedInsteadOfPublished()
    {
        var camera = new FakeStreamCamera();
        var sink = new RecordingStreamSink();
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        var grab = new FakeGrabFrame("Mono8", 4, 2, new byte[8], conversionSizeOverride: 9);
        Assert.IsTrue(camera.Emit(grab));

        Assert.AreEqual(0, sink.Frames.Count, "尺寸对不上说明映射表有误，不能把错步长的像素放出去。");
        StringAssert.Contains(sink.Completions[0], "尺寸与中立布局不一致");
        Assert.AreEqual(1, grab.DisposeCount);
    }

    /// <summary>接收方在回调内抛异常时不得逃逸到 SDK 回调线程，也不结束接收。</summary>
    [TestMethod]
    public void SinkThrow_DoesNotEscapeCallbackAndDoesNotEndStream()
    {
        var camera = new FakeStreamCamera();
        var sink = new RecordingStreamSink { PublishFailure = new InvalidOperationException("接收方拒绝") };
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        var rejected = new FakeGrabFrame("Mono8", 4, 2, new byte[8]);
        Assert.IsTrue(camera.Emit(rejected), "回调内异常不得抛出：厂商回调线程上抛异常会让事件通知直接停止。");

        Assert.AreEqual(1L, session.SinkFaultCount);
        Assert.AreEqual(1, rejected.DisposeCount, "设备帧必须由会话释放，不能随拒绝一起漏掉。");
        Assert.IsFalse(session.Stopped, "一次接收方违约不等于取流结束。");

        // 接收方恢复后仍能继续交付，证明会话没有被这次违约带偏。
        sink.PublishFailure = null;
        Assert.IsTrue(camera.Emit(new FakeGrabFrame("Mono8", 4, 2, new byte[8])));
        Assert.AreEqual(1, sink.Frames.Count);
        sink.DisposeFrames();
    }

    /// <summary>设备报告取流失败时结束接收并保留原因，重复上报只结束一次。</summary>
    [TestMethod]
    public void StreamFailure_EndsStreamOnceWithReason()
    {
        var camera = new FakeStreamCamera();
        var sink = new RecordingStreamSink();
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        Assert.IsTrue(camera.ReportFailure(new InvalidOperationException("相机断线")));
        Assert.IsTrue(camera.ReportFailure(new InvalidOperationException("又一次上报")));

        Assert.AreEqual(1, sink.Completions.Count, "结束通知必须只出现一次。");
        StringAssert.Contains(sink.Completions[0], "相机断线");
        Assert.AreEqual("相机断线", session.Failure);
        Assert.IsTrue(session.Stopped);
    }

    /// <summary>释放先停流，之后不再交付帧。</summary>
    [TestMethod]
    public async Task Dispose_StopsGrabAndRejectsLaterFrames()
    {
        var camera = new FakeStreamCamera();
        var sink = new RecordingStreamSink();
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        await session.DisposeAsync();

        Assert.AreEqual(1, camera.StopGrabCount);
        Assert.IsFalse(camera.Grabbing);
        Assert.IsTrue(session.Stopped);

        var late = new FakeGrabFrame("Mono8", 4, 2, new byte[8]);
        Assert.IsFalse(camera.Emit(late), "停流后钩子已摘除，回调不该再被调用。");
        Assert.AreEqual(0, sink.Frames.Count);
        Assert.AreEqual(1, late.DisposeCount);
    }

    /// <summary>设备违约、停流后仍交付一个在途帧时，会话自己挡住它。</summary>
    [TestMethod]
    public async Task Dispose_LateInFlightFrame_IsRejectedNotForwarded()
    {
        var camera = new FakeStreamCamera { DeliverAfterStop = true };
        var sink = new RecordingStreamSink();
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        await session.DisposeAsync();

        var late = new FakeGrabFrame("Mono8", 4, 2, new byte[8]);
        Assert.IsTrue(camera.Emit(late), "本用例模拟设备违约，仍会调用回调。");
        Assert.AreEqual(0, sink.Frames.Count, "停止之后不得再交付任何帧。");
        Assert.AreEqual(1L, session.RejectedAfterStopCount);
        Assert.AreEqual(1, late.DisposeCount);
    }

    /// <summary>释放必须等待已经进入的回调退出，而不是并发释放设备。</summary>
    [TestMethod]
    public async Task Dispose_WaitsForInFlightCallback()
    {
        var camera = new FakeStreamCamera { DeliverAfterStop = true };
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var sink = new RecordingStreamSink { Entered = entered, Release = release };
        var session = new BaslerStreamSession(camera, sink);
        session.Arm(EVisionTriggerMode.External, null, null);

        var grab = new FakeGrabFrame("Mono8", 4, 2, new byte[8]);
        var emit = Task.Run(() => camera.Emit(grab));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "回调必须已经进入接收方。");

        var dispose = session.DisposeAsync().AsTask();
        Assert.IsFalse(dispose.IsCompleted, "回调仍在途时释放不得完成。");

        release.Set();
        await emit;
        await dispose;

        Assert.AreEqual(1, sink.Frames.Count, "已经在途的帧应当完成交付。");
        Assert.AreEqual(1, grab.DisposeCount);
        sink.DisposeFrames();
    }

    /// <summary>重复释放是空操作，只停流一次。</summary>
    [TestMethod]
    public async Task Dispose_IsIdempotent()
    {
        var camera = new FakeStreamCamera();
        var session = new BaslerStreamSession(camera, new RecordingStreamSink());
        session.Arm(EVisionTriggerMode.External, null, null);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.AreEqual(1, camera.StopGrabCount);
    }
}
