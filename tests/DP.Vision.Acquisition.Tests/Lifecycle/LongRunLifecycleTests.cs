using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// V2-9 第 4 条：长时间运行、断线、重连与关闭时序。
/// <para>
/// 与本组用例对应的都是"跑久了才会暴露"的问题：设备被反复打开、接收流被反复重建、
/// 连接修订号悄悄增长、停止时把在途采集丢在设备释放之后。它们在短用例里都看不出来。
/// </para>
/// <para>
/// 断线、重连是 V2 的生命周期语义：设备只在 Runtime Start 打开、Stop 关闭，
/// 因此"重连"只能表现为新建 Runtime 再 Start，设备对象与接收流随之重建；
/// 不存在"就地重连"的隐藏路径。
/// </para>
/// </summary>
[TestClass]
public sealed class LongRunLifecycleTests
{
    private const string ProviderId = "dp.fake.stream";
    private const string SourceId = "Camera.Stream";

    /// <summary>多轮根运行长跑：设备、SDK 对象与接收流全程只建立一次，连接修订保持为 1。</summary>
    [TestMethod]
    public async Task ManyEpochs_KeepSingleDeviceAndSingleStream()
    {
        var fixture = new StreamingFixture();
        await using var runtime = fixture.CreateRuntime();
        await runtime.StartAsync(CancellationToken.None);
        var device = fixture.Devices.Single();

        const int Rounds = 20;
        for (var round = 0; round < Rounds; round++)
        {
            var lease = await runtime.BeginRunAsync("run-" + round, CancellationToken.None);
            Assert.IsTrue(device.Emit(deviceSequence: round * 2 + 1, seed: 1));
            Assert.IsTrue(device.Emit(deviceSequence: round * 2 + 2, seed: 2));

            for (var index = 0; index < 2; index++)
            {
                using (var captured = await CaptureAsync(runtime))
                {
                    Assert.AreEqual(round * 2 + index + 1L, captured.Metadata.DeviceSequence);
                }
            }

            await lease.DisposeAsync();
            Assert.AreEqual(0, runtime.GetDiagnostics(SourceId)!.InboxCount, "退役必须清空本代次未领取帧。");
        }

        var diagnostics = runtime.GetDiagnostics(SourceId)!;
        Assert.AreEqual(Rounds, diagnostics.Epoch, "每轮根运行必须推进采集代次。");
        Assert.AreEqual(Rounds * 2L, diagnostics.FramesReceived);
        Assert.AreEqual(Rounds * 2L, diagnostics.FramesClaimed);
        Assert.AreEqual(0L, diagnostics.UnclaimedAtEpochEnd, "每轮都领完，收口不得有未领取帧。");
        Assert.AreEqual(1, diagnostics.ConnectionRevision, "长跑途中不得发生任何重连。");

        Assert.AreEqual(1, fixture.Devices.Count, "多轮根运行只创建一个设备对象。");
        Assert.AreEqual(1, fixture.Provider.OpenedBindings.Count, "多轮根运行只打开一次设备。");
        Assert.AreEqual(1, device.StreamStartCount, "接收流只布防一次，跨根运行保持。");
        Assert.AreEqual(0, device.DisposeCount);
        Assert.AreEqual(0, device.StreamDisposeCount);

        await runtime.DisposeAsync();
        Assert.AreEqual(1, device.DisposeCount);
        Assert.AreEqual(1, device.StreamDisposeCount);
        CollectionAssert.AreEqual(
            new[] { "stream-start", "stream-stop", "device-dispose" },
            new List<string>(device.Events).ToArray(),
            "必须先停流再释放设备。");
    }

    /// <summary>断线：接收流意外结束后源进入故障态并保留结束原因，后续采集明确失败而不是等到超时。</summary>
    [TestMethod]
    public async Task StreamFailure_FaultsSourceWithStreamFailureKind()
    {
        var fixture = new StreamingFixture();
        await using var runtime = fixture.CreateRuntime();
        await runtime.StartAsync(CancellationToken.None);
        var lease = await runtime.BeginRunAsync("run-1", CancellationToken.None);
        var device = fixture.Devices.Single();

        var pending = CaptureAsync(runtime);
        for (var index = 0; index < 50 && !pending.IsCompleted; index++)
            await Task.Yield();

        Assert.IsTrue(device.CompleteStream(new InvalidOperationException("相机断线")));

        var failure = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () => await pending);
        StringAssert.Contains(failure.Message, "StreamFailure");
        StringAssert.Contains(failure.Message, "相机断线", "必须保留底层结束原因，否则现场分不清断线与正常停止。");

        var diagnostics = runtime.GetDiagnostics(SourceId)!;
        Assert.AreEqual("StreamFailure", diagnostics.FaultKind);
        Assert.AreEqual(EVisionConnectionState.Faulted, diagnostics.ConnectionState);
        Assert.IsTrue(diagnostics.IsFaulted);

