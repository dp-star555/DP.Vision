using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// V2-9 第 5 条：中立像素落地（复制/转换）的耗时与字节观测。
/// <para>
/// 观测值必须进入诊断累计：后续给中立像素做 HObject/Mat 表示缓存时，
/// "缓存到底省了多少毫秒、多少字节"只能靠这条基线回答，事后补埋点拿不到历史数据。
/// </para>
/// <para>
/// 关键语义：只要像素已经落地就计入，与这一帧随后是被接收还是被拒绝无关——
/// 拒绝的是队列，不是已经发生的复制。
/// </para>
/// </summary>
[TestClass]
public sealed class PixelTransferObservationTests
{
    private const string ProviderId = "dp.fake.stream";
    private const string SourceId = "Camera.Stream";
    private const string OnDemandSourceId = "Camera.Top";

    /// <summary>缓冲源：两帧的耗时与字节累计，且最近一次单独保留。</summary>
    [TestMethod]
    public async Task BufferedFrames_AccumulateTransferObservations()
    {
        FakeStreamingVisionDevice? device = null;
        await using var runtime = new VisionAcquisitionRuntime(BufferedComposition(binding =>
        {
            device = new FakeStreamingVisionDevice(new VisionDeviceIdentity(ProviderId, binding));
            return device;
        }));
        await runtime.StartAsync(CancellationToken.None);
        var lease = await runtime.BeginRunAsync("run-1", CancellationToken.None);

        Assert.IsTrue(device!.Emit(1, seed: 1, observation: Observation(3, 2048)));
        Assert.IsTrue(device.Emit(2, seed: 2, observation: Observation(1, 1024)));

        var diagnostics = runtime.GetDiagnostics(SourceId)!;
        Assert.IsNotNull(diagnostics.Transfer, "Provider 上报了观测就必须出现在诊断里。");
        Assert.AreEqual(2L, diagnostics.Transfer!.Count);
        Assert.AreEqual(3072L, diagnostics.Transfer.BytesTotal);
        Assert.AreEqual(4d, diagnostics.Transfer.DurationTotalMilliseconds);
        Assert.AreEqual(1024L, diagnostics.Transfer.LastBytes, "最近一次单独保留，便于判断当前单帧成本。");
        Assert.AreEqual(1d, diagnostics.Transfer.LastDurationMilliseconds);
        Assert.AreEqual("Streaming", diagnostics.TransferState);
        Assert.AreEqual(1, diagnostics.ConnectionRevision, "设备复用时不重复打开，连接修订保持为 1。");

        // 观测是累计事实，不因领取而清零。
        using (await runtime.CaptureAsync(
            new VisionSourceReference(SourceId),
            new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
            new VisionAcquisitionOwner("run-1", "node-1"),
            CancellationToken.None))
        {
        }

        await lease.DisposeAsync();
        Assert.AreEqual(3072L, runtime.GetDiagnostics(SourceId)!.Transfer!.BytesTotal);
    }

    /// <summary>Provider 未观测时诊断不得出现伪造的零值累计（空表示"没观测"，与"观测到 0 字节"不同）。</summary>
    [TestMethod]
    public async Task FramesWithoutObservation_LeaveTransferEmpty()
    {
        await using var runtime = new VisionAcquisitionRuntime(BufferedComposition(binding =>
            new FakeStreamingVisionDevice(new VisionDeviceIdentity(ProviderId, binding))));
        await runtime.StartAsync(CancellationToken.None);

        var diagnostics = runtime.GetDiagnostics(SourceId)!;
        Assert.IsNull(diagnostics.Transfer);
    }

    /// <summary>无活动代次被拒绝的帧仍然计入观测：像素复制已经发生，队列拒绝不能抹掉这份成本。</summary>
    [TestMethod]
    public async Task RejectedFrame_StillCountsTransferBytes()
    {
        FakeStreamingVisionDevice? device = null;
        await using var runtime = new VisionAcquisitionRuntime(BufferedComposition(binding =>
        {
            device = new FakeStreamingVisionDevice(new VisionDeviceIdentity(ProviderId, binding));
            return device;
        }));
        await runtime.StartAsync(CancellationToken.None);

        Assert.IsTrue(device!.Emit(1, seed: 1, observation: Observation(2, 4096)));

        var diagnostics = runtime.GetDiagnostics(SourceId)!;
        Assert.AreEqual(1L, diagnostics.FramesRejectedWithoutEpoch);
        Assert.AreEqual(1L, diagnostics.Transfer!.Count, "被拒绝的帧也已经落地过像素。");
        Assert.AreEqual(4096L, diagnostics.Transfer.BytesTotal);
    }

    /// <summary>主动单次采集没有回调边界，观测由 Runtime 在设备返回后交给会话，与缓冲源共用同一份累计。</summary>
    [TestMethod]
    public async Task OnDemandCapture_RecordsTransferObservation()
    {
        var provider = new FakeVisionProvider(ProviderId, binding => new FakeVisionDevice(
            new VisionDeviceIdentity(ProviderId, binding),
            (request, cancellationToken) => new ValueTask<VisionProviderFrame>(
                new VisionProviderFrame(
                    TestImages.Gray8(seed: 5), DateTimeOffset.UtcNow, deviceSequence: 7, transferObservation: Observation(6, 512)))));

        await using var runtime = new VisionAcquisitionRuntime(new VisionAcquisitionProviderComposer().Compose(
            new[]
            {
                new FakeVisionProviderModule(
                    "m.ondemand",
                    new VisionAcquisitionProviderRegistration(ProviderId, "1.0.0", () => provider))
            },
            new[] { new VisionAcquisitionSourceBinding(OnDemandSourceId, ProviderId, "top-camera", "camera:serial:TOP") }));
        await runtime.StartAsync(CancellationToken.None);

        using (var captured = await runtime.CaptureAsync(
            new VisionSourceReference(OnDemandSourceId),
            new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
            new VisionAcquisitionOwner("run-1", "node-1"),
            CancellationToken.None))
        {
            Assert.AreEqual(7L, captured.Metadata.DeviceSequence);
        }

        var diagnostics = runtime.GetDiagnostics(OnDemandSourceId)!;
        Assert.AreEqual(1L, diagnostics.Transfer!.Count);
        Assert.AreEqual(512L, diagnostics.Transfer.BytesTotal);
        Assert.AreEqual(6d, diagnostics.Transfer.DurationTotalMilliseconds);
        Assert.AreEqual("NotStarted", diagnostics.TransferState, "主动采集源没有持续取流，取流状态必须与连接状态分开报告。");
    }

    private static VisionPixelTransferObservation Observation(int milliseconds, long bytes) =>
        new VisionPixelTransferObservation(TimeSpan.FromMilliseconds(milliseconds), bytes);

    private static VisionAcquisitionProviderComposition BufferedComposition(
        Func<string, IVisionAcquisitionDevice> deviceFactory) =>
        new VisionAcquisitionProviderComposer().Compose(
            new[]
            {
                new FakeVisionProviderModule(
                    "m.stream",
                    new VisionAcquisitionProviderRegistration(
                        ProviderId, "1.0.0", () => new FakeVisionProvider(ProviderId, deviceFactory)))
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