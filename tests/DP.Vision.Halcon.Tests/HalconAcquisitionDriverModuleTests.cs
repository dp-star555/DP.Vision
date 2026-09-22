using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// HALCON采集Driver Module回归（V2-1 §20、V2-8 §20）：本程序集必须向Type Catalog贡献面阵与线扫两个Type，
/// 可以被Driver Module扫描器从插件目录发现——不依赖Manifest、不读取机器配置——
/// 并在缺SDK时通过健康报告给出Provider级诊断。
/// </summary>
[TestClass]
public sealed class HalconAcquisitionDriverModuleTests
{
    private static readonly string PluginDirectory =
        Path.GetDirectoryName(typeof(HalconAcquisitionDriverModule).Assembly.Location)!;

    /// <summary>Driver Module贡献HALCON面阵AreaScan Type。</summary>
    [TestMethod]
    public void DriverModule_ContributesAreaScanType()
    {
        var registration = Collect(new HalconAcquisitionDriverModule(), HalconAcquisitionDriverModule.AreaScanTypeId);

        Assert.AreEqual(HalconAcquisitionDriverModule.PluginIdentity, registration.PluginId);
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, registration.Kind);
        Assert.AreEqual(HalconAcquisitionDriverModule.TypeVersion, registration.Version);
        Assert.AreEqual(HalconAcquisitionDriverModule.DeviceSettingsVersion, registration.DeviceSettingsVersion);
        Assert.AreEqual("HALCON 面阵相机", registration.DisplayName);
        Assert.IsNotNull(registration.Factory);
        Assert.IsTrue(registration.Capabilities.SupportsFreeRun);
        Assert.IsTrue(registration.Capabilities.SupportsSoftwareTrigger);
        // 外部触发与完整帧回调只在SDK编译进程序集时声明；缺SDK时由本Module的健康报告单独给出诊断。
        Assert.AreEqual(HalconStreamCameras.IsSdkEnabled, registration.Capabilities.SupportsCompleteFrameCallback);
        Assert.AreEqual(HalconStreamCameras.IsSdkEnabled, registration.Capabilities.SupportsExternalTrigger);
    }

    /// <summary>Driver Module贡献HALCON线扫LineScan Type，能力声明与面阵一致。</summary>
    [TestMethod]
    public void DriverModule_ContributesLineScanType()
    {
        var registration = Collect(new HalconAcquisitionDriverModule(), HalconAcquisitionDriverModule.LineScanTypeId);

        Assert.AreEqual(HalconAcquisitionDriverModule.PluginIdentity, registration.PluginId);
        Assert.AreEqual(EVisionAcquisitionKind.LineScan, registration.Kind);
        Assert.AreEqual("HALCON 线扫相机", registration.DisplayName);
        Assert.IsNotNull(registration.Factory);
        Assert.AreEqual(
            Collect(new HalconAcquisitionDriverModule(), HalconAcquisitionDriverModule.AreaScanTypeId).Capabilities,
            registration.Capabilities,
            "线扫整图同样由SDK组装，取图与触发路径与面阵一致，不额外声明或隐瞒能力。");
    }

    /// <summary>
    /// 两个Type共享同一份私有配置契约：同一份deviceSettings解析出同样的绑定身份、资源键与摘要。
    /// 线扫的deviceSettings没有额外字段，因此不另立解析器制造会漂移的第二份契约。
    /// </summary>
    [TestMethod]
    public void DriverModule_TypesSharePrivateDeviceSettingsContract()
    {
        var module = new HalconAcquisitionDriverModule();
        const string settings = "{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"line-1\",\"serialNumber\":\"LINE-001\"}";

        var area = Collect(module, HalconAcquisitionDriverModule.AreaScanTypeId).DeviceSettingsParser!(settings);
        var line = Collect(module, HalconAcquisitionDriverModule.LineScanTypeId).DeviceSettingsParser!(settings);

        Assert.AreEqual(area, line);
        Assert.AreEqual("camera:serial:LINE-001", line.ResourceKey);
    }

    /// <summary>插件目录被扫描后Catalog按Kind分别列出面阵与线扫；全程不需要Manifest与机器配置。</summary>
    [TestMethod]
    public void PluginDirectory_ContributesBothKindsWithoutManifest()
    {
        var root = StageDriverDirectory();

        var result = new VisionAcquisitionDriverModuleLoader().Load(root);
        var catalog = new VisionAcquisitionTypeCatalogComposer().Compose(result.Modules);

        Assert.IsTrue(catalog.TryGetType(HalconAcquisitionDriverModule.AreaScanTypeId, out var area));
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, area!.Kind);
        Assert.AreEqual(HalconAcquisitionDriverModule.PluginIdentity, area.PluginId);
        Assert.IsTrue(catalog.TryGetType(HalconAcquisitionDriverModule.LineScanTypeId, out var line));
        Assert.AreEqual(EVisionAcquisitionKind.LineScan, line!.Kind);
        CollectionAssert.AreEqual(
            new[] { HalconAcquisitionDriverModule.AreaScanTypeId },
            catalog.GetByKind(EVisionAcquisitionKind.AreaScan).Select(item => item.AcquisitionTypeId).ToArray());
        CollectionAssert.AreEqual(
            new[] { HalconAcquisitionDriverModule.LineScanTypeId },
            catalog.GetByKind(EVisionAcquisitionKind.LineScan).Select(item => item.AcquisitionTypeId).ToArray());
    }

    /// <summary>SDK是否部署必须通过插件级诊断暴露，而不是等到采集时才失败。</summary>
    [TestMethod]
    public void DriverModuleHealth_ReportsSdkAvailability()
    {
        var catalog = new VisionAcquisitionTypeCatalogComposer()
            .Compose(new IVisionAcquisitionDriverModule[] { new HalconAcquisitionDriverModule() });

        var availability = catalog.PluginAvailability
            .Single(item => item.PluginId == HalconAcquisitionDriverModule.PluginIdentity);

        Assert.AreEqual(HalconStreamCameras.IsSdkEnabled, availability.IsAvailable);
        if (HalconStreamCameras.IsSdkEnabled)
            Assert.IsNull(availability.Diagnostic);
        else
            StringAssert.Contains(availability.Diagnostic, HalconAcquisitionProvider.ProviderIdentity);
    }

    /// <summary>SDK缺失时必须给出带Provider身份的诊断；注入探测结果以便在装有SDK的机器上也能覆盖该分支。</summary>
    [TestMethod]
    public void DriverModuleHealth_WithoutSdk_ReportsProviderDiagnostic()
    {
        var module = new HalconAcquisitionDriverModule(() => false);

        Assert.IsFalse(module.TryGetHealth(out var diagnostic));
        StringAssert.Contains(diagnostic, HalconAcquisitionProvider.ProviderIdentity);
        StringAssert.Contains(diagnostic, "HALCON SDK");
    }

    /// <summary>SDK已部署时不产生诊断，避免把正常状态误报成故障。</summary>
    [TestMethod]
    public void DriverModuleHealth_WithSdk_ReportsAvailable()
    {
        var module = new HalconAcquisitionDriverModule(() => true);

        Assert.IsTrue(module.TryGetHealth(out var diagnostic));
        Assert.IsNull(diagnostic);
    }

    /// <summary>
    /// 插件程序集所在目录必须自包含它的厂商依赖。
    /// 加载器用 <c>Assembly.GetExportedTypes()</c> 发现Driver Module，依赖解析不到就会整包加载失败；
    /// 部署脚本按"整个输出目录"投放插件包，所以这里守住"输出目录里有厂商程序集"这一条。
    /// 宿主提供的契约程序集（<c>DP.Vision</c>、<c>DP.Vision.Acquisition.Abstractions</c>）不在检查范围：
    /// 它们必须来自宿主，放进插件包反而会让插件拿到第二份契约类型，与宿主的中立接口不是同一个类型。
    /// </summary>
    [TestMethod]
    public void PluginDirectory_ContainsVendorDependencies()
    {
        var missing = typeof(HalconAcquisitionDriverModule).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.Length > 0)
            .Where(name => !HostProvidedAssemblies.Contains(name))
            .Where(name => !IsFrameworkAssembly(name))
            .Where(name => !File.Exists(Path.Combine(PluginDirectory, name + ".dll")))
            .ToArray();

        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            missing,
            "插件目录缺少依赖程序集：" + string.Join("、", missing) + "（目录 " + PluginDirectory + "）");
    }

    private static readonly string[] HostProvidedAssemblies =
    {
        "DP.Vision",
        "DP.Vision.Acquisition.Abstractions"
    };

    private static bool IsFrameworkAssembly(string name) =>
        name.StartsWith("System", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal)
        || name is "netstandard" or "mscorlib";

    private static VisionAcquisitionTypeRegistration Collect(HalconAcquisitionDriverModule module, string typeId)
    {
        var builder = new CollectingBuilder();
        module.Contribute(builder);
        return builder.Registrations.Single(registration =>
            string.Equals(registration.AcquisitionTypeId, typeId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 把插件输出目录的文件复制到临时目录：证明发现不依赖任何Manifest，
    /// 同时保证厂商依赖程序集（含SDK构建下的halcondotnet.dll）可被解析。
    /// </summary>
    private static string StageDriverDirectory()
    {
        var target = Path.Combine(Path.GetTempPath(), "dp-vision-halcon-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(PluginDirectory))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        return target;
    }

    private sealed class CollectingBuilder : IVisionAcquisitionTypeContributionBuilder
    {
        public List<VisionAcquisitionTypeRegistration> Registrations { get; } =
            new List<VisionAcquisitionTypeRegistration>();

        public void Register(VisionAcquisitionTypeRegistration registration) => Registrations.Add(registration);
    }
}
