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
/// 不同 <c>ResourceKey</c> 之间互不串行：一台相机打开慢、或停流慢，都不得把其他相机一起拖住。
/// <para>
/// 这类性质无法用计时阈值稳定断言（那会变成抖动测试），也无法靠"结果对不对"观察——
/// 串行实现最终也能全部打开、全部停流，只是慢。所以断言落在**事件顺序**上：
/// 一个资源还卡在原地时，另一个资源是否已经跑完。
/// </para>
/// <para>
/// 断言刻意**不假设资源的处理顺序**：组合体按 <c>SourceId</c> 排序（见
/// <c>VisionAcquisitionProviderComposition.Sources</c>），声明顺序不作数。
/// 因此这里卡住的是"第一个真正开始动作的资源"，再由测试自己分辨出另一个。
/// 若按名字假定谁先谁后，串行实现也会假绿。
/// </para>
/// <para>
/// 在串行实现下这两条用例都会在等待超时后失败——那正是它们要冻结的缺陷。
/// </para>
/// </summary>
[TestClass]
public sealed class ParallelResourceStartStopTests
{
    private const string ProviderId = "dp.fake.parallel";

    /// <summary>等待信号的耐心上限；只为让"串行实现"以失败而不是挂起收场。</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runtime Start 必须按 ResourceKey 并行打开设备：一台相机的 SDK 打开慢，不得让其他相机排队等它。
    /// </summary>
    [TestMethod]
    public async Task StartAsync_OneSlowOpen_DoesNotSerializeOtherResources()
    {
        var firstStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var completedCount = 0;
        var gateCount = 0;

        var provider = new BlockingVisionProvider(
            ProviderId,
            bindingId => new FakeVisionDevice(new VisionDeviceIdentity(ProviderId, bindingId)),
            // 只卡住"第一个开始打开"的资源；其余资源必须能在它完成之前跑完。
            blockOn: _ => Interlocked.Increment(ref gateCount) == 1 ? releaseFirst.Task : null,
            onOpenStarted: bindingId =>
            {
                if (Interlocked.Increment(ref startedCount) == 1)
                    firstStarted.TrySetResult(bindingId);
            },
            onOpened: bindingId =>
            {
                if (Interlocked.Increment(ref completedCount) == 1)
                    firstCompleted.TrySetResult(bindingId);
            });

        await using var runtime = new VisionAcquisitionRuntime(Compose(provider, new[]
        {
            OnDemandBinding("Camera.Slow", "slow-camera", "camera:serial:SLOW"),
            OnDemandBinding("Camera.Fast", "fast-camera", "camera:serial:FAST")
        }));

        var start = runtime.StartAsync(CancellationToken.None).AsTask();
        string first;
        string second;
        try
        {
            first = await AwaitOrFailAsync(firstStarted.Task, "至少有一台相机的打开动作必须已经开始。");

            // 关键断言：第一个资源还卡在打开里，另一个资源必须已经打开完成。
            second = await AwaitOrFailAsync(
                firstCompleted.Task,
                "第一个资源还卡在打开里时，另一个资源必须已经打开完成：不同 ResourceKey 必须并行打开。");
        }
        finally
        {
            // 断言失败时也要放行，否则慢打开会永远挂着，掩盖真正的失败原因。
            releaseFirst.TrySetResult(true);
        }

        Assert.AreNotEqual(first, second, "并行的必须是两个不同的资源键。");
        Assert.AreEqual(EVisionRuntimeState.Ready, await start, "两台相机都打开成功后运行时必须就绪。");
        Assert.AreEqual(2, Volatile.Read(ref startedCount), "两个 ResourceKey 各打开一次，不多不少。");
        Assert.AreEqual(
            EVisionConnectionState.Connected,
            runtime.GetDiagnostics("Camera.Fast")!.ConnectionState);
        Assert.AreEqual(
            EVisionConnectionState.Connected,
            runtime.GetDiagnostics("Camera.Slow")!.ConnectionState);
    }

