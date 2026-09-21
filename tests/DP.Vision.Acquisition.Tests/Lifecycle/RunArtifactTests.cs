using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// V2-9 第 3 条：组合与机器配置修订进入运行制品。
/// <para>
/// 制品的用途是事后回答"这根运行到底用了哪一版配置、经手了哪些帧"：
/// 因此它必须同时带上采集组合身份、机器配置修订号、插件清单与相机配置摘要（已脱敏）。
/// </para>
/// <para>
/// 制品只记录事实，不参与控制流：租约不产出制品时采集行为完全不变。
/// </para>
/// </summary>
[TestClass]
public sealed class RunArtifactTests
{
    private const string AreaTypeId = "dp.acquisition.test.area";
    private const string SourceId = "Camera.Top";

    /// <summary>制品记录来源身份、修订号、插件清单与脱敏后的配置摘要，以及本轮领取帧的身份。</summary>
    [TestMethod]
    public async Task Artifact_RecordsCompositionRevisionAndClaimedFrames()
    {
        var fixture = new ArtifactFixture();
        await using var runtime = new VisionAcquisitionRuntime(fixture.Composition, machineConfigurationRevision: 7);
        await runtime.StartAsync(CancellationToken.None);
        var lease = await runtime.BeginRunAsync("run-1", "wf-1", CancellationToken.None);

        Assert.IsTrue(lease is IVisionAcquisitionRunArtifactSource, "运行时租约必须能产出运行制品。");
        var source = (IVisionAcquisitionRunArtifactSource)lease;

        Assert.IsTrue(fixture.Device.Emit(deviceSequence: 5, seed: 3));
        using (await CaptureAsync(runtime))
        {
        }

        Assert.IsTrue(source.TryGetArtifact(out var artifact));
        Assert.IsNotNull(artifact);
        Assert.AreEqual("run-1", artifact!.RunId);
        Assert.AreEqual("wf-1", artifact.WorkflowCompositionId);
        Assert.AreEqual(runtime.CompositionId, artifact.AcquisitionCompositionId);
        Assert.AreEqual(7, artifact.MachineConfigurationRevision, "制品必须回答跑的是哪一版机器配置。");
        Assert.AreEqual(1, artifact.Epoch);
        CollectionAssert.AreEqual(new[] { AreaTypeId + "@1.0.0" }, artifact.PluginManifest.ToArray());

        var entry = artifact.Sources.Single();
        Assert.AreEqual(SourceId, entry.SourceId);
        Assert.AreEqual(AreaTypeId, entry.AcquisitionTypeId);
        Assert.AreEqual(AreaTypeId, entry.ProviderId);
        Assert.AreEqual("1.0.0", entry.PluginVersion);
        Assert.AreEqual("camera:serial:SN-1", entry.ResourceKey);
        Assert.AreEqual("serialNumber=***", entry.CameraConfigurationSummary, "序列号必须脱敏后才能进入归档制品。");
        Assert.AreEqual(EVisionConnectionState.Connected, entry.ConnectionState);
        Assert.AreEqual("Streaming", entry.TransferState);
        Assert.AreEqual(1L, entry.ClaimedFrames);
        Assert.AreEqual(1, entry.InboxHighWatermark);
        Assert.IsNull(entry.LastFailureKind);

        var frame = entry.ClaimedFrameDetails.Single();
        Assert.IsFalse(string.IsNullOrWhiteSpace(frame.CaptureId));
        Assert.AreEqual(1L, frame.ReceivedSequence);
        Assert.AreEqual(5L, frame.DeviceSequence);
        Assert.AreEqual(0L, artifact.UnclaimedAtEpochEnd, "尚未退役时没有收口计数。");
    }

