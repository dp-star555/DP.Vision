using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// HALCON采集Driver Module回归（V2-1 §20、V2-8 §20）：本程序集必须向Type Catalog贡献面阵与线扫两个Type，
/// 并且可以被Driver Module扫描器从插件目录发现——不依赖Manifest、不读取机器配置。
/// </summary>
[TestClass]
public sealed class HalconAcquisitionDriverModuleTests
{
    private static readonly string PluginDirectory =
        Path.GetDirectoryName(typeof(HalconAcquisitionProviderPlugin).Assembly.Location)!;

    /// <summary>Driver Module贡献HALCON面阵AreaScan Type。</summary>
    [TestMethod]
    public void DriverModule_ContributesAreaScanType()
    {
        var registration = Collect(new HalconAcquisitionDriverModule(), HalconAcquisitionDriverModule.AreaScanTypeId);

        Assert.AreEqual(HalconAcquisitionProviderPlugin.PluginIdentity, registration.PluginId);
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, registration.Kind);
        Assert.AreEqual(HalconAcquisitionDriverModule.TypeVersion, registration.Version);
        Assert.AreEqual(HalconAcquisitionDriverModule.DeviceSettingsVersion, registration.DeviceSettingsVersion);
        Assert.AreEqual("HALCON 面阵相机", registration.DisplayName);
        Assert.IsNotNull(registration.Factory);
        Assert.IsTrue(registration.Capabilities.SupportsFreeRun);
        Assert.IsTrue(registration.Capabilities.SupportsSoftwareTrigger);
        // 外部触发与完整帧回调只在SDK编译进程序集时声明；缺SDK时由Provider健康报告单独给出诊断。
        Assert.AreEqual(HalconStreamCameras.IsSdkEnabled, registration.Capabilities.SupportsCompleteFrameCallback);
        Assert.AreEqual(HalconStreamCameras.IsSdkEnabled, registration.Capabilities.SupportsExternalTrigger);
    }

    /// <summary>Driver Module贡献HALCON线扫LineScan Type，能力声明与面阵一致。</summary>
    [TestMethod]
    public void DriverModule_ContributesLineScanType()
    {
        var registration = Collect(new HalconAcquisitionDriverModule(), HalconAcquisitionDriverModule.LineScanTypeId);

        Assert.AreEqual(HalconAcquisitionProviderPlugin.PluginIdentity, registration.PluginId);
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
        Assert.AreEqual(HalconAcquisitionProviderPlugin.PluginIdentity, area.PluginId);
        Assert.IsTrue(catalog.TryGetType(HalconAcquisitionDriverModule.LineScanTypeId, out var line));
        Assert.AreEqual(EVisionAcquisitionKind.LineScan, line!.Kind);
        CollectionAssert.AreEqual(
            new[] { HalconAcquisitionDriverModule.AreaScanTypeId },
            catalog.GetByKind(EVisionAcquisitionKind.AreaScan).Select(item => item.AcquisitionTypeId).ToArray());
        CollectionAssert.AreEqual(
            new[] { HalconAcquisitionDriverModule.LineScanTypeId },
            catalog.GetByKind(EVisionAcquisitionKind.LineScan).Select(item => item.AcquisitionTypeId).ToArray());
    }

    private static VisionAcquisitionTypeRegistration Collect(HalconAcquisitionDriverModule module, string typeId)
    {
        var builder = new CollectingBuilder();
        module.Contribute(builder);
        return builder.Registrations.Single(registration =>
            string.Equals(registration.AcquisitionTypeId, typeId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 把插件输出目录的文件（不含plugin.json）复制到临时目录：证明发现不依赖Manifest，
    /// 同时保证厂商依赖程序集（含SDK构建下的halcondotnet.dll）可被解析。
    /// </summary>
    private static string StageDriverDirectory()
    {
        var target = Path.Combine(Path.GetTempPath(), "dp-vision-halcon-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(PluginDirectory))
        {
            if (Path.GetFileName(file).Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        return target;
    }

    private sealed class CollectingBuilder : IVisionAcquisitionTypeContributionBuilder
    {
        public List<VisionAcquisitionTypeRegistration> Registrations { get; } =
            new List<VisionAcquisitionTypeRegistration>();

        public void Register(VisionAcquisitionTypeRegistration registration) => Registrations.Add(registration);
    }
}
