using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// 设备适配器的生命周期回归：单一已连接相机的复用、两种采集模式的互斥、断线状态与释放顺序。
/// 这些结论都在设备契约这一层，不需要相机或 pylon 运行时。
/// </summary>
[TestClass]
public sealed class BaslerAcquisitionDeviceStreamTests
{
    /// <summary>布防触发模式由绑定是否声明触发源决定，不猜物理接线。</summary>
    /// <param name="triggerSource">绑定声明的触发源。</param>
    /// <param name="expected">期望的布防触发模式。</param>
    [TestMethod]
    [DataRow(null, EVisionTriggerMode.KeepCurrent)]
    [DataRow("Line1", EVisionTriggerMode.External)]
    public void ArmTriggerMode_FollowsBindingTriggerSource(string? triggerSource, EVisionTriggerMode expected)
    {
        Assert.AreEqual(expected, BaslerAcquisitionDevice.ResolveArmTriggerMode(Binding(triggerSource)));
    }

    /// <summary>布防打开相机、写入布防参数，并把回调帧交付给接收方。</summary>
    [TestMethod]
    public async Task StartStream_ArmsCameraAndDeliversFrames()
    {
        var camera = new FakeStreamCamera();
        await using var device = Device(camera, "Line1");
        var sink = new RecordingStreamSink();

        await using var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        Assert.AreEqual(1, camera.OpenCount);
        Assert.AreEqual(EVisionTriggerMode.External, camera.AppliedTriggerMode);
        Assert.AreEqual(1, camera.StartGrabCount);

        Assert.IsTrue(camera.Emit(new FakeGrabFrame("Mono8", 2, 1, new byte[] { 9, 8 })));
        Assert.AreEqual(1, sink.Frames.Count);
        sink.DisposeFrames();
    }

    /// <summary>第二根根运行复用已打开的相机，不重新打开设备。</summary>
    [TestMethod]
    public async Task SecondArm_ReusesOpenCameraInsteadOfOpeningAgain()
    {
        var camera = new FakeStreamCamera();
        await using var device = Device(camera);

        var first = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);
        await first.DisposeAsync();

