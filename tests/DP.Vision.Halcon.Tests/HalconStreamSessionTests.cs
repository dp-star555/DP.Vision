using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// HALCON 采集会话的三条流式纪律回归。
/// <para>
/// 与 Basler 侧的差异是这一组用例存在的理由：pylon 由 SDK 在回调线程上推帧，
/// HALCON 的采集循环与线程都由会话自建，因此"线程所有权、停止等待、停流后不得再交付"
/// 必须由本层的用例咬住，不能指望 SDK。
/// </para>
/// </summary>
[TestClass]
public sealed class HalconStreamSessionTests
{
    /// <summary>布防必须打开设备、写入布防参数并真正开始拉取。</summary>
    [TestMethod]
    public async Task Arm_OpensConfiguresAndStartsGrabbing()
    {
        await using var harness = new Harness();

        Assert.AreEqual(1, harness.Camera.OpenCount);
        Assert.AreEqual(EVisionTriggerMode.External, harness.Camera.AppliedTriggerMode);
        Assert.AreEqual("Line1", harness.Camera.AppliedTriggerSource);
        Assert.AreEqual(1500, harness.Camera.AppliedGrabTimeout);
        Assert.IsTrue(harness.Camera.WaitForGrabEntered(), "布防后采集循环必须真的开始拉取。");
        CollectionAssert.AreEqual(new[] { "Open", "Apply" }, harness.Camera.Events.Take(2).ToArray());
    }

