using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision;
using DP.Vision.Acquisition;
using DP.Vision.UI.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// V2-9b 采集管理表现层：发现合并与失败隔离、配置修订快照、监视行搬运与试拍结果化。
/// <para>
/// 表现层只做编排与投影，因此测试全部走"组合 + 运行时 + 修订存储"的真实契约，不伪造快照：
/// 一旦运行时或修订存储的行为变了，这里的断言会先失败。
/// </para>
/// </summary>
[TestClass]
public sealed class AcquisitionManagementPresenterTests
{
    private const string AreaTypeId = ConfigurableAcquisitionDriverModule.AreaScanTypeId;
    private const string SourceId = "Camera.Top";

    /// <summary>
    /// 各Provider的候选合并成顺序确定的列表：按ProviderId分组、组内按规范资源键升序，
    /// 与Provider自己返回候选的顺序无关；是否已被当前机器配置引用由规范资源键比对得出。
    /// </summary>
    [TestMethod]
    public async Task DiscoverAsync_MergesCandidatesInDeterministicOrderAndMarksConfiguration()
    {
        FakeVisionProvider? plainProvider = null;
        var composition = new VisionAcquisitionProviderComposer().Compose(
            new[]
            {
                Module("module.alpha", DiscoveryRegistration(
                    "dp.fake.alpha",
                    Descriptor("dp.fake.alpha", "cam-b", "camera:serial:B", "乙相机"),
                    Descriptor("dp.fake.alpha", "cam-a", "camera:serial:A", "甲相机"))),
                Module("module.beta", DiscoveryRegistration(
                    "dp.fake.beta",
                    Descriptor("dp.fake.beta", "cam-c", "camera:serial:C", "丙相机"))),
                Module("module.gamma", new VisionAcquisitionProviderRegistration(
                    "dp.fake.gamma",
                    "1.0.0",
                    () =>
                    {
                        plainProvider = FakeVisionProvider.WithDevices("dp.fake.gamma");
                        return plainProvider;
                    }))
            },
            new[] { new VisionAcquisitionSourceBinding(SourceId, "dp.fake.alpha", "cam-a", "camera:serial:A") });

        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(runtime, composition, NewStore());

        var snapshot = await presenter.DiscoverAsync(CancellationToken.None);

        Assert.AreEqual(0, snapshot.ProviderFailures.Count, "不实现发现能力的Provider只是不贡献候选，不算失败。");
        CollectionAssert.AreEqual(
            new[] { "camera:serial:A", "camera:serial:B", "camera:serial:C" },
            snapshot.Devices.Select(item => item.CanonicalKey!).ToArray(),
            "候选必须按ProviderId后按规范资源键排序，现场枚举顺序不得影响界面顺序。");
        Assert.AreEqual("甲相机", snapshot.Devices[0].DisplayName);
        Assert.AreEqual("cam-a", snapshot.Devices[0].ProviderBindingId);
        Assert.IsTrue(snapshot.Devices[0].IsConfigured, "资源键与已发布Source一致时判定为已被引用。");
        Assert.IsFalse(snapshot.Devices[0].IsUnconfigured);
        Assert.IsFalse(snapshot.Devices[1].IsConfigured);
        Assert.IsTrue(snapshot.Devices[1].IsUnconfigured);
        Assert.IsNotNull(plainProvider);
        Assert.AreEqual(1, plainProvider!.DisposeCount, "发现用的Provider实例必须由表现层释放，不能泄漏。");
    }