    /// <summary>
    /// Runtime Stop 必须按 ResourceKey 并行停流：一台相机的停流慢，不得让其他相机跟着一起等。
    /// </summary>
    [TestMethod]
    public async Task StopAsync_OneSlowStreamStop_DoesNotSerializeOtherResources()
    {
        var firstStopped = new TaskCompletionSource<FakeStreamingVisionDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = 0;

        FakeStreamingVisionDevice? slowDevice = null;
        FakeStreamingVisionDevice? fastDevice = null;

        // 同一套闸门装到两台设备上：只有"第一个开始停流"的那台会被卡住。
        Func<FakeStreamingVisionDevice, Func<ValueTask>> makeGate = device => () =>
        {
            if (Interlocked.Increment(ref stopEntered) == 1)
            {
                firstStopped.TrySetResult(device);
                return new ValueTask(releaseFirst.Task);
            }

            return default;
        };

        var provider = new BlockingVisionProvider(
            ProviderId,
            bindingId =>
            {
                var device = new FakeStreamingVisionDevice(new VisionDeviceIdentity(ProviderId, bindingId));
                device.StreamStopGate = makeGate(device);
                if (bindingId == "slow-camera")
                    slowDevice = device;
                else
                    fastDevice = device;
                return device;
            });

        await using var runtime = new VisionAcquisitionRuntime(Compose(provider, new[]
        {
            BufferedBinding("Camera.Slow", "slow-camera", "camera:serial:SLOW"),
            BufferedBinding("Camera.Fast", "fast-camera", "camera:serial:FAST")
        }));

        await runtime.StartAsync(CancellationToken.None);
        Assert.IsTrue(slowDevice!.IsStreaming, "缓冲源必须在 Runtime Start 时布防。");
        Assert.IsTrue(fastDevice!.IsStreaming, "缓冲源必须在 Runtime Start 时布防。");

        var stop = runtime.StopAsync().AsTask();
        try
        {
            var first = await AwaitOrFailAsync(firstStopped.Task, "至少有一台相机的停流动作必须已经开始。");

            // 关键断言：第一台还卡在停流里，另一台必须已经停流完成。
            var other = ReferenceEquals(first, slowDevice) ? fastDevice : slowDevice;
            Assert.IsTrue(
                other.Events.Contains("stream-stop"),
                "第一台相机还卡在停流里时，另一台必须已经停流完成：不同 ResourceKey 必须并行停流。"
                + "实际事件：" + string.Join(" → ", other.Events));
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        await stop;

        Assert.IsTrue(slowDevice.Events.Contains("stream-stop"), "慢相机最终也必须停流。");
        Assert.IsTrue(fastDevice.Events.Contains("stream-stop"), "快相机最终也必须停流。");
        Assert.AreEqual(1, provider.DisposeCount, "停流之后仍必须释放Provider。");
    }

    private static async Task<T> AwaitOrFailAsync<T>(Task<T> signal, string message)
    {
        var completed = await Task.WhenAny((Task)signal, Task.Delay(Patience)).ConfigureAwait(false);
        Assert.AreSame(signal, completed, message);
        return await signal.ConfigureAwait(false);
    }

    private static VisionAcquisitionProviderComposition Compose(
        IVisionAcquisitionProvider provider,
        IReadOnlyList<VisionAcquisitionSourceBinding> sources) =>
        new VisionAcquisitionProviderComposer().Compose(
            new[]
            {
                new FakeVisionProviderModule(
                    "m.parallel",
                    new VisionAcquisitionProviderRegistration(ProviderId, "1.0.0", () => provider))
            },
            sources);

    private static VisionAcquisitionSourceBinding OnDemandBinding(
        string sourceId,
        string bindingId,
        string resourceKey) =>
        new VisionAcquisitionSourceBinding(sourceId, ProviderId, bindingId, resourceKey);

    private static VisionAcquisitionSourceBinding BufferedBinding(
        string sourceId,
        string bindingId,
        string resourceKey) =>
        new VisionAcquisitionSourceBinding(
            sourceId,
            ProviderId,
            bindingId,
            resourceKey,
            EVisionSourceSharingPolicy.ExclusiveRun,
            EVisionAcquisitionMode.BufferedExternal,
            new VisionFrameInboxPolicy(8, 4096, TimeSpan.FromMinutes(1)));
}