    /// <summary>退役后重新读取制品，收口未领取帧计数与源级计数都必须出现。</summary>
    [TestMethod]
    public async Task Artifact_AfterRetirement_RecordsUnclaimedFrames()
    {
        var fixture = new ArtifactFixture();
        await using var runtime = new VisionAcquisitionRuntime(fixture.Composition, machineConfigurationRevision: 3);
        await runtime.StartAsync(CancellationToken.None);
        var lease = await runtime.BeginRunAsync("run-1", "wf-1", CancellationToken.None);
        var source = (IVisionAcquisitionRunArtifactSource)lease;

        Assert.IsTrue(fixture.Device.Emit(deviceSequence: 1, seed: 1));
        Assert.IsTrue(fixture.Device.Emit(deviceSequence: 2, seed: 2));
        Assert.IsTrue(fixture.Device.Emit(deviceSequence: 3, seed: 3));
        using (await CaptureAsync(runtime))
        {
        }

        await lease.DisposeAsync();

        Assert.IsTrue(source.TryGetArtifact(out var artifact));
        var entry = artifact!.Sources.Single();
        Assert.AreEqual(2L, artifact.UnclaimedAtEpochEnd, "本轮收口释放两帧。");
        Assert.AreEqual(2L, entry.UnclaimedAtEpochEnd);
        Assert.AreEqual(1L, entry.ClaimedFrames);
        Assert.AreEqual(3L, entry.FramesReceived);
        Assert.IsTrue(artifact.CompletedAtUtc >= artifact.StartedAtUtc);
    }

    /// <summary>宿主不提供工作流组合身份或修订号时制品仍然可用，只把这些字段留空/置零。</summary>
    [TestMethod]
    public async Task Artifact_WithoutHostIdentity_LeavesOptionalFieldsEmpty()
    {
        var fixture = new ArtifactFixture();
        await using var runtime = new VisionAcquisitionRuntime(fixture.Composition);
        await runtime.StartAsync(CancellationToken.None);

        // 走 IVisionAcquisitionRunOwner 契约：工作流侧只依赖租约，不提供制品身份。
        var lease = await ((IVisionAcquisitionRunOwner)runtime).BeginRunAsync("run-1", CancellationToken.None);

        Assert.IsTrue(((IVisionAcquisitionRunArtifactSource)lease).TryGetArtifact(out var artifact));
        Assert.IsNull(artifact!.WorkflowCompositionId);
        Assert.AreEqual(0, artifact.MachineConfigurationRevision);
        Assert.AreEqual(runtime.CompositionId, artifact.AcquisitionCompositionId, "采集组合身份永远可用。");

        await lease.DisposeAsync();
    }

    private static Task<VisionCapturedImage> CaptureAsync(VisionAcquisitionRuntime runtime) =>
        runtime.CaptureAsync(
            new VisionSourceReference(SourceId),
            new VisionCaptureRequest(TimeSpan.FromSeconds(5)),
            new VisionAcquisitionOwner("run-1", "node-1"),
            CancellationToken.None).AsTask();

    /// <summary>
    /// 制品装配：机器配置路径产生的OnConnect缓冲源，因此既有真实配置摘要（可验证脱敏），
    /// 又有External回调源（可验证领取记账）。
    /// </summary>
    private sealed class ArtifactFixture
    {
        private FakeStreamingVisionDevice? _device;

        public ArtifactFixture()
        {
            var module = new FakeAcquisitionDriverModule(
                "module.artifact",
                new VisionAcquisitionTypeRegistration(
                    AreaTypeId,
                    "dp.vision.test",
                    "1.0.0",
                    EVisionAcquisitionKind.AreaScan,
                    1,
                    "制品测试面阵",
                    new VisionAcquisitionTypeCapabilities(
                        SupportsFreeRun: true,
                        SupportsCompleteFrameCallback: true),
                    () => new FakeVisionProvider(AreaTypeId, binding =>
                    {
                        _device = new FakeStreamingVisionDevice(new VisionDeviceIdentity(AreaTypeId, binding));
                        return _device;
                    }),
                    TestDeviceSettingsParser.Parse));

            var catalog = new VisionAcquisitionTypeCatalogComposer()
                .Compose(new IVisionAcquisitionDriverModule[] { module });
            var cameras = VisionAcquisitionMachineConfigurationParser.Parse(
                "{\"sourceId\":\"" + SourceId + "\",\"acquisitionType\":\"" + AreaTypeId + "\",\"settingsVersion\":1,"
                + "\"connection\":{\"transferStart\":\"OnConnect\"},"
                + "\"inbox\":{\"capacity\":8,\"byteBudget\":4096,\"maximumFrameAgeMilliseconds\":60000},"
                + "\"deviceSettings\":{\"serialNumber\":\"SN-1\"}}");
            Composition = new VisionAcquisitionMachineConfigurationComposer().Compose(catalog, cameras);
        }

        public VisionAcquisitionProviderComposition Composition { get; }

        /// <summary>已打开的假流式设备；未打开时访问会明确失败。</summary>
        public FakeStreamingVisionDevice Device =>
            _device ?? throw new InvalidOperationException("设备尚未打开：请先启动运行时。");
    }
}