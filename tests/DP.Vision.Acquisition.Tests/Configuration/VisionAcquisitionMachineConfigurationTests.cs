using System;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// V2-2：机器相机配置 → CameraDefinition → 唯一Composition与SourceCatalog 的回归。
/// 覆盖验收§21.3（未安装Type保真且不可用）与§21.26（私有配置变化改变CompositionId）、
/// 策略推导、settingsVersion校验、OnConnect/PerRequest 缓冲策略约束、共享Provider注册与公共字段严格解析。
/// </summary>
[TestClass]
public sealed class VisionAcquisitionMachineConfigurationTests
{
    private const string AreaTypeId = "dp.acquisition.test.area";
    private const string LineTypeId = "dp.acquisition.test.line";

    private readonly VisionAcquisitionTypeCatalogComposer _catalogComposer = new();
    private readonly VisionAcquisitionMachineConfigurationComposer _composer = new();

    /// <summary>面阵+线扫均支持完整帧回调的测试Catalog。</summary>
    private VisionAcquisitionTypeCatalog ComposeAreaCatalog() =>
        _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            new ConfigurableAcquisitionDriverModule()
        });

    private static VisionAcquisitionCameraDefinition AreaCamera(
        string sourceId,
        string serialNumber,
        EVisionAcquisitionTransferStart transferStart = EVisionAcquisitionTransferStart.PerRequest,
        VisionFrameInboxPolicy? inbox = null,
        bool isRequired = false,
        int settingsVersion = 1) =>
        new VisionAcquisitionCameraDefinition(
            sourceId,
            AreaTypeId,
            settingsVersion,
            isRequired,
            new VisionAcquisitionConnectionPolicy(OpenOnApplicationStart: true, transferStart),
            inbox,
            "{\"serialNumber\":\"" + serialNumber + "\"}");

    private static VisionFrameInboxPolicy Inbox() =>
        new VisionFrameInboxPolicy(capacity: 4, byteBudget: 16_000_000, TimeSpan.FromSeconds(2));

    /// <summary>一份面阵配置生成唯一Composition，SourceCatalog与Binding一致投影（完成条件）。</summary>
    [TestMethod]
    public void SingleCamera_ProjectsUniqueCompositionAndSourceCatalog()
    {
        var composition = _composer.Compose(
            ComposeAreaCatalog(),
            new[] { AreaCamera("Camera.Top", "SN-1") });

        Assert.IsFalse(string.IsNullOrWhiteSpace(composition.CompositionId));

        var binding = composition.Sources.Single();
        Assert.AreEqual("Camera.Top", binding.SourceId);
        Assert.AreEqual(AreaTypeId, binding.ProviderId);
        Assert.AreEqual("test:serial:SN-1", binding.ProviderBindingId);
        Assert.AreEqual("camera:serial:SN-1", binding.ResourceKey);
        Assert.AreEqual(EVisionSourceSharingPolicy.ExclusiveOperation, binding.SharingPolicy);
        Assert.AreEqual(EVisionAcquisitionMode.OnDemand, binding.AcquisitionMode);
        Assert.IsNull(binding.InboxPolicy);

        var entry = composition.SourceCatalog.Single();
        Assert.AreEqual("Camera.Top", entry.SourceId);
        Assert.AreEqual(AreaTypeId, entry.ProviderId);
        Assert.AreEqual(AreaTypeId, entry.AcquisitionTypeId);
        Assert.AreEqual("camera:serial:SN-1", entry.ResourceKey);
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, entry.Kind);
        Assert.IsTrue(entry.IsAvailable);
        Assert.IsNull(entry.Diagnostic);

        Assert.IsTrue(composition.TryGetSourceEntry("Camera.Top", out _));
        Assert.IsFalse(composition.TryGetSourceEntry("Camera.Missing", out _));
        Assert.AreEqual("serialNumber=SN-1", composition.GetConfigurationSummary("Camera.Top"));
        CollectionAssert.AreEqual(
            new[] { AreaTypeId + "@1.0.0" },
            composition.ProviderManifest.ToArray());
    }

    /// <summary>机器配置单对象与数组两种形态解析出相同定义，产生相同组合身份。</summary>
    [TestMethod]
    public void ArrayAndSingleObject_ProduceSameComposition()
    {
        var single = VisionAcquisitionMachineConfigurationParser.Parse(
            "{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + AreaTypeId
            + "\",\"settingsVersion\":1,\"deviceSettings\":{\"serialNumber\":\"SN-1\"}}");
        var array = VisionAcquisitionMachineConfigurationParser.Parse(
            "[{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + AreaTypeId
            + "\",\"settingsVersion\":1,\"deviceSettings\":{\"serialNumber\":\"SN-1\"}}]");

        var catalog = ComposeAreaCatalog();
        Assert.AreEqual(
            _composer.Compose(catalog, single).CompositionId,
            _composer.Compose(catalog, array).CompositionId);
    }

    /// <summary>解析器保留deviceSettings原始JSON供Plugin解析（公共层不解释）。</summary>
    [TestMethod]
    public void Parser_PreservesRawDeviceSettings()
    {
        var definitions = VisionAcquisitionMachineConfigurationParser.Parse(
            "{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + AreaTypeId
            + "\",\"settingsVersion\":1,\"deviceSettings\":{\"serialNumber\":\"SN-1\"}}");

        var camera = definitions.Single();
        Assert.AreEqual("Camera.Top", camera.SourceId);
        Assert.AreEqual(AreaTypeId, camera.AcquisitionTypeId);
        Assert.AreEqual(1, camera.SettingsVersion);
        Assert.AreEqual("{\"serialNumber\":\"SN-1\"}", camera.DeviceSettingsJson);
        Assert.AreEqual(EVisionAcquisitionTransferStart.PerRequest, camera.Connection.TransferStart);
        Assert.IsNull(camera.Inbox);
    }

    /// <summary>公共层未知字段一律拒绝，避免拼写错误被静默忽略。</summary>
    [TestMethod]
    public void UnknownPublicField_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            VisionAcquisitionMachineConfigurationParser.Parse(
                "{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + AreaTypeId
                + "\",\"settingsVersion\":1,\"typoField\":1}"));

        StringAssert.Contains(failure.Message, "未知字段");
    }

    /// <summary>数组中重复SourceId拒绝解析。</summary>
    [TestMethod]
    public void DuplicateSourceIdInArray_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            VisionAcquisitionMachineConfigurationParser.Parse(
                "[{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + AreaTypeId
                + "\",\"settingsVersion\":1},{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\""
                + AreaTypeId + "\",\"settingsVersion\":1}]"));

        StringAssert.Contains(failure.Message, "逻辑源身份重复");
    }

    /// <summary>settingsVersion与Type声明不一致时拒绝组合。</summary>
    [TestMethod]
    public void SettingsVersionMismatch_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            ComposeAreaCatalog(),
            new[] { AreaCamera("Camera.Top", "SN-1", settingsVersion: 2) }));

        StringAssert.Contains(failure.Message, "settingsVersion");
        StringAssert.Contains(failure.Message, "不一致");
    }

    /// <summary>OnConnect但Type未声明完整帧回调能力时拒绝组合。</summary>
    [TestMethod]
    public void OnConnectWithoutCallbackCapability_IsRejected()
    {
        var noCallback = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            new FakeAcquisitionDriverModule(
                "module.no-callback",
                new VisionAcquisitionTypeRegistration(
                    "dp.acquisition.test.area",
                    "dp.vision.test",
                    "1.0.0",
                    EVisionAcquisitionKind.AreaScan,
                    1,
                    "无回调测试",
                    new VisionAcquisitionTypeCapabilities(SupportsFreeRun: true),
                    () => FakeVisionProvider.WithDevices("dp.vision.test.provider"),
                    TestDeviceSettingsParser.Parse))
        });

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            noCallback,
            new[] { AreaCamera("Camera.Top", "SN-1", EVisionAcquisitionTransferStart.OnConnect, Inbox()) }));

        StringAssert.Contains(failure.Message, "完整帧回调");
    }

    /// <summary>OnConnect源必须声明Inbox，否则定义构造拒绝。</summary>
    [TestMethod]
    public void OnConnectWithoutInbox_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AreaCamera(
            "Camera.Top",
            "SN-1",
            EVisionAcquisitionTransferStart.OnConnect,
            inbox: null));
    }

    /// <summary>PerRequest源不接受Inbox，否则定义构造拒绝。</summary>
    [TestMethod]
    public void PerRequestWithInbox_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AreaCamera(
            "Camera.Top",
            "SN-1",
            EVisionAcquisitionTransferStart.PerRequest,
            inbox: Inbox()));
    }

    /// <summary>OnConnect源投影为ExclusiveRun/BufferedExternal并携带Inbox。</summary>
    [TestMethod]
    public void OnConnect_PublishesBufferedExclusiveRunBinding()
    {
        var inbox = Inbox();
        var composition = _composer.Compose(
            ComposeAreaCatalog(),
            new[] { AreaCamera("Camera.Top", "SN-1", EVisionAcquisitionTransferStart.OnConnect, inbox) });

        var binding = composition.Sources.Single();
        Assert.AreEqual(EVisionSourceSharingPolicy.ExclusiveRun, binding.SharingPolicy);
        Assert.AreEqual(EVisionAcquisitionMode.BufferedExternal, binding.AcquisitionMode);
        Assert.AreEqual(inbox, binding.InboxPolicy);

        var entry = composition.SourceCatalog.Single();
        Assert.IsTrue(entry.IsAvailable);
        Assert.AreEqual(EVisionSourceSharingPolicy.ExclusiveRun, entry.SharingPolicy);
        Assert.AreEqual(EVisionAcquisitionMode.BufferedExternal, entry.AcquisitionMode);
    }

    /// <summary>同一Type的多个Source共享一个Provider注册（Provider身份等价于AcquisitionTypeId）。</summary>
    [TestMethod]
    public void MultipleSourcesOnSameType_ShareProviderRegistration()
    {
        var composition = _composer.Compose(
            ComposeAreaCatalog(),
            new[]
            {
                AreaCamera("Camera.Top", "SN-1"),
                AreaCamera("Camera.Side", "SN-2")
            });

        Assert.AreEqual(2, composition.Sources.Count);
        Assert.AreEqual(1, composition.ProviderManifest.Count);
        Assert.IsTrue(composition.TryGetProvider(AreaTypeId, out _));
        Assert.IsFalse(composition.TryGetProvider("dp.vision.test.driver", out _));
    }

    /// <summary>未安装Type的Source保真保留、标记不可用，且不产生Binding（验收§21.3）。</summary>
    [TestMethod]
    public void UninstalledType_IsPreservedUnavailable()
    {
        var emptyCatalog = _catalogComposer.Compose(Array.Empty<IVisionAcquisitionDriverModule>());

        var composition = _composer.Compose(
            emptyCatalog,
            new[] { AreaCamera("Camera.Top", "SN-1") });

        Assert.IsFalse(string.IsNullOrWhiteSpace(composition.CompositionId));
        Assert.AreEqual(0, composition.Sources.Count);
        Assert.IsFalse(composition.TryGetSource("Camera.Top", out _));

        var entry = composition.SourceCatalog.Single();
        Assert.AreEqual("Camera.Top", entry.SourceId);
        Assert.AreEqual(AreaTypeId, entry.AcquisitionTypeId);
        Assert.IsFalse(entry.IsAvailable);
        Assert.IsNotNull(entry.Diagnostic);
        StringAssert.Contains(entry.Diagnostic, "未安装");
        Assert.IsNull(composition.GetConfigurationSummary("Camera.Top"));
    }

    /// <summary>私有配置变化必须改变CompositionId；相同配置产生相同身份（验收§21.26）。</summary>
    [TestMethod]
    public void PrivateSettingsChange_ChangesCompositionId()
    {
        var catalog = ComposeAreaCatalog();
        var first = _composer.Compose(catalog, new[] { AreaCamera("Camera.Top", "SN-1") });
        var identical = _composer.Compose(catalog, new[] { AreaCamera("Camera.Top", "SN-1") });
        var changed = _composer.Compose(catalog, new[] { AreaCamera("Camera.Top", "SN-2") });

        Assert.AreEqual(first.CompositionId, identical.CompositionId);
        Assert.AreNotEqual(first.CompositionId, changed.CompositionId);
    }
}
