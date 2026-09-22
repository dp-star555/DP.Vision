using System;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 插件级可用性在Type Catalog与逻辑源目录上的投影回归（阶段B-2）。
/// </summary>
/// <remarks>
/// 旧插件路径（读 <c>plugin.json</c> 的 Manifest + ProviderPlugin 入口）删除后，
/// "插件已安装但底层运行时不可用"（HALCON缺SDK、Basler缺pylon原生库）这条诊断
/// 必须仍然可见——它现在由Driver Module的健康报告承载，并在Catalog冻结时被记录下来。
/// <para>
/// 关键行为：不可用插件声明的逻辑源<b>保真保留并带诊断</b>，但<b>不产生绑定</b>，
/// 于是它不会进入Runtime的设备打开流程。缺SDK因此表现为"源不可用 + 原因"，
/// 而不是"启动时打开设备失败"或更糟的"采集时才抛原生异常"。
/// </para>
/// </remarks>
[TestClass]
public sealed class VisionAcquisitionPluginAvailabilityTests
{
    private const string AreaTypeId = "dp.acquisition.test.area";

    private readonly VisionAcquisitionTypeCatalogComposer _catalogComposer = new();
    private readonly VisionAcquisitionMachineConfigurationComposer _composer = new();

    /// <summary>Module报告不可用时，Catalog记下插件身份与原因。</summary>
    [TestMethod]
    public void UnhealthyModule_RecordsPluginDiagnosticOnCatalog()
    {
        var catalog = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            new ConfigurableAcquisitionDriverModule(isAvailable: false, diagnostic: "测试运行时未部署。")
        });

        var availability = catalog.PluginAvailability.Single();
        Assert.AreEqual(ConfigurableAcquisitionDriverModule.PluginIdentity, availability.PluginId);
        Assert.IsFalse(availability.IsAvailable);
        Assert.AreEqual("测试运行时未部署。", availability.Diagnostic);

        Assert.IsTrue(catalog.TryGetPluginAvailability(ConfigurableAcquisitionDriverModule.PluginIdentity, out var found));
        Assert.AreEqual(availability, found);
        Assert.IsFalse(catalog.TryGetPluginAvailability("dp.vision.unknown", out _));
    }

    /// <summary>Module报告可用时不留诊断，避免把正常状态误报成故障。</summary>
    [TestMethod]
    public void HealthyModule_RecordsPluginAvailable()
    {
        var catalog = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            new ConfigurableAcquisitionDriverModule()
        });

        var availability = catalog.PluginAvailability.Single();
        Assert.IsTrue(availability.IsAvailable);
        Assert.IsNull(availability.Diagnostic);
    }

    /// <summary>未实现健康报告的Module视为可用：这是可选接口，不是"默认不可用"。</summary>
    [TestMethod]
    public void ModuleWithoutHealthReporting_IsTreatedAsAvailable()
    {
        var catalog = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            new FakeAcquisitionDriverModule(
                "module.silent",
                Registration(AreaTypeId, "dp.vision.silent"))
        });

        var availability = catalog.PluginAvailability.Single();
        Assert.AreEqual("dp.vision.silent", availability.PluginId);
        Assert.IsTrue(availability.IsAvailable, "没有健康报告的Module必须被当作可用，否则所有不关心运行时的插件都会变成不可用。");
        Assert.IsNull(availability.Diagnostic);
    }

    /// <summary>同一PluginId由多个Module声明时，任一Module不可用即整体不可用。</summary>
    [TestMethod]
    public void PluginDeclaredByMultipleModules_IsUnavailableIfAnyModuleIsUnavailable()
    {
        var healthy = new HealthReportingDriverModule("module.a", "dp.vision.shared", "dp.acquisition.shared.a", isAvailable: true);
        var broken = new HealthReportingDriverModule("module.b", "dp.vision.shared", "dp.acquisition.shared.b", isAvailable: false, diagnostic: "模块B的运行时缺失。");

        var allHealthy = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            healthy,
            new HealthReportingDriverModule("module.b", "dp.vision.shared", "dp.acquisition.shared.b", isAvailable: true)
        });
        Assert.IsTrue(allHealthy.PluginAvailability.Single().IsAvailable);

        var mixed = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[] { healthy, broken });

        var availability = mixed.PluginAvailability.Single();
        Assert.AreEqual("dp.vision.shared", availability.PluginId);
        Assert.IsFalse(availability.IsAvailable, "同一插件只要有一个Module报告不可用，该插件整体就不能算可用。");
        Assert.AreEqual("模块B的运行时缺失。", availability.Diagnostic);
    }

    /// <summary>同一PluginId的两个Module报出同一条原因时，只保留一条，不拼成"原因；原因"。</summary>
    [TestMethod]
    public void TwoModulesSharingPluginId_ReportSameReasonOnlyOnce()
    {
        var catalog = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            new HealthReportingDriverModule("module.a", "dp.vision.shared", "dp.acquisition.shared.a", isAvailable: false, diagnostic: "同一条原因。"),
            new HealthReportingDriverModule("module.b", "dp.vision.shared", "dp.acquisition.shared.b", isAvailable: false, diagnostic: "同一条原因。")
        });

        var availability = catalog.PluginAvailability.Single();
        Assert.IsFalse(availability.IsAvailable);
        Assert.AreEqual("同一条原因。", availability.Diagnostic);
    }

    /// <summary>插件可用时逻辑源照常可用，并带上配置摘要——这是不可用场景的对照。</summary>
    [TestMethod]
    public void AvailablePlugin_KeepsSourceAvailable()
    {
        var composition = ComposeOneCamera(new ConfigurableAcquisitionDriverModule());

        var entry = composition.SourceCatalog.Single();
        Assert.IsTrue(entry.IsAvailable);
        Assert.IsNull(entry.Diagnostic);
        Assert.AreEqual("camera:serial:SN-1", entry.ResourceKey);
        Assert.AreEqual(1, composition.Sources.Count);
        Assert.AreEqual("serialNumber=SN-1", composition.GetConfigurationSummary("Camera.Top"));
    }

    /// <summary>
    /// 插件不可用时逻辑源保真保留并带诊断，但不产生绑定：
    /// 源不进Runtime的设备打开流程，缺SDK因此表现为"源不可用 + 原因"而不是打开失败。
    /// </summary>
    [TestMethod]
    public void UnavailablePlugin_MarksSourceUnavailableWithoutPublishingBinding()
    {
        var available = ComposeOneCamera(new ConfigurableAcquisitionDriverModule());
        var composition = ComposeOneCamera(
            new ConfigurableAcquisitionDriverModule(isAvailable: false, diagnostic: "测试运行时未部署。"));

        var entry = composition.SourceCatalog.Single();
        Assert.AreEqual("Camera.Top", entry.SourceId);
        Assert.AreEqual(AreaTypeId, entry.AcquisitionTypeId);
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, entry.Kind);
        Assert.IsFalse(entry.IsAvailable);
        Assert.AreEqual("测试运行时未部署。", entry.Diagnostic);

        // 保真保留：源在目录里；但没有任何绑定与Provider注册，因此Runtime不会去打开它。
        Assert.IsTrue(composition.TryGetSourceEntry("Camera.Top", out _));
        Assert.IsFalse(composition.TryGetSource("Camera.Top", out _));
        Assert.AreEqual(0, composition.Sources.Count);
        Assert.AreEqual(0, composition.Providers.Count);
        Assert.IsNull(composition.GetConfigurationSummary("Camera.Top"));

        // 可用性变化改变了实际发布的绑定集合，因此组合身份也必须不同。
        Assert.AreNotEqual(available.CompositionId, composition.CompositionId);
    }

    /// <summary>一个插件不可用不得牵连另一个插件的逻辑源。</summary>
    [TestMethod]
    public void UnavailablePlugin_DoesNotAffectOtherPluginsSources()
    {
        var catalog = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            new ConfigurableAcquisitionDriverModule(),
            new HealthReportingDriverModule("module.bad", "dp.vision.bad", "dp.acquisition.bad.area", isAvailable: false, diagnostic: "坏插件的运行时缺失。")
        });

        var composition = _composer.Compose(
            catalog,
            new[]
            {
                Camera("Camera.Bad", "dp.acquisition.bad.area", "BAD-1"),
                Camera("Camera.Good", AreaTypeId, "GOOD-1")
            });

        var bad = composition.SourceCatalog.Single(item => item.SourceId == "Camera.Bad");
        Assert.IsFalse(bad.IsAvailable);
        Assert.AreEqual("坏插件的运行时缺失。", bad.Diagnostic);
        Assert.IsFalse(composition.TryGetSource("Camera.Bad", out _));

        var good = composition.SourceCatalog.Single(item => item.SourceId == "Camera.Good");
        Assert.IsTrue(good.IsAvailable, "另一个插件不可用不能把本插件的源也标成不可用。");
        Assert.IsNull(good.Diagnostic);
        Assert.IsTrue(composition.TryGetSource("Camera.Good", out var binding));
        Assert.AreEqual(AreaTypeId, binding!.ProviderId);
    }

    /// <summary>健康报告缺原因时仍要留下可读诊断，不能变成空字符串。</summary>
    [TestMethod]
    public void UnhealthyModuleWithoutDiagnostic_StillProducesReadableReason()
    {
        var catalog = _catalogComposer.Compose(new IVisionAcquisitionDriverModule[]
        {
            new HealthReportingDriverModule("module.quiet", "dp.vision.quiet", "dp.acquisition.quiet.area", isAvailable: false)
        });

        var availability = catalog.PluginAvailability.Single();
        Assert.IsFalse(availability.IsAvailable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(availability.Diagnostic));
        StringAssert.Contains(availability.Diagnostic, "dp.vision.quiet");
    }

    private VisionAcquisitionProviderComposition ComposeOneCamera(IVisionAcquisitionDriverModule module)
    {
        var catalog = _catalogComposer.Compose(new[] { module });
        return _composer.Compose(catalog, new[] { Camera("Camera.Top", AreaTypeId, "SN-1") });
    }

    private static VisionAcquisitionCameraDefinition Camera(
        string sourceId,
        string acquisitionTypeId,
        string serialNumber) =>
        new VisionAcquisitionCameraDefinition(
            sourceId,
            acquisitionTypeId,
            1,
            isRequired: false,
            new VisionAcquisitionConnectionPolicy(
                OpenOnApplicationStart: true,
                EVisionAcquisitionTransferStart.PerRequest),
            inbox: null,
            "{\"serialNumber\":\"" + serialNumber + "\"}");

    private static VisionAcquisitionTypeRegistration Registration(string typeId, string pluginId) =>
        new VisionAcquisitionTypeRegistration(
            typeId,
            pluginId,
            "1.0.0",
            EVisionAcquisitionKind.AreaScan,
            1,
            "静默Module相机",
            new VisionAcquisitionTypeCapabilities(SupportsFreeRun: true),
            () => FakeVisionProvider.WithDevices(pluginId + ".provider"),
            TestDeviceSettingsParser.Parse);
}