    /// <summary>
    /// 单个Provider发现失败（缺SDK或插件工厂损坏）只记为该Provider的一条诊断：
    /// 整体不失败，另一个Provider的相机仍然可见；失败也不能被伪装成"现场没有设备"。
    /// </summary>
    [TestMethod]
    public async Task DiscoverAsync_IsolatesProviderFailuresAsDiagnostics()
    {
        var composition = new VisionAcquisitionProviderComposer().Compose(
            new[]
            {
                Module("module.alpha", DiscoveryRegistration(
                    "dp.fake.alpha",
                    Descriptor("dp.fake.alpha", "cam-a", "camera:serial:A", "甲相机"))),
                Module("module.beta", new VisionAcquisitionProviderRegistration(
                    "dp.fake.beta",
                    "1.0.0",
                    () =>
                    {
                        var provider = FakeVisionProvider.WithDevices("dp.fake.beta");
                        provider.DiscoveryFailure = new VisionProviderUnavailableException("dp.fake.beta", "未装配测试SDK。");
                        return provider;
                    })),
                Module("module.gamma", new VisionAcquisitionProviderRegistration(
                    "dp.fake.gamma",
                    "1.0.0",
                    () => throw new InvalidOperationException("插件工厂损坏。")))
            },
            new[] { new VisionAcquisitionSourceBinding(SourceId, "dp.fake.alpha", "cam-a", "camera:serial:A") });

        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(runtime, composition, NewStore());

        var snapshot = await presenter.DiscoverAsync(CancellationToken.None);

        Assert.AreEqual(1, snapshot.Devices.Count, "一个Provider不可用不能让另一个Provider的相机也看不见。");
        Assert.AreEqual(2, snapshot.ProviderFailures.Count);
        Assert.AreEqual("dp.fake.beta", snapshot.ProviderFailures[0].ProviderId);
        StringAssert.Contains(snapshot.ProviderFailures[0].Message, "未装配测试SDK");
        Assert.AreEqual("dp.fake.gamma", snapshot.ProviderFailures[1].ProviderId);
        StringAssert.Contains(snapshot.ProviderFailures[1].Message, "插件工厂损坏");
    }