        await using var second = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        Assert.AreEqual(1, camera.OpenCount, "退役只停流，相机在两次布防之间保持打开。");
        Assert.AreEqual(2, camera.StartGrabCount);
        Assert.IsTrue(camera.IsOpen);
    }

    /// <summary>已经在布防状态时重复布防明确拒绝，且不替换原接收方。</summary>
    [TestMethod]
    public async Task SecondArm_WhileArmed_IsRejectedWithoutReplacingSink()
    {
        var camera = new FakeStreamCamera();
        await using var device = Device(camera);
        var first = new RecordingStreamSink();

        await using var stream = await device.StartStreamAsync(first, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None));

        Assert.IsTrue(camera.Emit(new FakeGrabFrame("Mono8", 2, 1, new byte[] { 1, 2 })));
        Assert.AreEqual(1, first.Frames.Count, "原接收方必须仍然是唯一的接收方。");
        first.DisposeFrames();
    }

    /// <summary>取流没起来时相机保持打开，下一次布防可以直接重试。</summary>
    [TestMethod]
    public async Task ArmFailure_KeepsCameraOpenForRetry()
    {
        var camera = new FakeStreamCamera { StartGrabFailure = new InvalidOperationException("取流启动失败") };
        await using var device = Device(camera);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None));

        Assert.IsTrue(camera.IsOpen, "取流失败不应关闭相机：下一次布防要能复用同一个已打开的设备。");
        Assert.AreEqual(0, camera.CloseCount);

        camera.StartGrabFailure = null;
        await using var stream = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        Assert.AreEqual(1, camera.OpenCount, "重试不得重新打开相机。");
        Assert.AreEqual(1, camera.StartGrabCount);
    }

    /// <summary>缓冲源布防期间不允许同一设备按请求单次采集。</summary>
    [TestMethod]
    public async Task CaptureAsync_WhileArmed_IsRejected()
    {
        var camera = new FakeStreamCamera();
        await using var device = Device(camera);
        await using var stream = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<VisionSourceConfigurationException>(
            async () => await device.CaptureAsync(new VisionCaptureRequest(TimeSpan.FromSeconds(1)), CancellationToken.None));
    }

    /// <summary>释放顺序是契约的一部分：先停流，再关闭相机。</summary>
    [TestMethod]
    public async Task DeviceDispose_StopsStreamBeforeClosingCamera()
    {
        var camera = new FakeStreamCamera();
        var device = Device(camera);
        await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        await device.DisposeAsync();

        Assert.IsFalse(camera.IsOpen);
        Assert.AreEqual(1, camera.DisposeCount);
        CollectionAssert.AreEqual(
            new[] { "open", "apply-parameters", "start-grab", "stop-grab", "close" },
            camera.Events.ToArray(),
            "必须先停流（等待在途回调退出），再关闭设备。");
    }

    /// <summary>重复释放是空操作，设备只关闭一次。</summary>
    [TestMethod]
    public async Task DeviceDispose_IsIdempotent()
    {
        var camera = new FakeStreamCamera();
        var device = Device(camera);
        await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        await device.DisposeAsync();
        await device.DisposeAsync();

        Assert.AreEqual(1, camera.DisposeCount);
    }

    /// <summary>从未布防过的设备不触碰相机：长连接设备是惰性创建的。</summary>
    [TestMethod]
    public async Task DeviceDispose_WithoutArming_DoesNotTouchCamera()
    {
        var camera = new FakeStreamCamera();
        var device = Device(camera);

        await device.DisposeAsync();

        Assert.AreEqual(0, camera.OpenCount);
        Assert.AreEqual(0, camera.DisposeCount);
    }

    /// <summary>已释放的设备拒绝再次布防。</summary>
    [TestMethod]
    public async Task StartStream_AfterDeviceDisposed_Throws()
    {
        var camera = new FakeStreamCamera();
        var device = Device(camera);
        await device.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None));
    }

    /// <summary>断线只结束接收流，不关闭相机：设备本身仍然打开，便于排障后重连。</summary>
    [TestMethod]
    public async Task StreamFailure_EndsSessionWithoutClosingCamera()
    {
        var camera = new FakeStreamCamera();
        await using var device = Device(camera);
        var sink = new RecordingStreamSink();
        await using var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        Assert.IsTrue(camera.ReportFailure(new InvalidOperationException("相机断线")));

        Assert.IsTrue(stream is BaslerStreamSession session && session.Stopped, "断线必须结束本次接收。");
        StringAssert.Contains(sink.Completions[0], "相机断线");
        Assert.IsTrue(camera.IsOpen, "断线不等于关闭设备：相机由设备生命周期拥有，不在回调路径上关闭。");
        Assert.AreEqual(0, camera.CloseCount);
    }

    /// <summary>三根根运行共用同一个已连接相机与同一条接收流，退出后也只关闭一次。</summary>
    [TestMethod]
    public async Task ThreeRuns_ShareSingleConnectedCameraAndCloseOnce()
    {
        var camera = new FakeStreamCamera();
        var device = Device(camera);

        for (var run = 0; run < 3; run++)
        {
            var stream = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);
            await stream.DisposeAsync();
        }

        Assert.AreEqual(1, camera.OpenCount, "三根根运行只能打开相机一次。");
        Assert.AreEqual(3, camera.StartGrabCount);
        Assert.AreEqual(3, camera.StopGrabCount);
        Assert.AreEqual(0, camera.CloseCount, "退役不停流不关设备；只有释放设备时才关闭。");

        await device.DisposeAsync();

        Assert.AreEqual(1, camera.CloseCount);
        Assert.AreEqual(1, camera.DisposeCount);
    }

    /// <summary>单次采集之间复用同一个已连接相机：不重复打开，也不在每次采集后关闭。</summary>
    [TestMethod]
    public async Task CaptureAsync_ReusesSingleConnectedCameraAcrossRequests()
    {
        var camera = new FakeStreamCamera();
        await using var device = Device(camera);

        for (var index = 0; index < 3; index++)
        {
            var frame = await device.CaptureAsync(Request(1500, 2.5), CancellationToken.None);
            Assert.AreEqual(EPixelLayout.Gray8, frame.Image.Info.Layout);
            frame.Dispose();
        }

        Assert.AreEqual(1, camera.OpenCount, "相机只在第一次使用时打开一次。");
        Assert.AreEqual(3, camera.SingleCaptureCount);
        Assert.AreEqual(0, camera.CloseCount, "采集结束后不关闭相机。");
        Assert.AreEqual(0, camera.StartGrabCount, "单次采集不建立持续取流。");
        Assert.AreEqual(1500d, camera.AppliedExposure!.Value, "每次请求的曝光都要写到设备上。");
        CollectionAssert.AreEqual(
            new[] { "open", "capture-single", "capture-single", "capture-single" },
            camera.Events.ToArray(),
            "单次采集路径不得出现每次采集的新建/开关设备。");
    }

    /// <summary>采集失败不关闭相机，也不丢弃已建立的连接：排障后可以继续复用同一台设备。</summary>
    [TestMethod]
    public async Task CaptureAsync_Failure_KeepsCameraOpenForNextRequest()
    {
        var camera = new FakeStreamCamera
        {
            CaptureFrameFactory = () => throw new VisionCaptureTimeoutException("外部触发未到达")
        };
        await using var device = Device(camera);

        await Assert.ThrowsExactlyAsync<VisionCaptureTimeoutException>(
            async () => await device.CaptureAsync(Request(null, null), CancellationToken.None));

        Assert.IsTrue(camera.IsOpen);
        Assert.AreEqual(0, camera.CloseCount);

        camera.CaptureFrameFactory = null;
        var frame = await device.CaptureAsync(Request(null, null), CancellationToken.None);

        Assert.AreEqual(1, camera.OpenCount, "重试不得重新打开相机。");
        Assert.AreEqual(2, camera.SingleCaptureCount);
        frame.Dispose();
    }

    /// <summary>单次采集的设备对象由设备生命周期拥有：释放设备时才关闭并释放它一次。</summary>
    [TestMethod]
    public async Task DeviceDispose_AfterCaptureOnly_ClosesCameraOnce()
    {
        var camera = new FakeStreamCamera();
        var device = Device(camera);

        var frame = await device.CaptureAsync(Request(null, null), CancellationToken.None);
        frame.Dispose();

        await device.DisposeAsync();
        await device.DisposeAsync();

        Assert.IsFalse(camera.IsOpen);
        Assert.AreEqual(1, camera.CloseCount);
        Assert.AreEqual(1, camera.DisposeCount);
    }

    /// <summary>单次采集在途时不允许布防：同一条取流通道不能被两条路径同时占用。</summary>
    [TestMethod]
    public async Task StartStream_WhileCapturing_IsRejected()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var camera = new FakeStreamCamera { CaptureEntered = entered, CaptureRelease = release };
        await using var device = Device(camera);

        var capture = device.CaptureAsync(Request(null, null), CancellationToken.None).AsTask();
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "单次采集必须已经进入设备侧。");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None));
        Assert.AreEqual(0, camera.StartGrabCount, "拒绝必须发生在设备动作之前。");

        release.Set();
        (await capture).Dispose();

        Assert.AreEqual(1, camera.SingleCaptureCount);
        Assert.AreEqual(0, camera.CloseCount);
    }

    /// <summary>断线之后不再接受单次采集：故障原因必须先报告，而不是伪装成"正在布防"。</summary>
    [TestMethod]
    public async Task CaptureAsync_AfterStreamFailure_ReportsDisconnect()
    {
        var camera = new FakeStreamCamera();
        await using var device = Device(camera);
        await using var stream = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);
        camera.ReportFailure(new InvalidOperationException("相机断线"));

        var failure = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(
            async () => await device.CaptureAsync(Request(null, null), CancellationToken.None));

        StringAssert.Contains(failure.Message, "相机断线", "诊断必须带上断线原因。");
        Assert.IsTrue(camera.IsOpen);
    }

    private static VisionCaptureRequest Request(double? exposureMicroseconds, double? gainDecibels) =>
        new VisionCaptureRequest(
            TimeSpan.FromSeconds(1),
            triggerMode: EVisionTriggerMode.FreeRun,
            exposureMicroseconds: exposureMicroseconds,
            gainDecibels: gainDecibels);

    private static BaslerAcquisitionBinding Binding(string? triggerSource = null) =>
        new BaslerAcquisitionBinding("cam-top", serialNumber: "12345678", triggerSource: triggerSource);

    private static BaslerAcquisitionDevice Device(FakeStreamCamera camera, string? triggerSource = null) =>
        new BaslerAcquisitionDevice(Binding(triggerSource), _ => camera);
}
