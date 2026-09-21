using System;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// HALCON采集Driver Module回归（V2-1 §20）：本程序集必须向Type Catalog贡献面阵AreaScan Type，
/// 并且可以被Driver Module扫描器从插件目录发现——不依赖Manifest、不读取机器配置。
/// </summary>
[TestClass]
public sealed class HalconAcquisitionDriverModuleTests
{
    private static readonly string PluginDirectory =
        Path.GetDirectoryName(typeof(HalconAcquisitionProviderPlugin).Assembly.Location)!;

    /// <summary>Driver Module贡献一个HALCON面阵AreaScan Type。</summary>
    [TestMethod]
    public void DriverModule_ContributesAreaScanType()
    {
        var module = new HalconAcquisitionDriverModule();

        var registration = Collect(module);

        Assert.AreEqual(HalconAcquisitionDriverModule.AreaScanTypeId, registration.AcquisitionTypeId);
        Assert.AreEqual(HalconAcquisitionProviderPlugin.PluginIdentity, registration.PluginId);
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, registration.Kind);
        Assert.AreEqual(HalconAcquisitionDriverModule.TypeVersion, registration.Version);
        Assert.IsNotNull(registration.Factory);
        Assert.IsTrue(registration.Capabilities.SupportsFreeRun);
        Assert.IsTrue(registration.Capabilities.SupportsSoftwareTrigger);
        // 外部触发与完整帧回调只在SDK编译进程序集时声明；缺SDK时由Provider健康报告单独给出诊断。
        Assert.AreEqual(HalconCameraCapture.IsSdkEnabled, registration.Capabilities.SupportsCompleteFrameCallback);
        Assert.AreEqual(HalconCameraCapture.IsSdkEnabled, registration.Capabilities.SupportsExternalTrigger);
    }

    /// <summary>插件目录被扫描后Catalog列出HALCON面阵Type；全程不需要Manifest与机器配置。</summary>
    [TestMethod]
    public void PluginDirectory_ContributesAcquisitionTypeWithoutManifest()
    {
        var root = StageDriverDirectory();

        var result = new VisionAcquisitionDriverModuleLoader().Load(root);
        var catalog = new VisionAcquisitionTypeCatalogComposer().Compose(result.Modules);

        Assert.IsTrue(catalog.TryGetType(HalconAcquisitionDriverModule.AreaScanTypeId, out var descriptor));
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, descriptor!.Kind);
        Assert.AreEqual(HalconAcquisitionProviderPlugin.PluginIdentity, descriptor.PluginId);
    }

    private static VisionAcquisitionTypeRegistration Collect(HalconAcquisitionDriverModule module)
    {
        var builder = new CollectingBuilder();
        module.Contribute(builder);
        return builder.Registrations.Single();
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
        public System.Collections.Generic.List<VisionAcquisitionTypeRegistration> Registrations { get; } =
            new System.Collections.Generic.List<VisionAcquisitionTypeRegistration>();

        public void Register(VisionAcquisitionTypeRegistration registration) => Registrations.Add(registration);
    }
}
