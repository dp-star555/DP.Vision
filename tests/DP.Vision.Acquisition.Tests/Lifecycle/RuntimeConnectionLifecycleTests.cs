using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// V2-3 应用级连接生命周期验收（实施基线 §21 第 4–9 项）：
/// 同一ResourceKey只创建一个Device；Runtime Start真实Open一次；多次OnDemand Capture不复开不关闭；
/// 节点不得隐式打开/关闭设备；Required失败→NotReady；Optional失败→Degraded且该Source不可用。
/// </summary>
[TestClass]
public sealed class RuntimeConnectionLifecycleTests
{
    private readonly VisionAcquisitionProviderComposer _composer = new VisionAcquisitionProviderComposer();

    /// <summary>§21-4：同一ResourceKey的多个Source共享同一个Device与同一个Provider，不重复创建。</summary>
    [TestMethod]
    public async Task SameResourceKey_StartsSingleDevice()
    {
        var factory = new RecordingProviderFactory();
        var composition = _composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one")) },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top-camera", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Alias", "dp.fake.one", "top-camera", "camera:serial:A")
            });
        await using var runtime = new VisionAcquisitionRuntime(composition);

        var state = await runtime.StartAsync(CancellationToken.None);
        Assert.AreEqual(EVisionRuntimeState.Ready, state);

        Assert.AreEqual(1, factory.Created.Count, "同一ResourceKey只创建一个Provider/Device Adapter。");
        Assert.AreEqual(1, factory.Last.OpenedBindings.Count, "同一ResourceKey只打开一次设备。");

        // 两个Source共享同一连接，都报告Connected并携带同一设备规范身份诊断。
        var top = runtime.GetDiagnostics("Camera.Top")!;
        var alias = runtime.GetDiagnostics("Camera.Alias")!;
        Assert.AreEqual(EVisionConnectionState.Connected, top.ConnectionState);
        Assert.AreEqual(EVisionConnectionState.Connected, alias.ConnectionState);
        Assert.AreEqual(top.ConnectionMessage, alias.ConnectionMessage);
    }

    /// <summary>§21-5/6：Start真实Open一次；连续多次OnDemand Capture不复开不关闭；直到Runtime停止才关闭一次。</summary>
    [TestMethod]
    public async Task MultipleCaptures_OpenOnceAndNeverCloseUntilStop()
    {
        var factory = new RecordingProviderFactory();
        await using var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top-camera", "camera:serial:A") }));

        await runtime.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, factory.Last.OpenedBindings.Count, "Runtime Start必须真实打开设备。");

        for (var index = 0; index < 3; index++)
        {
            using var captured = await runtime.CaptureAsync(
                new VisionSourceReference("Camera.Top"),
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
                new VisionAcquisitionOwner("run-1", "node-" + index),
                CancellationToken.None);
            Assert.IsNotNull(captured);
            Assert.AreEqual(1, factory.Last.OpenedBindings.Count, $"第 {index + 1} 次采集不得重新打开设备。");
        }

        Assert.AreEqual(0, factory.Last.DisposeCount, "采集结束不关闭设备。");

        await runtime.StopAsync();
        Assert.AreEqual(1, factory.Last.DisposeCount, "Runtime停止才关闭设备，且只关闭一次。");
    }

    /// <summary>§21-6/7：Runtime未启动时节点采集明确失败，节点不得隐式打开设备。</summary>
    [TestMethod]
    public async Task UnstartedRuntime_NodeCaptureFailsWithoutImplicitOpen()
    {
        var factory = new RecordingProviderFactory();
        await using var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top-camera", "camera:serial:A") }));

        var offline = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                new VisionSourceReference("Camera.Top"),
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
                new VisionAcquisitionOwner("run-1", "node-1"),
                CancellationToken.None);
        });
        StringAssert.Contains(offline.Message, "StartAsync");

        Assert.AreEqual(0, factory.Created.Count, "节点不得隐式打开或重连设备。");
    }

    /// <summary>§21-7：Runtime未启动时根运行布防同样明确失败，不得隐式打开设备。</summary>
    [TestMethod]
    public async Task UnstartedRuntime_RootRunFailsWithoutImplicitOpen()
    {
        var factory = new RecordingProviderFactory();
        await using var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one")) },
            new[]
            {
                new VisionAcquisitionSourceBinding(
                    "Camera.Stream", "dp.fake.one", "stream-camera", "camera:serial:A",
                    EVisionSourceSharingPolicy.ExclusiveRun, EVisionAcquisitionMode.BufferedExternal,
                    new VisionFrameInboxPolicy(8, 4096, TimeSpan.FromMinutes(1)))
            }));

        var offline = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(
            async () => await runtime.BeginRunAsync("run-1", CancellationToken.None));
        StringAssert.Contains(offline.Message, "StartAsync");

        Assert.AreEqual(0, factory.Created.Count, "根运行布防不得隐式打开设备。");
    }

    /// <summary>§21-8：Required设备打开失败时Runtime进入NotReady，不得伪装成继续可用；采集明确失败。</summary>
    [TestMethod]
    public async Task RequiredFailure_RuntimeIsNotReady()
    {
        var failing = new FakeVisionProvider("dp.fake.one", binding => throw new InvalidOperationException("打开失败。"));
        await using var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[]
            {
                new FakeVisionProviderModule(
                    "m.one", new VisionAcquisitionProviderRegistration("dp.fake.one", "1.0.0", () => failing))
            },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top-camera", "camera:serial:A") }));

        var state = await runtime.StartAsync(CancellationToken.None);
        Assert.AreEqual(EVisionRuntimeState.NotReady, state);
        Assert.IsFalse(runtime.IsReady);
        Assert.IsFalse(runtime.IsDegraded);

        var diag = runtime.GetDiagnostics("Camera.Top")!;
        Assert.IsTrue(diag.IsFaulted);
        Assert.AreEqual(EVisionConnectionState.Faulted, diag.ConnectionState);
        StringAssert.Contains(diag.FaultMessage, "打开失败");

        var offline = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                new VisionSourceReference("Camera.Top"),
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
                new VisionAcquisitionOwner("run-1", "node-1"),
                CancellationToken.None);
        });
        StringAssert.Contains(offline.Message, "打开失败");
    }

    /// <summary>§21-9：Optional设备打开失败时Runtime降级为Degraded；失败Source不可用但整体可继续采集。</summary>
    [TestMethod]
    public async Task OptionalFailure_RuntimeDegradesAndSourceUnavailable()
    {
        var optional = new FakeVisionProvider("dp.fake.optional", binding => throw new InvalidOperationException("打开失败。"));
        var healthy = new RecordingProviderFactory();
        var composition = _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule(
                    "m.optional", new VisionAcquisitionProviderRegistration("dp.fake.optional", "1.0.0", () => optional)),
                new FakeVisionProviderModule("m.healthy", healthy.Registration("dp.fake.healthy"))
            },
            new[]
            {
                new VisionAcquisitionSourceBinding(
                    "Camera.Optional", "dp.fake.optional", "opt-camera", "camera:serial:OPT", isRequired: false),
                new VisionAcquisitionSourceBinding("Camera.Main", "dp.fake.healthy", "main-camera", "camera:serial:MAIN")
            });
        await using var runtime = new VisionAcquisitionRuntime(composition);

        var state = await runtime.StartAsync(CancellationToken.None);
        Assert.AreEqual(EVisionRuntimeState.Degraded, state);
        Assert.IsTrue(runtime.IsDegraded);
        Assert.IsFalse(runtime.IsReady);

        // 失败Source不可用，但保留完整诊断。
        var optionalDiag = runtime.GetDiagnostics("Camera.Optional")!;
        Assert.IsTrue(optionalDiag.IsFaulted);
        Assert.AreEqual(EVisionConnectionState.Faulted, optionalDiag.ConnectionState);
        StringAssert.Contains(optionalDiag.FaultMessage, "打开失败");

        // 整体仍可运行：Required源采集正常。
        using var captured = await runtime.CaptureAsync(
            new VisionSourceReference("Camera.Main"),
            new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
            new VisionAcquisitionOwner("run-1", "node-1"),
            CancellationToken.None);
        Assert.IsNotNull(captured);
        Assert.AreEqual("camera:serial:MAIN", captured.Metadata.ResourceKey);
    }
}