    /// <summary>候选校验失败时错误文本进入配置快照，且不产生任何修订。</summary>
    [TestMethod]
    public void InvalidCandidate_ExposesErrorsInConfigurationSnapshot()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-1");
        var presenter = new AcquisitionManagementPresenter(
            new VisionAcquisitionRuntime(composition),
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog));

        presenter.SetCandidate(ConfigurationWithoutSerial());

        var validation = presenter.ValidateCandidate();
        Assert.IsFalse(validation.IsValid);
        Assert.IsNull(validation.CompositionId);

        var snapshot = presenter.CaptureConfiguration();
        Assert.IsTrue(snapshot.HasCandidate, "候选仍在，界面可以继续修配置。");
        Assert.AreEqual(1, snapshot.CandidateErrors.Count);
        StringAssert.Contains(snapshot.CandidateErrors[0], "serialNumber");
        Assert.IsNull(snapshot.CurrentRevision);
        Assert.AreEqual(0, snapshot.History.Count);
        Assert.IsNull(snapshot.LastFailureMessage);
    }

    /// <summary>发布成功后修订号、组合身份与已发布源行进入配置快照。</summary>
    [TestMethod]
    public void Publish_ExposesRevisionCompositionAndSourceRows()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-7");
        var presenter = new AcquisitionManagementPresenter(
            new VisionAcquisitionRuntime(composition),
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog));

        presenter.SetCandidate(Configuration("SN-7"));
        var validation = presenter.ValidateCandidate();
        Assert.IsTrue(validation.IsValid, string.Join("；", validation.Errors));

        var revision = presenter.Publish();
        var snapshot = presenter.CaptureConfiguration();

        Assert.AreEqual(1, revision.Revision);
        Assert.AreEqual(revision.Revision, snapshot.CurrentRevision);
        Assert.AreEqual(revision.CompositionId, snapshot.CompositionId);
        Assert.IsFalse(snapshot.HasCandidate, "发布后候选必须清空。");
        Assert.AreEqual(0, snapshot.CandidateErrors.Count);
        Assert.IsNull(snapshot.LastFailureMessage);

        Assert.AreEqual(1, snapshot.Sources.Count);
        var row = snapshot.Sources[0];
        Assert.AreEqual(SourceId, row.SourceId);
        Assert.AreEqual(AreaTypeId, row.AcquisitionTypeId);
        Assert.AreEqual("camera:serial:SN-7", row.ResourceKey);
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, row.Kind);
        Assert.AreEqual(EVisionAcquisitionMode.OnDemand, row.AcquisitionMode);
        Assert.AreEqual(EVisionSourceSharingPolicy.ExclusiveOperation, row.SharingPolicy);
        Assert.IsTrue(row.IsAvailable);
        Assert.IsNull(row.Diagnostic);
        Assert.AreEqual("serialNumber=SN-7", row.ConfigurationSummary, "私有配置摘要用于人工比对两次发布之间的差异。");

        Assert.AreEqual(1, snapshot.History.Count);
        Assert.AreEqual(1, snapshot.History[0].Revision);
        Assert.AreEqual(revision.CompositionId, snapshot.History[0].CompositionId);
        Assert.IsFalse(snapshot.History[0].IsRollback);
        Assert.IsNull(snapshot.History[0].RestoredFromRevision);
    }

    /// <summary>发布失败：异常带全部错误文本，同时快照里留下同一份文本，历史保持不变。</summary>
    [TestMethod]
    public void PublishFailure_CarriesAllErrorsAndLeavesHistoryUntouched()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-1");
        var presenter = new AcquisitionManagementPresenter(
            new VisionAcquisitionRuntime(composition),
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog));

        presenter.SetCandidate(ConfigurationWithoutSerial());

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => presenter.Publish());
        StringAssert.Contains(failure.Message, "serialNumber");

        var snapshot = presenter.CaptureConfiguration();
        Assert.IsNotNull(snapshot.LastFailureMessage);
        StringAssert.Contains(snapshot.LastFailureMessage!, "serialNumber");
        Assert.AreEqual(1, snapshot.CandidateErrors.Count);
        Assert.AreEqual(0, snapshot.History.Count, "校验不通过时不得产生任何修订。");
        Assert.IsNull(snapshot.CurrentRevision);
    }

    /// <summary>回滚追加一条新修订并进入历史：历史只追加不移动指针，中间修订必须保留。</summary>
    [TestMethod]
    public void Rollback_AppendsRevisionVisibleInHistory()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-1");
        var presenter = new AcquisitionManagementPresenter(
            new VisionAcquisitionRuntime(composition),
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog));

        presenter.SetCandidate(Configuration("SN-1"));
        presenter.Publish();
        presenter.SetCandidate(Configuration("SN-2"));
        presenter.Publish();

        var rolledBack = presenter.Rollback(1);
        var snapshot = presenter.CaptureConfiguration();

        Assert.AreEqual(3, rolledBack.Revision, "回滚产生新修订，而不是回到修订 1。");
        Assert.AreEqual(3, snapshot.CurrentRevision);
        Assert.AreEqual(3, snapshot.History.Count);
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3 },
            snapshot.History.Select(item => item.Revision).ToArray());

        var last = snapshot.History[2];
        Assert.IsTrue(last.IsRollback);
        Assert.AreEqual(1, last.RestoredFromRevision);
        Assert.AreEqual(snapshot.History[0].CompositionId, last.CompositionId, "回滚后的组合身份等于目标修订。");
        Assert.IsFalse(snapshot.History[1].IsRollback, "中间修订不得被删除或改写。");
    }

    /// <summary>
    /// 运行时尚未启动时也能采样监视快照：逐字段搬运诊断，且从未上报像素落地的行必须显式标出"没有观测"。
    /// </summary>
    [TestMethod]
    public void CaptureMonitor_BeforeStart_ReportsCreatedRowsAndEmptyTransfer()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-1");
        var presenter = new AcquisitionManagementPresenter(
            new VisionAcquisitionRuntime(composition),
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog, Configuration("SN-1")));

        var snapshot = presenter.CaptureMonitor();

        Assert.AreEqual(EVisionRuntimeState.Created, snapshot.RuntimeState);
        Assert.IsFalse(snapshot.IsReady);
        Assert.IsFalse(snapshot.IsDegraded);
        Assert.AreEqual(composition.CompositionId, snapshot.CompositionId);
        Assert.AreEqual(1, snapshot.MachineConfigurationRevision, "修订号来自修订存储的当前修订。");
        Assert.AreEqual(1, snapshot.Rows.Count);

        var row = snapshot.Rows[0];
        Assert.AreEqual(SourceId, row.SourceId);
        Assert.AreEqual("camera:serial:SN-1", row.ResourceKey);
        Assert.AreEqual(AreaTypeId, row.ProviderId);
        Assert.AreEqual(AreaTypeId, row.AcquisitionTypeId);
        Assert.AreEqual(AreaTypeId, row.PluginId);
        Assert.AreEqual(ConfigurableAcquisitionDriverModule.TypeVersion, row.PluginVersion);
        Assert.AreEqual("Created", row.State);
        Assert.AreEqual(EVisionConnectionState.Created, row.ConnectionState);
        Assert.IsNull(row.ConnectionMessage);
        Assert.AreEqual("NotStarted", row.TransferState);
        Assert.IsNull(row.Transfer);
        Assert.IsFalse(row.HasTransferObservation, "未上报观测时界面要显示'未上报'，而不是一排零。");
        Assert.IsFalse(row.IsFaulted);
        Assert.IsNull(row.FaultKind);
        Assert.IsNull(row.FaultMessage);
        Assert.AreEqual(0, row.Epoch);
        Assert.AreEqual(0L, row.FramesReceived);
        Assert.AreEqual(0L, row.FramesClaimed);
        Assert.AreEqual(0L, row.FramesExpired);
        Assert.AreEqual(0L, row.FramesRejected);
        Assert.AreEqual(0L, row.FramesRejectedWithoutEpoch);
        Assert.AreEqual(0L, row.FramesRejectedOverflow);
        Assert.AreEqual(0L, row.UnclaimedAtEpochEnd);
        Assert.AreEqual(0, row.InboxCount);
        Assert.AreEqual(0L, row.InboxBytes);
        Assert.AreEqual(0, row.InboxHighWatermark);
        Assert.AreEqual(0L, row.BytesHighWatermark);
        Assert.AreEqual(0L, row.DeviceSequenceGaps);
        Assert.AreEqual(0L, row.CallbackFaults);
        Assert.AreEqual(0, row.ConnectionRevision);
    }

    /// <summary>缓冲源的监视行搬运像素落地观测与取流状态：连接状态与取流状态分开可见。</summary>
    [TestMethod]
    public async Task CaptureMonitor_MapsTransferObservationAndStreamingState()
    {
        const string providerId = "dp.fake.stream";
        FakeStreamingVisionDevice? device = null;
        var composition = new VisionAcquisitionProviderComposer().Compose(
            new[]
            {
                Module("module.stream", new VisionAcquisitionProviderRegistration(
                    providerId,
                    "1.0.0",
                    () => new FakeVisionProvider(providerId, binding =>
                    {
                        device = new FakeStreamingVisionDevice(new VisionDeviceIdentity(providerId, binding));
                        return device;
                    })))
            },
            new[]
            {
                new VisionAcquisitionSourceBinding(
                    "Camera.Line",
                    providerId,
                    "stream-camera",
                    "camera:serial:STREAM",
                    EVisionSourceSharingPolicy.ExclusiveRun,
                    EVisionAcquisitionMode.BufferedExternal,
                    new VisionFrameInboxPolicy(capacity: 8, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1)))
            });

        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(runtime, composition, NewStore());
        await runtime.StartAsync(CancellationToken.None);

        Assert.IsTrue(device!.Emit(
            deviceSequence: 1,
            observation: new VisionPixelTransferObservation(TimeSpan.FromMilliseconds(2), 480)));

        var snapshot = presenter.CaptureMonitor();
        Assert.AreEqual(EVisionRuntimeState.Ready, snapshot.RuntimeState);
        Assert.IsTrue(snapshot.IsReady);

        var row = snapshot.Rows[0];
        Assert.AreEqual("Camera.Line", row.SourceId);
        Assert.AreEqual("Streaming", row.State);
        Assert.AreEqual("Streaming", row.TransferState);
        Assert.AreEqual(EVisionConnectionState.Connected, row.ConnectionState);
        Assert.AreEqual(1, row.ConnectionRevision);
        Assert.IsTrue(row.HasTransferObservation);
        Assert.IsNotNull(row.Transfer);
        Assert.AreEqual(1L, row.Transfer!.Count);
        Assert.AreEqual(480L, row.Transfer.BytesTotal);
        Assert.AreEqual(480L, row.Transfer.LastBytes);
        Assert.IsTrue(row.Transfer.DurationTotalMilliseconds > 0);
        Assert.AreEqual(1L, row.FramesReceived, "接收计数含被拒绝的帧：像素已经落地过，面板要如实显示成本。");
        Assert.AreEqual(0L, row.FramesClaimed, "被拒绝的帧从未进入队列，因此领取数为零。");
        Assert.AreEqual(1L, row.FramesRejectedWithoutEpoch, "它同时解释'帧来了但没有根运行'。");
        Assert.AreEqual(0L, row.FramesRejectedOverflow);
        Assert.AreEqual(0, row.InboxCount, "被拒绝的帧没有留在队列里。");
    }

    /// <summary>运行时停止后仍能采样监视快照：整体状态为 Stopped，行来自组合而不是残留会话。</summary>
    [TestMethod]
    public async Task CaptureMonitor_AfterStop_ReportsStoppedStateWithRows()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-1");
        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(
            runtime,
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog, Configuration("SN-1")));

        await runtime.StartAsync(CancellationToken.None);
        await runtime.StopAsync();

        var snapshot = presenter.CaptureMonitor();

        Assert.AreEqual(EVisionRuntimeState.Stopped, snapshot.RuntimeState);
        Assert.IsFalse(snapshot.IsReady);
        Assert.IsFalse(snapshot.IsDegraded);
        Assert.AreEqual(1, snapshot.Rows.Count);
        Assert.AreEqual(SourceId, snapshot.Rows[0].SourceId);
        Assert.AreEqual("Created", snapshot.Rows[0].State);
        Assert.AreEqual(EVisionConnectionState.Created, snapshot.Rows[0].ConnectionState);
    }

    /// <summary>试拍成功：顺带启动运行时，返回采集身份与图像元数据，不抛异常。</summary>
    [TestMethod]
    public async Task TrialCapture_SucceedsAndStartsRuntime()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-3");
        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(
            runtime,
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog, Configuration("SN-3")));

        var result = await presenter.TrialCaptureAsync(SourceId, cancellationToken: CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.FailureKind);
        Assert.IsNull(result.FailureMessage);
        Assert.AreEqual(SourceId, result.SourceId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.CaptureId));
        Assert.AreEqual(AreaTypeId, result.ProviderId);
        Assert.AreEqual("camera:serial:SN-3", result.ResourceKey);
        Assert.IsNotNull(result.CapturedAtUtc);
        Assert.AreEqual(4, result.ImageWidth);
        Assert.AreEqual(3, result.ImageHeight);
        Assert.AreEqual(EPixelLayout.Gray8, result.PixelLayout);
        Assert.AreEqual(12, result.ImageByteLength);
        Assert.IsTrue(result.Duration >= TimeSpan.Zero);
        Assert.AreEqual(EVisionRuntimeState.Ready, runtime.RuntimeState, "试拍必须确保运行时已启动，并保持其运行。");
    }

    /// <summary>试拍目标是未发布的逻辑源：返回配置类失败结果，而不是抛采集异常。</summary>
    [TestMethod]
    public async Task TrialCapture_UnboundSource_ReturnsConfigurationFailure()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-3");
        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(
            runtime,
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog, Configuration("SN-3")));

        var result = await presenter.TrialCaptureAsync("Camera.Missing", cancellationToken: CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("SourceConfiguration", result.FailureKind);
        StringAssert.Contains(result.FailureMessage!, "未在已发布的机器配置中绑定");
        Assert.IsNull(result.ImageWidth);
        Assert.IsNull(result.PixelLayout);
        Assert.IsNull(result.CaptureId);
    }

    /// <summary>设备打不开（Required源失败）时试拍返回设备离线结果，运行时的降级状态同时可见。</summary>
    [TestMethod]
    public async Task TrialCapture_DeviceUnavailable_ReturnsOfflineFailure()
    {
        const string providerId = "dp.fake.offline";
        var composition = new VisionAcquisitionProviderComposer().Compose(
            new[]
            {
                Module("module.offline", new VisionAcquisitionProviderRegistration(
                    providerId,
                    "1.0.0",
                    () => new FakeVisionProvider(providerId, _ => throw new VisionDeviceOfflineException("相机电源未开。"))))
            },
            new[] { new VisionAcquisitionSourceBinding(SourceId, providerId, "top-camera", "camera:serial:OFFLINE") });

        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(runtime, composition, NewStore());

        var result = await presenter.TrialCaptureAsync(SourceId, cancellationToken: CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("DeviceOffline", result.FailureKind);
        StringAssert.Contains(result.FailureMessage!, "已标记为故障");
        Assert.AreEqual(EVisionRuntimeState.NotReady, runtime.RuntimeState, "Required源打不开时运行时不得伪装成可用。");
    }

    /// <summary>运行时已经停止时无法重启，试拍把它表达成失败结果而不是抛出只能启动一次的异常。</summary>
    [TestMethod]
    public async Task TrialCapture_StoppedRuntime_ReturnsFailureInsteadOfThrowing()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-3");
        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(
            runtime,
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog, Configuration("SN-3")));

        await runtime.StartAsync(CancellationToken.None);
        await runtime.StopAsync();

        var result = await presenter.TrialCaptureAsync(SourceId, cancellationToken: CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("DeviceOffline", result.FailureKind);
        StringAssert.Contains(result.FailureMessage!, "已经停止");
    }

    /// <summary>取消必须原样传播，不能被当成一次普通失败。</summary>
    [TestMethod]
    public async Task TrialCapture_CancellationPropagates()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-3");
        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(
            runtime,
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog, Configuration("SN-3")));
        await runtime.StartAsync(CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await presenter.TrialCaptureAsync(SourceId, cancellationToken: cancellation.Token));
    }

    /// <summary>空逻辑源标识是调用方错误，不是一次"试拍失败"，因此在进入试拍之前就明确拒绝。</summary>
    [TestMethod]
    public async Task TrialCapture_EmptySourceId_IsRejected()
    {
        var catalog = Catalog();
        var composition = ComposeMachineConfiguration(catalog, "SN-3");
        await using var runtime = new VisionAcquisitionRuntime(composition);
        var presenter = new AcquisitionManagementPresenter(
            runtime,
            composition,
            new VisionAcquisitionMachineConfigurationRevisionStore(catalog, Configuration("SN-3")));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await presenter.TrialCaptureAsync("   ", cancellationToken: CancellationToken.None));
    }

    private static VisionAcquisitionTypeCatalog Catalog() =>
        new VisionAcquisitionTypeCatalogComposer().Compose(
            new IVisionAcquisitionDriverModule[] { new ConfigurableAcquisitionDriverModule() });

    private static string Configuration(string serialNumber) =>
        "{\"sourceId\":\"" + SourceId + "\",\"acquisitionType\":\"" + AreaTypeId
        + "\",\"settingsVersion\":1,\"deviceSettings\":{\"serialNumber\":\"" + serialNumber + "\"}}";

    /// <summary>合法JSON但deviceSettings缺少serialNumber：Plugin解析器必须报出可照着改的错误。</summary>
    private static string ConfigurationWithoutSerial() =>
        "{\"sourceId\":\"" + SourceId + "\",\"acquisitionType\":\"" + AreaTypeId
        + "\",\"settingsVersion\":1,\"deviceSettings\":{\"serial\":\"SN-9\"}}";

    private static VisionAcquisitionProviderComposition ComposeMachineConfiguration(
        VisionAcquisitionTypeCatalog catalog,
        string serialNumber) =>
        new VisionAcquisitionMachineConfigurationComposer().Compose(
            catalog,
            VisionAcquisitionMachineConfigurationParser.Parse(Configuration(serialNumber)));

    private static VisionAcquisitionMachineConfigurationRevisionStore NewStore() =>
        new VisionAcquisitionMachineConfigurationRevisionStore(Catalog());

    private static IVisionAcquisitionProviderModule Module(
        string moduleId,
        VisionAcquisitionProviderRegistration registration) =>
        new FakeVisionProviderModule(moduleId, registration);

    private static VisionAcquisitionProviderRegistration DiscoveryRegistration(
        string providerId,
        params VisionDeviceDescriptor[] devices) =>
        new VisionAcquisitionProviderRegistration(providerId, "1.0.0", () =>
        {
            var provider = FakeVisionProvider.WithDevices(providerId);
            provider.Devices = devices;
            return provider;
        });

    private static VisionDeviceDescriptor Descriptor(
        string providerId,
        string bindingId,
        string canonicalKey,
        string displayName) =>
        new VisionDeviceDescriptor(providerId, bindingId, canonicalKey, displayName, "测试厂商", "测试型号", bindingId);
}