    /// <summary>一帧设备数据落到中立图像并原样交付；HALCON 没有设备帧序号，只能上报空值。</summary>
    [TestMethod]
    public async Task Frame_IsDeliveredAsNeutralImageWithNullDeviceSequence()
    {
        var harness = new Harness();
        try
        {
            Assert.IsTrue(harness.Camera.WaitForGrabEntered());

            var frame = new FakeHalconGrabFrame(2, 1, "byte", new byte[] { 4, 5 });
            harness.Camera.Feed(frame);

            Assert.IsTrue(harness.Sink.WaitForFrames(1));
            var delivered = harness.Sink.Frames.Single();
            Assert.IsNull(delivered.DeviceSequence, "HALCON 通用采集层不提供设备帧序号，不得伪造。");
            Assert.AreEqual(1, harness.Session.DeliveredCount);

            var pixels = new byte[2];
            delivered.Image.CopyTo(0, pixels, 0, 2);
            CollectionAssert.AreEqual(new byte[] { 4, 5 }, pixels);

            // 交付成功不等于设备帧已释放（释放在交付路径的 finally 里）；等采集线程退出后再断言。
            await harness.Session.DisposeAsync();
            Assert.AreEqual(1, frame.DisposeCount, "设备帧必须在交付路径结束时释放。");
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    /// <summary>抓取超时是"这一轮没有等到帧"，只计诊断，绝不结束接收。</summary>
    [TestMethod]
    public async Task GrabTimeout_IsCountedAndDoesNotEndStream()
    {
        await using var harness = new Harness();
        Assert.IsTrue(harness.Camera.WaitForGrabEntered());

        harness.Camera.FeedTimeout();
        harness.Camera.Feed(new FakeHalconGrabFrame(1, 1, "byte", new byte[] { 9 }));

        Assert.IsTrue(harness.Sink.WaitForFrames(1));
        Assert.AreEqual(1, harness.Session.GrabTimeoutCount);
        Assert.AreEqual(0, harness.Sink.Completions.Count);
        Assert.IsFalse(harness.Session.Stopped);
    }

    /// <summary>设备故障结束接收且只结束一次，并把原因带给等待中的领取。</summary>
    [TestMethod]
    public async Task DeviceFault_EndsStreamOnceWithReason()
    {
        await using var harness = new Harness();
        Assert.IsTrue(harness.Camera.WaitForGrabEntered());

        var fault = new VisionDeviceOfflineException("相机断线");
        harness.Camera.FeedFault(fault);

        Assert.IsTrue(harness.Sink.WaitForCompletions(1));
        Assert.AreEqual(fault.Message, harness.Sink.Completions.Single()!.Message);
        Assert.IsTrue(harness.Session.Stopped);
        Assert.AreEqual(fault.Message, harness.Session.Failure);

        // 终态：此后不会再有任何结束通知，即使设备继续报错。
        harness.Camera.FeedFault(new VisionDataException("不会再被取走"));
        Assert.IsFalse(harness.Sink.WaitForCompletions(2, 300));
    }

    /// <summary>许可证故障必须以 Provider 不可用的形态上报，不能和设备离线混在一起。</summary>
    [TestMethod]
    public async Task LicenseFault_IsReportedAsProviderUnavailable()
    {
        await using var harness = new Harness();
        Assert.IsTrue(harness.Camera.WaitForGrabEntered());

        harness.Camera.FeedFault(
            new VisionProviderUnavailableException(HalconAcquisitionProvider.ProviderIdentity, "许可证不可用"));

        Assert.IsTrue(harness.Sink.WaitForCompletions(1));
        Assert.IsInstanceOfType<VisionProviderUnavailableException>(harness.Sink.Completions.Single());
        Assert.IsFalse(harness.Sink.Completions.Single() is VisionDeviceOfflineException);
    }

    /// <summary>像素落地失败不能静默丢帧：结束接收并释放设备帧。</summary>
    [TestMethod]
    public async Task ConversionFailure_EndsStreamAndReleasesFrame()
    {
        var harness = new Harness();
        try
        {
            Assert.IsTrue(harness.Camera.WaitForGrabEntered());

            var frame = new FakeHalconGrabFrame(2, 2, "real", new byte[4]);
            harness.Camera.Feed(frame);

            Assert.IsTrue(harness.Sink.WaitForCompletions(1));
            Assert.IsInstanceOfType<NotSupportedException>(harness.Sink.Completions.Single());
            Assert.AreEqual(1, harness.Session.ConversionFaultCount);
            Assert.AreEqual(0, harness.Sink.Frames.Count);

            // 结束通知是在交付路径内部发出的，设备帧的释放在其后；因此要等采集线程真正退出后再断言，
            // 这也正是公共契约给出的保证：DisposeAsync 返回时一切都已释放。
            await harness.Session.DisposeAsync();
            Assert.AreEqual(1, frame.DisposeCount, "像素落地失败时设备帧从未离开会话，必须由会话释放。");
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    /// <summary>接收方违约不得逃逸到采集线程，也不得把接收打死。</summary>
    [TestMethod]
    public async Task SinkThrow_DoesNotEscapeAndDoesNotEndStream()
    {
        await using var harness = new Harness();
        Assert.IsTrue(harness.Camera.WaitForGrabEntered());

        harness.Sink.PublishFailure = new InvalidOperationException("接收方违约");
        var rejected = new FakeHalconGrabFrame(1, 1, "byte", new byte[] { 1 });
        harness.Camera.Feed(rejected);

        // 循环又回到拉取，说明异常没有把采集线程打死。
        Assert.IsTrue(harness.Camera.WaitForGrabEntered(), "接收方抛异常后采集循环必须继续。");
        Assert.AreEqual(1, harness.Session.SinkFaultCount);
        Assert.AreEqual(1, rejected.DisposeCount);
        Assert.AreEqual(0, harness.Sink.Completions.Count);

        harness.Sink.PublishFailure = null;
        harness.Camera.Feed(new FakeHalconGrabFrame(1, 1, "byte", new byte[] { 2 }));
        Assert.IsTrue(harness.Sink.WaitForFrames(1));
    }

    /// <summary>
    /// 停止后到达的帧必须被拒绝并释放，既不交付也不泄漏；
    /// 而且这条保证不得依赖 do_abort_grab 成功——接口不支持它时同样成立。
    /// </summary>
    [TestMethod]
    public async Task Dispose_RejectsFrameArrivingAfterStopAndReleasesIt()
    {
        await using var harness = new Harness();
        Assert.IsTrue(harness.Camera.WaitForGrabEntered());

        // 模拟"该采集接口不支持 do_abort_grab"：中止无效，只能等这一轮抓取自己返回。
        harness.Camera.AbortUnblocksGrab = false;

        var disposal = harness.Session.DisposeAsync();

        var late = new FakeHalconGrabFrame(2, 1, "byte", new byte[] { 8, 9 });
        harness.Camera.Feed(late);

        await disposal;
        Assert.AreEqual(1, harness.Session.RejectedAfterStopCount);
        Assert.AreEqual(1, late.DisposeCount, "被拒绝的帧仍必须由会话释放。");
        Assert.AreEqual(0, harness.Sink.Frames.Count);
        Assert.AreEqual(0, harness.Session.DeliveredCount);
    }

    /// <summary>停止必须等待已经进入交付的那一帧退出，之后才返回。</summary>
    [TestMethod]
    public async Task Dispose_WaitsForInFlightDelivery()
    {
        await using var harness = new Harness();
        Assert.IsTrue(harness.Camera.WaitForGrabEntered());

        harness.Sink.Entered = new System.Threading.ManualResetEventSlim(false);
        harness.Sink.Release = new System.Threading.ManualResetEventSlim(false);

        var frame = new FakeHalconGrabFrame(1, 1, "byte", new byte[] { 3 });
        harness.Camera.Feed(frame);
        Assert.IsTrue(harness.Sink.Entered.Wait(5000), "交付必须已经进入。");

        var disposal = harness.Session.DisposeAsync();
        Assert.IsFalse(disposal.IsCompleted, "交付仍在进行时 Dispose 不得提前完成。");

        harness.Sink.Release.Set();
        await disposal;

        Assert.AreEqual(1, harness.Session.DeliveredCount);
        Assert.AreEqual(1, frame.DisposeCount);
    }

    /// <summary>重复停止是空操作，且中止抓取只发生一次。</summary>
    [TestMethod]
    public async Task Dispose_IsIdempotent()
    {
        var harness = new Harness();
        try
        {
            await harness.Session.DisposeAsync();
            await harness.Session.DisposeAsync();

            Assert.AreEqual(1, harness.Camera.AbortCount);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    /// <summary>从未布防就停止不得挂起：没有采集线程要等。</summary>
    [TestMethod]
    public async Task Dispose_WithoutArming_CompletesImmediately()
    {
        var camera = new FakeHalconStreamCamera();
        var session = new HalconStreamSession(camera, new RecordingStreamSink());

        await session.DisposeAsync();

        Assert.IsTrue(session.Stopped);
    }

    /// <summary>布防参数写不进设备时设备保持打开以便重试，且停止不得挂起。</summary>
    [TestMethod]
    public async Task ArmApplyFailure_KeepsCameraOpenForRetry()
    {
        var camera = new FakeHalconStreamCamera
        {
            ApplyFailure = new VisionSourceConfigurationException("触发源写不进设备")
        };
        var session = new HalconStreamSession(camera, new RecordingStreamSink());

        Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => session.Arm(EVisionTriggerMode.External, "Line1", 1000));

        Assert.IsTrue(session.Stopped);
        Assert.AreEqual(1, camera.OpenCount);
        Assert.AreEqual(0, camera.CloseCount, "布防失败不得关闭设备：设备要在两次布防之间复用。");

        await session.DisposeAsync();
    }

    /// <summary>打开阶段失败时同样不得让停止挂起（没有采集线程被启动）。</summary>
    [TestMethod]
    public async Task ArmOpenFailure_LeavesSessionStoppedAndDisposable()
    {
        var camera = new FakeHalconStreamCamera { OpenFailure = new VisionDeviceOfflineException("设备离线") };
        var session = new HalconStreamSession(camera, new RecordingStreamSink());

        Assert.ThrowsExactly<VisionDeviceOfflineException>(
            () => session.Arm(EVisionTriggerMode.KeepCurrent, null, 1000));

        Assert.IsTrue(session.Stopped);
        Assert.AreEqual(0, camera.OpenCount);
        Assert.IsFalse(camera.WaitForGrabEntered(200), "打开失败后不得启动采集循环。");

        await session.DisposeAsync();
    }

    /// <summary>会话本身不拥有设备：停止只停流，不关闭也不释放相机。</summary>
    [TestMethod]
    public async Task Dispose_StopsStreamWithoutClosingCamera()
    {
        var harness = new Harness();
        try
        {
            await harness.Session.DisposeAsync();

            Assert.AreEqual(1, harness.Camera.AbortCount);
            Assert.AreEqual(0, harness.Camera.CloseCount, "关闭设备是设备适配器的职责。");
            Assert.AreEqual(0, harness.Camera.DisposeCount);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    /// <summary>把"布防 + 采集线程 + 收尾"打包，避免每个用例都手写资源释放。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            EVisionTriggerMode triggerMode = EVisionTriggerMode.External,
            string? triggerSource = "Line1",
            int grabTimeoutMilliseconds = 1500)
        {
            Camera = new FakeHalconStreamCamera();
            Sink = new RecordingStreamSink();
            Session = new HalconStreamSession(Camera, Sink);
            Session.Arm(triggerMode, triggerSource, grabTimeoutMilliseconds);
        }

        public FakeHalconStreamCamera Camera { get; }

        public RecordingStreamSink Sink { get; }

        public HalconStreamSession Session { get; }

        public async ValueTask DisposeAsync()
        {
            Camera.AbortUnblocksGrab = true;
            await Session.DisposeAsync();
            Sink.DisposeFrames();
        }
    }
}
