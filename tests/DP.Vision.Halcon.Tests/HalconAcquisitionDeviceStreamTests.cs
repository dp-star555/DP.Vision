using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// 设备适配器在长连接模式下的生命周期回归：布防、复用、拒绝重复布防、释放顺序。
/// 这些结论都在设备契约这一层，不需要相机或 HALCON 许可证。
/// </summary>
[TestClass]
public sealed class HalconAcquisitionDeviceStreamTests
{
    /// <summary>布防触发模式由绑定是否声明触发源决定，不猜物理接线。</summary>
    /// <param name="triggerSource">绑定声明的触发源。</param>
    /// <param name="expected">期望的布防触发模式。</param>
    [TestMethod]
    [DataRow(null, EVisionTriggerMode.KeepCurrent)]
    [DataRow("Line1", EVisionTriggerMode.External)]
    public void ArmTriggerMode_FollowsBindingTriggerSource(string? triggerSource, EVisionTriggerMode expected)
    {
        Assert.AreEqual(expected, HalconAcquisitionDevice.ResolveArmTriggerMode(Binding(triggerSource)));
    }

    /// <summary>布防打开设备、写入布防参数（含抓取超时与触发源），并把设备帧交付给接收方。</summary>
    [TestMethod]
    public async Task StartStream_ArmsCameraAndDeliversFrames()
    {
        var camera = new FakeHalconStreamCamera();
        await using var device = Device(camera, "Line1");
        var sink = new RecordingStreamSink();

        await using var stream = await device.StartStreamAsync(sink, CancellationToken.None);

        Assert.AreEqual(1, camera.OpenCount);
        Assert.AreEqual(EVisionTriggerMode.External, camera.AppliedTriggerMode);
        Assert.AreEqual("Line1", camera.AppliedTriggerSource);
        Assert.AreEqual(2500, camera.AppliedGrabTimeout);

        camera.Feed(new FakeHalconGrabFrame(2, 1, "byte", new byte[] { 9, 8 }));
        Assert.IsTrue(sink.WaitForFrames(1));
        Assert.AreEqual(1, sink.Frames.Count);
        sink.DisposeFrames();
    }

    /// <summary>第二根根运行复用已打开的设备，不重新打开相机。</summary>
    [TestMethod]
    public async Task SecondArm_ReusesOpenCameraInsteadOfOpeningAgain()
    {
        var camera = new FakeHalconStreamCamera();
        await using var device = Device(camera);

        var first = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);
        await first.DisposeAsync();

        await using var second = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        Assert.AreEqual(1, camera.OpenCount, "退役只停流，设备在两次布防之间保持打开。");
        Assert.IsTrue(camera.IsOpen);
        Assert.AreEqual(0, camera.CloseCount);
    }

    /// <summary>已经在布防状态时重复布防明确拒绝，且不替换原接收方。</summary>
    [TestMethod]
    public async Task SecondArm_WhileArmed_IsRejectedWithoutReplacingSink()
    {
        var camera = new FakeHalconStreamCamera();
        await using var device = Device(camera);
        var first = new RecordingStreamSink();

        await using var stream = await device.StartStreamAsync(first, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None));

        camera.Feed(new FakeHalconGrabFrame(2, 1, "byte", new byte[] { 1, 2 }));
        Assert.IsTrue(first.WaitForFrames(1), "原接收方必须仍然是唯一的接收方。");
        first.DisposeFrames();
    }

    /// <summary>布防参数写不进设备时设备保持打开，下一次布防可以直接重试。</summary>
    [TestMethod]
    public async Task ArmFailure_KeepsCameraOpenForRetry()
    {
        var camera = new FakeHalconStreamCamera
        {
            ApplyFailure = new VisionSourceConfigurationException("触发源写不进设备")
        };
        await using var device = Device(camera, "Line1");

        await Assert.ThrowsExactlyAsync<VisionSourceConfigurationException>(
            async () => await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None));

        Assert.IsTrue(camera.IsOpen, "布防失败不应关闭设备：下一次布防要能复用同一个已打开的设备。");
        Assert.AreEqual(0, camera.CloseCount);

        camera.ApplyFailure = null;
        await using var stream = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        Assert.AreEqual(1, camera.OpenCount, "重试不得重新打开设备。");
    }

    /// <summary>缓冲源布防期间不允许同一设备按请求单次采集。</summary>
    [TestMethod]
    public async Task CaptureAsync_WhileArmed_IsRejected()
    {
        var camera = new FakeHalconStreamCamera();
        await using var device = Device(camera);
        await using var stream = await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<VisionSourceConfigurationException>(
            async () => await device.CaptureAsync(
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)), CancellationToken.None));
    }

    /// <summary>释放顺序是契约的一部分：先停流（中止抓取并等采集线程退出），再关闭设备。</summary>
    [TestMethod]
    public async Task DeviceDispose_StopsStreamBeforeClosingCamera()
    {
        var camera = new FakeHalconStreamCamera();
        var device = Device(camera);
        await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None);

        await device.DisposeAsync();

        Assert.IsFalse(camera.IsOpen);
        Assert.AreEqual(1, camera.DisposeCount);
        CollectionAssert.AreEqual(
            new[] { "Open", "Apply", "Abort", "Close", "Dispose" },
            camera.Events.ToArray(),
            "必须先停流（中止抓取、等待采集线程与在途交付退出），再关闭设备。");
    }

    /// <summary>重复释放是空操作，设备只关闭一次。</summary>
    [TestMethod]
    public async Task DeviceDispose_IsIdempotent()
    {
        var camera = new FakeHalconStreamCamera();
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
        var camera = new FakeHalconStreamCamera();
        var device = Device(camera);

        await device.DisposeAsync();

        Assert.AreEqual(0, camera.OpenCount);
        Assert.AreEqual(0, camera.DisposeCount);
    }

    /// <summary>已释放的设备拒绝再次布防。</summary>
    [TestMethod]
    public async Task StartStream_AfterDeviceDisposed_Throws()
    {
        var camera = new FakeHalconStreamCamera();
        var device = Device(camera);
        await device.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await device.StartStreamAsync(new RecordingStreamSink(), CancellationToken.None));
    }

    private static HalconAcquisitionBinding Binding(string? triggerSource = null) =>
        new HalconAcquisitionBinding(
            "top-camera",
            "GigEVision2",
            "cam-top",
            serialNumber: "DEMO0001",
            triggerSource: triggerSource,
            grabTimeoutMilliseconds: 2500);

    private static HalconAcquisitionDevice Device(FakeHalconStreamCamera camera, string? triggerSource = null) =>
        new HalconAcquisitionDevice(Binding(triggerSource), _ => camera);
}