        // 故障是终态：不重连、不自愈，后续采集同样明确失败。
        await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () => await CaptureAsync(runtime));
        await lease.DisposeAsync();
    }

    /// <summary>
    /// 重连：放弃旧 Runtime、新建 Runtime 再 Start，设备对象随之重建（Open 次数 +1）。
    /// <para>
    /// 这是 V2 唯一的重连路径——设备连接属于软件生命周期，节点与根运行都不得隐式重连。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Reconnect_UsesNewRuntimeAndOpensDeviceAgain()
    {
        var fixture = new StreamingFixture();

        var first = fixture.CreateRuntime();
        await first.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, fixture.Devices.Count);
        await first.DisposeAsync();
        Assert.AreEqual(1, fixture.Devices[0].DisposeCount, "Stop 必须释放唯一 SDK 设备对象。");
        Assert.AreEqual(1, fixture.Devices[0].StreamDisposeCount, "Stop 必须显式停流。");

        await using var second = fixture.CreateRuntime();
        await second.StartAsync(CancellationToken.None);

        Assert.AreEqual(2, fixture.Devices.Count, "重连必须重建设备对象，不能复用已释放的对象。");
        Assert.AreEqual(2, fixture.Provider.OpenedBindings.Count, "每次重连都要真实打开一次设备。");
        Assert.AreEqual(1, second.GetDiagnostics(SourceId)!.ConnectionRevision, "新 Runtime = 新会话，连接修订重新从 1 记起。");
        Assert.AreEqual(1, fixture.Devices[1].StreamStartCount);
        Assert.AreEqual(0, fixture.Devices[1].DisposeCount);
    }

    /// <summary>
    /// 关闭时序：采集在途时 StopAsync 必须等它退出，之后才释放设备。
    /// <para>
    /// 顺序反过来就是在回调/采集可能仍在读像素时释放 SDK 对象——现场表现为偶发崩溃，
    /// 而这正是 V2 关闭契约里最容易被"看起来能跑"掩盖的一条。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task StopAwaitsInFlightCaptureBeforeDisposingDevice()
    {
        FakeVisionDevice? device = null;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var provider = new FakeVisionProvider(ProviderId, binding =>
        {
            device = new FakeVisionDevice(new VisionDeviceIdentity(ProviderId, binding), async (request, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return new VisionProviderFrame(TestImages.Gray8(seed: 1), DateTimeOffset.UtcNow);
            });
            return device;
        });

        await using var runtime = new VisionAcquisitionRuntime(ComposeOnDemand(provider));
        await runtime.StartAsync(CancellationToken.None);

        var capture = CaptureAsync(runtime);
        await entered.Task;

        var stop = runtime.StopAsync().AsTask();
        for (var index = 0; index < 50 && !stop.IsCompleted; index++)
            await Task.Yield();
        Assert.IsFalse(stop.IsCompleted, "在途采集未退出时停止不得完成。");
        Assert.AreEqual(0, device!.DisposeCount, "设备必须在在途采集退出之后才释放。");

        // 停止后不再接受新采集，也不得隐式重开设备：失败信息取决于会话是否已清退，
        // 但「明确失败」与「Open 次数不增加」这两条是契约。
        await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () => await CaptureAsync(runtime));
        Assert.AreEqual(1, provider.OpenedBindings.Count, "停止路径不得隐式重连设备。");

        release.SetResult(true);
        using (var captured = await capture)
            Assert.IsNotNull(captured);

        await stop;
        Assert.AreEqual(1, device.DisposeCount);
    }

    private static Task<VisionCapturedImage> CaptureAsync(VisionAcquisitionRuntime runtime) =>
        runtime.CaptureAsync(
            new VisionSourceReference(SourceId),
            new VisionCaptureRequest(TimeSpan.FromSeconds(5)),
            new VisionAcquisitionOwner("run-1", "node-1"),
            CancellationToken.None).AsTask();

    private static VisionAcquisitionProviderComposition ComposeOnDemand(FakeVisionProvider provider) =>
        new VisionAcquisitionProviderComposer().Compose(
            new[]
            {
                new FakeVisionProviderModule(
                    "m.ondemand",
                    new VisionAcquisitionProviderRegistration(ProviderId, "1.0.0", () => provider))
            },
            new[] { new VisionAcquisitionSourceBinding(SourceId, ProviderId, "stream-camera", "camera:serial:STREAM") });

    /// <summary>
    /// 长跑装配：一个 Provider 实例、按每次打开创建设备、一个外部回调缓冲源。
    /// <para>Provider 实例刻意跨 Runtime 复用，因此 <c>OpenedBindings</c> 能横跨重连累计打开次数。</para>
    /// </summary>
    private sealed class StreamingFixture
    {
        private readonly FakeVisionProvider _provider;
        private readonly List<FakeStreamingVisionDevice> _devices = new List<FakeStreamingVisionDevice>();

        public StreamingFixture()
        {
            _provider = new FakeVisionProvider(ProviderId, binding =>
            {
                var device = new FakeStreamingVisionDevice(new VisionDeviceIdentity(ProviderId, binding));
                _devices.Add(device);
                return device;
            });
            Composition = new VisionAcquisitionProviderComposer().Compose(
                new[]
                {
                    new FakeVisionProviderModule(
                        "m.stream",
                        new VisionAcquisitionProviderRegistration(ProviderId, "1.0.0", () => _provider))
                },
                new[]
                {
                    new VisionAcquisitionSourceBinding(
                        SourceId,
                        ProviderId,
                        "stream-camera",
                        "camera:serial:STREAM",
                        EVisionSourceSharingPolicy.ExclusiveRun,
                        EVisionAcquisitionMode.BufferedExternal,
                        new VisionFrameInboxPolicy(capacity: 8, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1)))
                });
        }

        public VisionAcquisitionProviderComposition Composition { get; }

        public FakeVisionProvider Provider => _provider;

        /// <summary>按打开顺序创建过的设备；每新建一个 Runtime 就多一个。</summary>
        public IReadOnlyList<FakeStreamingVisionDevice> Devices => _devices;

        public VisionAcquisitionRuntime CreateRuntime() => new VisionAcquisitionRuntime(Composition);
    }
}