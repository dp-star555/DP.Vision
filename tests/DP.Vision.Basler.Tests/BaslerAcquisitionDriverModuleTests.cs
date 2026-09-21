using System;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// Basler采集Driver Module回归（V2-1 §20）：本程序集必须向Type Catalog贡献面阵AreaScan Type，
/// 并且可以被Driver Module扫描器从插件目录发现——不依赖Manifest、不读取机器配置。
/// </summary>
[TestClass]
public sealed class BaslerAcquisitionDriverModuleTests
{
    private static readonly string PluginDirectory =
        Path.GetDirectoryName(typeof(BaslerAcquisitionProviderPlugin).Assembly.Location)!;

    /// <summary>Driver Module贡献一个Basler面阵AreaScan Type。</summary>
    [TestMethod]
    public void DriverModule_ContributesAreaScanType()
    {
        var module = new BaslerAcquisitionDriverModule();

        var registration = Collect(module);

        Assert.AreEqual(BaslerAcquisitionDriverModule.AreaScanTypeId, registration.AcquisitionTypeId);
        Assert.AreEqual(BaslerAcquisitionProviderPlugin.PluginIdentity, registration.PluginId);
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, registration.Kind);
        Assert.AreEqual(BaslerAcquisitionDriverModule.TypeVersion, registration.Version);
        Assert.IsNotNull(registration.Factory);
        Assert.IsTrue(registration.Capabilities.SupportsFreeRun);
        Assert.IsTrue(registration.Capabilities.SupportsSoftwareTrigger);
        Assert.IsTrue(registration.Capabilities.SupportsExternalTrigger);
        Assert.IsTrue(registration.Capabilities.SupportsCompleteFrameCallback);
    }

    /// <summary>插件目录被扫描后Catalog列出Basler面阵Type；全程不需要Manifest与机器配置。</summary>
    [TestMethod]
    public void PluginDirectory_ContributesAcquisitionTypeWithoutManifest()
    {
        var root = StageDriverDirectory();

        var result = new VisionAcquisitionDriverModuleLoader().Load(root);
        var catalog = new VisionAcquisitionTypeCatalogComposer().Compose(result.Modules);

        Assert.IsTrue(catalog.TryGetType(BaslerAcquisitionDriverModule.AreaScanTypeId, out var descriptor));
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, descriptor!.Kind);
        Assert.AreEqual(BaslerAcquisitionProviderPlugin.PluginIdentity, descriptor.PluginId);
    }

    private static VisionAcquisitionTypeRegistration Collect(BaslerAcquisitionDriverModule module)
    {
        var builder = new CollectingBuilder();
        module.Contribute(builder);
        return builder.Registrations.Single();
    }

    /// <summary>
    /// 把插件输出目录的文件（不含plugin.json）复制到临时目录：证明发现不依赖Manifest，
    /// 同时保证厂商依赖程序集（pylon托管程序集）可被解析。
    /// </summary>
    private static string StageDriverDirectory()
    {
        var target = Path.Combine(Path.GetTempPath(), "dp-vision-basler-driver-" + Guid.NewGuid().ToString("N"));
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
