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
/// 外部回调缓冲源（<see cref="EVisionAcquisitionMode.BufferedExternal"/>）的运行时行为，
/// 对应实施基线 <c>ACQUISITION_RUNTIME_V1.md</c> §14 第 2–10、12、14、17、18 项。
/// <para>
/// 全部通过公共 API 驱动：布防、逐帧推动回调、领取、诊断。帧的推送完全由测试触发，
/// 不使用计时器，因此结果确定可复现。
/// </para>
/// <para>
/// 第 11 项（Nested 准备不清空父 Epoch）属于 V1-C 的工作流接线，不在本文件。
/// </para>
/// </summary>
[TestClass]
public sealed class BufferedExternalInboxTests
{
    private const string ProviderId = "dp.fake.stream";
    private const string SourceId = "Camera.Stream";

    /// <summary>§14-2：回调先于采集节点到达时，Capture 立即领取——这正是缓冲模式存在的理由。</summary>
    [TestMethod]
    public async Task CallbackBeforeCapture_IsClaimedImmediately()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync();

        Assert.IsTrue(rig.Device.Emit(deviceSequence: 11, seed: 3), "布防后回调必须被交付给会话。");

        // 用块而不是 using 声明：均衡等式要求断言时帧句柄已经释放。
        using (var captured = await rig.CaptureAsync())
        {
            Assert.AreEqual(11L, captured.Metadata.DeviceSequence);
            Assert.AreEqual(EVisionAcquisitionMode.BufferedExternal, captured.Metadata.AcquisitionMode);
            Assert.IsNotNull(captured.Metadata.ReceivedSequence, "缓冲来源必须带 Runtime 接收序号。");
            Assert.IsNotNull(captured.Metadata.ReceivedAtUtc, "缓冲来源必须带 Runtime 接收时刻。");
            Assert.AreEqual(SourceId, captured.Metadata.SourceId);
            Assert.AreEqual(1, captured.Metadata.ReceivedSequence);
        }

        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());
    }

    /// <summary>§14-3：Capture 先等待，回调到达后完成。</summary>
    [TestMethod]
    public async Task CaptureBeforeCallback_CompletesWhenFrameArrives()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync();

        var pending = rig.CaptureAsync(TimeSpan.FromSeconds(10));

        // 让领取有机会推进到等待状态：若实现错误地立即返回空帧，这里就会暴露。
        for (var index = 0; index < 50 && !pending.IsCompleted; index++)
            await Task.Yield();
        Assert.IsFalse(pending.IsCompleted, "没有可用帧时领取必须阻塞等待回调，而不是立即返回。");

        Assert.IsTrue(rig.Device.Emit(deviceSequence: 21, seed: 4));

        using var captured = await pending;
        Assert.AreEqual(21L, captured.Metadata.DeviceSequence);
        Assert.AreEqual(1, rig.Diagnostics.FramesClaimed);
    }

    /// <summary>§14-4：三帧按 FIFO 顺序领取，设备序号原样透传。</summary>
    [TestMethod]
    public async Task ThreeFrames_AreClaimedInFifoOrder()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync();

        Assert.IsTrue(rig.Device.Emit(1, seed: 1));
        Assert.IsTrue(rig.Device.Emit(2, seed: 2));
        Assert.IsTrue(rig.Device.Emit(3, seed: 3));
        Assert.AreEqual(3, rig.Diagnostics.InboxCount, "三帧必须都在待领取队列里。");

        var sequences = new List<long?>();
        for (var index = 0; index < 3; index++)
        {
            using var captured = await rig.CaptureAsync();
            sequences.Add(captured.Metadata.DeviceSequence);
        }

        CollectionAssert.AreEqual(new long?[] { 1, 2, 3 }, sequences);
        Assert.AreEqual(0, rig.Diagnostics.InboxCount);
        Assert.AreEqual(3, rig.Diagnostics.FramesClaimed);
        Assert.AreEqual(3, rig.Diagnostics.InboxHighWatermark);
        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());
    }

    /// <summary>§14-5：两个并行领取确定性拒绝其中一个，且失败者报告占用者。</summary>
    [TestMethod]
    public async Task ParallelClaims_RejectExactlyOneDeterministically()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync();

        var first = rig.CaptureAsync(TimeSpan.FromSeconds(10));
        var second = rig.CaptureAsync(TimeSpan.FromSeconds(10));

        var probeFirst = ProbeAsync(first);
        var probeSecond = ProbeAsync(second);
        var loserProbe = await Task.WhenAny(probeFirst, probeSecond);

        // 只有一个可以失败：赢家必须仍在等待，否则说明两个都被拒绝了。
        var winner = ReferenceEquals(loserProbe, probeFirst) ? second : first;
        var winnerProbe = ReferenceEquals(loserProbe, probeFirst) ? probeSecond : probeFirst;
        Assert.IsFalse(winnerProbe.IsCompleted, "两个并行领取中必须有一个仍在等待，不能两个都失败。");

        var failure = await loserProbe;
        Assert.IsNotNull(failure, "两个并行领取必须有一个确定性失败。");
        Assert.AreEqual(typeof(VisionResourceConflictException), failure!.GetType(), failure.Message);
        StringAssert.Contains(failure.Message, "只允许一个等待中的领取");

        // 赢家仍持有领取门，回调到达后必须正常完成。
        Assert.IsTrue(rig.Device.Emit(31, seed: 5));
        using var captured = await winner;
        Assert.AreEqual(31L, captured.Metadata.DeviceSequence);
    }

    /// <summary>§14-6：取消等待不得吞掉随后到达的帧。</summary>
    [TestMethod]
    public async Task CancelledClaim_DoesNotSwallowNextFrame()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync();

        using var cancellation = new CancellationTokenSource();
        var pending = rig.CaptureAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await pending);

        // 取消只是放弃本次等待；帧仍归下一领取者，不能被本次取消顺手消费掉。
        Assert.IsTrue(rig.Device.Emit(41, seed: 6));
        using var captured = await rig.CaptureAsync();
        Assert.AreEqual(41L, captured.Metadata.DeviceSequence);
        Assert.AreEqual(1, rig.Counter.Retains);
    }

    /// <summary>§14-7：取消与回调同时发生时，帧只能有一个所有者，且最终只释放一次。</summary>
    [TestMethod]
    public async Task CancelRacingCallback_LeavesFrameWithSingleOwner()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync();

        using var cancellation = new CancellationTokenSource();
        var pending = rig.CaptureAsync(TimeSpan.FromSeconds(10), cancellation.Token);

        // 先入队再取消：帧与取消几乎同时到达，两种结果都合法，但都必须只有一个所有者。
        Assert.IsTrue(rig.Device.Emit(51, seed: 7));
        cancellation.Cancel();

        VisionCapturedImage? captured = null;
        try
        {
            captured = await pending;
        }
        catch (OperationCanceledException)
        {
        }

        captured?.Dispose();
        await rig.DisposeAsync();

        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());
        Assert.AreEqual(captured is null ? 0 : 1, rig.Counter.Retains);
        Assert.AreEqual(1, rig.Counter.Created, "只发出过一帧。");
        Assert.AreEqual(
            captured is null ? 1 : 2,
            rig.Counter.Disposes,
            "取消胜出时帧留在队列里由退役清退；领取胜出时 Provider 句柄与帧句柄各释放一次。");
    }

    /// <summary>§14-8：队列达到容量后 Source 进入 Faulted，新帧被释放且不静默覆盖。</summary>
    [TestMethod]
    public async Task InboxOverflow_FaultsSourceAndReleasesFrames()
    {
        await using var rig = new Rig(new VisionFrameInboxPolicy(capacity: 2, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1)));
        await rig.ArmAsync();

        Assert.IsTrue(rig.Device.Emit(1, seed: 1));
        Assert.IsTrue(rig.Device.Emit(2, seed: 2));
        Assert.IsTrue(rig.Device.Emit(3, seed: 3), "溢出帧也必须被交付；拒绝发生在会话内部而不是回调边界。");

        var diagnostics = rig.Diagnostics;
        Assert.IsTrue(diagnostics.IsFaulted, "队列溢出必须把 Source 标记为故障，而不是丢弃最旧帧。");
        Assert.AreEqual("InboxOverflow", diagnostics.FaultKind);
        StringAssert.Contains(diagnostics.FaultMessage, "溢出");
        Assert.AreEqual(1, diagnostics.FramesRejected, "只有第三帧因容量不足被拒绝。");
        Assert.AreEqual(0, diagnostics.InboxCount, "故障后不再有领取路径，待领取帧必须一并释放。");
        Assert.AreEqual(0, diagnostics.FramesClaimed);
        Assert.AreEqual(3, rig.Counter.Disposes, "被拒绝帧与故障清退帧都必须恰好释放一次。");
        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());

        // 故障后不再接受领取，并且诊断要说明类别。
        var offline = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(
            async () => await rig.CaptureAsync(TimeSpan.FromMilliseconds(200)));
        StringAssert.Contains(offline.Message, "InboxOverflow");
    }

    /// <summary>§14-9：超龄帧被释放，不能成功返回。</summary>
    [TestMethod]
    public async Task ExpiredFrame_IsReleasedAndNeverReturned()
    {
        await using var rig = new Rig(new VisionFrameInboxPolicy(capacity: 8, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMilliseconds(30)));
        await rig.ArmAsync();

        Assert.IsTrue(rig.Device.Emit(1, seed: 1));
        await Task.Delay(300);

        await Assert.ThrowsExactlyAsync<VisionCaptureTimeoutException>(
            async () => await rig.CaptureAsync(TimeSpan.FromMilliseconds(100)));

        var diagnostics = rig.Diagnostics;
        Assert.AreEqual(1, diagnostics.FramesExpired);
        Assert.AreEqual(0, diagnostics.FramesClaimed);
        Assert.AreEqual(0, diagnostics.InboxCount);
        Assert.AreEqual(1, rig.Counter.Disposes, "超龄帧必须在领取尝试中被释放，而不是留在队列里。");
        Assert.AreEqual(0, rig.Counter.Retains, "超龄帧不得被返回给调用方。");
        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());
    }

    /// <summary>§14-10：上一根运行的未领取帧不得进入下一根运行。</summary>
    [TestMethod]
    public async Task PreviousRunFrames_DoNotEnterNextRun()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync("run-1");
        Assert.AreEqual(1, rig.Lease.Epoch);

        Assert.IsTrue(rig.Device.Emit(1, seed: 1));
        Assert.IsTrue(rig.Device.Emit(2, seed: 2));
        Assert.AreEqual(2, rig.Diagnostics.InboxCount);

        await rig.Lease.DisposeAsync();
        Assert.AreEqual(0, rig.Diagnostics.InboxCount, "退役本轮必须释放未领取帧。");
        Assert.AreEqual(2, rig.Counter.Disposes);

        await rig.ArmAsync("run-2");
        Assert.AreEqual(2, rig.Lease.Epoch, "新一轮必须推进采集代次。");

        // 旧代次的帧既不能被领取，也不能被重复释放。
        await Assert.ThrowsExactlyAsync<VisionCaptureTimeoutException>(
            async () => await rig.CaptureAsync(TimeSpan.FromMilliseconds(100)));
        Assert.AreEqual(0, rig.Diagnostics.FramesClaimed);
        Assert.AreEqual(2, rig.Counter.Disposes);
        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());
    }

    /// <summary>
    /// 第二根根运行复用同一台已打开的设备，不重新打开相机。
    /// <para>
    /// 设备在两次布防之间保持打开（退役只停流），所以第二轮必须复用会话里已有的设备：
    /// 重新打开会让真实相机第二次直接失败（同一进程通常无法独占打开同一台相机），
    /// 并且会把上一根运行持有的设备对象漏掉——它既不会停流，也不会被释放。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task SecondRun_ReusesOpenDeviceInsteadOfOpeningCameraAgain()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync("run-1");
        Assert.IsTrue(rig.Device.Emit(1, seed: 1));
        await rig.Lease.DisposeAsync();

        await rig.ArmAsync("run-2");
        Assert.AreEqual(2, rig.Lease.Epoch, "新一轮必须推进采集代次。");
        Assert.IsTrue(rig.Device.Emit(2, seed: 2));

        // 用块而不是 using 声明：均衡等式要求断言时帧句柄已经释放。
        long? sequence;
        using (var captured = await rig.CaptureAsync())
            sequence = captured.Metadata.DeviceSequence;

        Assert.AreEqual(2L, sequence, "第二轮必须领到自己代次的帧。");
        Assert.AreEqual(2, rig.Device.StreamStartCount, "第二根根运行必须重新布防接收流。");
        Assert.AreEqual(1, rig.Provider.OpenedBindings.Count, "设备在两次布防之间保持打开，不得重新打开相机。");
        Assert.AreEqual(0, rig.Device.DisposeCount, "上一根运行持有的设备对象不得被静默丢弃。");
        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());
    }

    /// <summary>§14-12：根运行所有权冲突明确失败，并报告当前持有者身份。</summary>
    [TestMethod]
    public async Task SecondRootRun_ConflictsWithHolderIdentity()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync("run-1");

        var conflict = await Assert.ThrowsExactlyAsync<VisionResourceConflictException>(
            async () => await rig.Runtime.BeginRunAsync("run-2", CancellationToken.None));

        StringAssert.Contains(conflict.Message, "run-1", "冲突报告必须指出当前持有者。");
        StringAssert.Contains(conflict.Message, "run-2");
        Assert.AreEqual("run-1", conflict.Diagnostics.HolderOwnerId);
        Assert.AreEqual("run-2", conflict.Diagnostics.RequestOwnerId);
        Assert.AreEqual(EVisionSourceSharingPolicy.ExclusiveRun, conflict.Diagnostics.Policy);
    }

    /// <summary>§14-14：接收流异常结束后所有等待者立即失败，不等到超时；且会话进入故障态。</summary>
    [TestMethod]
    public async Task StreamCompletion_FailsWaitersImmediately()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync();

        var pending = rig.CaptureAsync(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 50 && !pending.IsCompleted; index++)
            await Task.Yield();

        Assert.IsTrue(rig.Device.CompleteStream(new InvalidOperationException("相机断线")));

        var failure = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () => await pending);
        StringAssert.Contains(failure.Message, "StreamFailure", "断线必须表现为故障态，而不是一句泛泛的'流已结束'。");
        StringAssert.Contains(failure.Message, "相机断线", "必须保留底层结束原因，否则现场无法判断是断线还是正常停止。");

        // 故障是终态：后续领取同样失败，而不是继续等到超时。
        var after = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () => await rig.CaptureAsync());
        StringAssert.Contains(after.Message, "相机断线");
    }

    /// <summary>§14-17：被拒绝、超龄与未领取的帧最终都只释放一次。</summary>
    [TestMethod]
    public async Task EveryFrame_IsDisposedExactlyOnce()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync();

        Assert.IsTrue(rig.Device.Emit(1, seed: 1));
        Assert.IsTrue(rig.Device.Emit(2, seed: 2));
        Assert.IsTrue(rig.Device.Emit(3, seed: 3));
        Assert.AreEqual(0, rig.Counter.Disposes, "领取之前不得释放任何帧。");

        using (var claimed = await rig.CaptureAsync())
        {
            Assert.AreEqual(1L, claimed.Metadata.DeviceSequence);
            Assert.AreEqual(1, rig.Counter.Retains, "ImageFrame 构造必须保留一份独立句柄。");
            Assert.AreEqual(1, rig.Counter.Disposes, "像素所有权转移后立即释放 Provider 句柄。");
        }

        // 已领取帧的独立句柄由调用方释放；其余两帧在退役时清退。
        Assert.AreEqual(2, rig.Counter.Disposes);

        await rig.DisposeAsync();
        Assert.AreEqual(3, rig.Counter.Created);
        Assert.AreEqual(1, rig.Counter.Retains);
        Assert.AreEqual(4, rig.Counter.Disposes);
        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());
    }

    /// <summary>§14-18：Provider 设备关闭后，已返回帧仍可读——像素所有权独立于设备与 SDK 句柄。</summary>
    [TestMethod]
    public async Task ReturnedFrame_RemainsReadableAfterDeviceDisposed()
    {
        // 本用例要读真实像素，因此不能用只为计数而存在的替身图像。
        await using var rig = new Rig(Policy(), seed => TestImages.Gray8(seed: seed));
        await rig.ArmAsync();

        Assert.IsTrue(rig.Device.Emit(deviceSequence: 71, seed: 9));
        using var captured = await rig.CaptureAsync();

        await rig.DisposeAsync();
        Assert.AreEqual(1, rig.Device.DisposeCount, "释放运行时必须释放设备。");
        Assert.AreEqual(1, rig.Device.StreamDisposeCount, "必须在释放设备之前显式停流，不能靠设备关闭兜底。");
        Assert.IsFalse(rig.Device.IsStreaming);
        CollectionAssert.AreEqual(
            new[] { "stream-start", "stream-stop", "device-dispose" },
            rig.Device.Events.ToArray(),
            "必须先停流再释放设备；顺序反过来会让回调与设备释放并发。");

        var info = captured.Frame.Image.Info;
        var pixels = new byte[info.ByteLength];
        captured.Frame.Image.CopyTo(0, pixels, 0, pixels.Length);

        Assert.AreEqual(info.ByteLength, pixels.Length);
        Assert.AreEqual(unchecked((byte)9), pixels[0]);
        Assert.AreEqual(unchecked((byte)(9 + pixels.Length - 1)), pixels[pixels.Length - 1]);
    }

    /// <summary>
    /// 兜底路径：宿主异常退出时可能来不及退役根运行租约，只释放运行时。
    /// <para>
    /// 主路径（<c>ReturnedFrame_RemainsReadableAfterDeviceDisposed</c>）先退役再释放，
    /// 停流由退役完成；本用例跳过退役直接释放运行时，锁住"释放运行时本身也必须停流"这道独立防线。
    /// 断言必须放在租约退役之前，否则两条路径都会让停流发生，区分不出是哪一条做到的。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task RuntimeDisposal_StopsStreamEvenWithoutLeaseRetirement()
    {
        var rig = new Rig(Policy());
        await rig.ArmAsync();
        Assert.IsTrue(rig.Device.Emit(1, seed: 1));
        Assert.AreEqual(1, rig.Diagnostics.InboxCount);

        // 故意不调用 rig.DisposeAsync（它会先退役租约），直接释放运行时。
        await rig.Runtime.DisposeAsync();

        Assert.AreEqual(1, rig.Device.StreamDisposeCount, "释放运行时必须主动停接收流，不能依赖设备关闭兜底。");
        Assert.IsFalse(rig.Device.IsStreaming, "释放运行时后设备不得仍在布防。");
        Assert.AreEqual(1, rig.Device.DisposeCount, "释放运行时必须释放设备。");
        CollectionAssert.AreEqual(
            new[] { "stream-start", "stream-stop", "device-dispose" },
            rig.Device.Events.ToArray(),
            "必须先停流再释放设备；顺序反过来会让回调与设备释放并发。");
        Assert.AreEqual(1, rig.Counter.Disposes, "释放运行时必须清退未领取帧。");
        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());

        await rig.DisposeAsync();
    }

    /// <summary>
    /// 运行结束（租约退役）就必须停接收流，而运行时仍存活——生产常态是运行时比单根运行活得久。
    /// <para>
    /// 这条独立锁住 <c>DisarmAsync</c> 的停流。如果停流只由"释放运行时"兜底，长驻宿主里
    /// 上一根运行结束后相机会一直布防、帧持续进入队列，直到进程退出才暴露。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task RunEnd_StopsStreamWhileRuntimeStaysAlive()
    {
        await using var rig = new Rig(Policy());
        await rig.ArmAsync("run-1");
        Assert.IsTrue(rig.Device.Emit(1, seed: 1));

        await rig.Lease.DisposeAsync();

        Assert.AreEqual(1, rig.Device.StreamDisposeCount, "运行结束必须停接收流，不能等运行时释放。");
        Assert.IsFalse(rig.Device.IsStreaming, "运行结束后设备不得仍在布防。");
        Assert.AreEqual(0, rig.Device.DisposeCount, "设备保持打开以便下次布防复用。");
        Assert.IsFalse(rig.Device.Emit(2, seed: 2), "停流后回调不得再被交付。");
        Assert.AreEqual(1, rig.Counter.Disposes, "运行结束必须清退未领取帧。");
        Assert.IsTrue(rig.Counter.IsBalanced, rig.Counter.ToString());
    }

    /// <summary>默认策略：容量与字节预算足够容纳本文件所有用例的帧数。</summary>
    private static VisionFrameInboxPolicy Policy() =>
        new VisionFrameInboxPolicy(capacity: 8, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1));

    /// <summary>把"成功"与"失败原因"折叠成一个结果，便于对两个并行领取做 WhenAny。</summary>
    private static async Task<Exception?> ProbeAsync(Task<VisionCapturedImage> task)
    {
        try
        {
            using var captured = await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// 单个外部回调缓冲源的最小装配：一个假 Provider、一台可控假流式设备、一个 ResourceSession。
    /// <para>Provider 与设备按需惰性创建，因此设备引用只在布防之后才可用。</para>
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly VisionAcquisitionRuntime _runtime;
        private readonly FakeVisionProvider _provider;
        private FakeStreamingVisionDevice? _device;
        private IVisionAcquisitionRunLease? _lease;
        private bool _disposed;

        public Rig(VisionFrameInboxPolicy policy, Func<byte, IImageSource>? imageFactory = null)
        {
            var provider = new FakeVisionProvider(ProviderId, binding =>
            {
                _device = new FakeStreamingVisionDevice(
                    new VisionDeviceIdentity(ProviderId, binding),
                    imageFactory ?? (seed => new TrackingImageSource(Counter)));
                return _device;
            });
            _provider = provider;

            _runtime = new VisionAcquisitionRuntime(new VisionAcquisitionProviderComposer().Compose(
                new[]
                {
                    new FakeVisionProviderModule(
                        "m.stream",
                        new VisionAcquisitionProviderRegistration(ProviderId, "1.0.0", () => provider))
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
                        policy)
                }));

            // 设备连接属于软件生命周期：Runtime在构造后立即启动并打开设备；根运行与采集都只复用会话设备。
            _runtime.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        /// <summary>图像句柄的创建/保留/释放计数。</summary>
        public DisposalCounter Counter { get; } = new DisposalCounter();

        /// <summary>被测运行时。</summary>
        public VisionAcquisitionRuntime Runtime => _runtime;

        /// <summary>假Provider；用来观察相机到底被打开了几次。</summary>
        public FakeVisionProvider Provider => _provider;

        /// <summary>已打开的假流式设备；未布防时访问会明确失败而不是返回空。</summary>
        public FakeStreamingVisionDevice Device =>
            _device ?? throw new InvalidOperationException("设备尚未打开：请先 ArmAsync。");

        /// <summary>当前根运行租约。</summary>
        public IVisionAcquisitionRunLease Lease =>
            _lease ?? throw new InvalidOperationException("尚未开始根运行。");

        /// <summary>唯一逻辑源的诊断快照。</summary>
        public VisionSourceDiagnostics Diagnostics =>
            _runtime.GetDiagnostics(SourceId) ?? throw new InvalidOperationException($"源 {SourceId} 未发布。");

        /// <summary>开始一根根运行：推进代次并布防外部回调缓冲源。</summary>
        /// <param name="runId">根运行身份。</param>
        public async Task ArmAsync(string runId = "run-1")
        {
            _lease = await _runtime.BeginRunAsync(runId, CancellationToken.None);
        }

        /// <summary>按缓冲模式领取一帧。</summary>
        /// <param name="timeout">等待超时；为空时取 5 秒。</param>
        /// <param name="cancellationToken">协作取消。</param>
        public Task<VisionCapturedImage> CaptureAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            _runtime.CaptureAsync(
                new VisionSourceReference(SourceId),
                new VisionCaptureRequest(timeout ?? TimeSpan.FromSeconds(5)),
                new VisionAcquisitionOwner("run-1", "node-1"),
                cancellationToken).AsTask();

        /// <summary>先退役本轮，再释放运行时；重复调用无副作用。</summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_lease is not null)
                await _lease.DisposeAsync();
            await _runtime.DisposeAsync();
        }
    }
}
