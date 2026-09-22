using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// Basler采集Driver Module回归（V2-1 §20）：本程序集必须向Type Catalog贡献面阵AreaScan Type，
/// 可以被Driver Module扫描器从插件目录发现——不依赖Manifest、不读取机器配置——
/// 并在缺pylon原生运行时通过健康报告给出Provider级诊断。
/// </summary>
[TestClass]
public sealed class BaslerAcquisitionDriverModuleTests
{
    private static readonly string PluginDirectory =
        Path.GetDirectoryName(typeof(BaslerAcquisitionDriverModule).Assembly.Location)!;

    /// <summary>Driver Module贡献一个Basler面阵AreaScan Type。</summary>
    [TestMethod]
    public void DriverModule_ContributesAreaScanType()
    {
        var module = new BaslerAcquisitionDriverModule();

        var registration = Collect(module);

        Assert.AreEqual(BaslerAcquisitionDriverModule.AreaScanTypeId, registration.AcquisitionTypeId);
        Assert.AreEqual(BaslerAcquisitionDriverModule.PluginIdentity, registration.PluginId);
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
        Assert.AreEqual(BaslerAcquisitionDriverModule.PluginIdentity, descriptor.PluginId);
    }

    /// <summary>缺原生运行时必须通过插件级诊断暴露，而不是等到采集时抛原生异常。</summary>
    [TestMethod]
    public void DriverModuleHealth_ReportsRuntimeAvailability()
    {
        var catalog = new VisionAcquisitionTypeCatalogComposer()
            .Compose(new IVisionAcquisitionDriverModule[] { new BaslerAcquisitionDriverModule() });

        var availability = catalog.PluginAvailability
            .Single(item => item.PluginId == BaslerAcquisitionDriverModule.PluginIdentity);

        Assert.AreEqual(BaslerPylonRuntime.IsDeployed, availability.IsAvailable);
        if (BaslerPylonRuntime.IsDeployed)
            Assert.IsNull(availability.Diagnostic);
        else
            StringAssert.Contains(availability.Diagnostic, BaslerAcquisitionProvider.ProviderIdentity);
    }

    /// <summary>缺运行时必须给出带Provider身份的诊断；注入探测结果以便在装有pylon的机器上也能覆盖该分支。</summary>
    [TestMethod]
    public void DriverModuleHealth_WithoutRuntime_ReportsProviderDiagnostic()
    {
        var module = new BaslerAcquisitionDriverModule(() => false);

        Assert.IsFalse(module.TryGetHealth(out var diagnostic));
        StringAssert.Contains(diagnostic, BaslerAcquisitionProvider.ProviderIdentity);
        StringAssert.Contains(diagnostic, "pylon");
    }

    /// <summary>运行时已部署时不产生诊断，避免把正常状态误报成故障。</summary>
    [TestMethod]
    public void DriverModuleHealth_WithRuntime_ReportsAvailable()
    {
        var module = new BaslerAcquisitionDriverModule(() => true);

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
        var missing = typeof(BaslerAcquisitionDriverModule).Assembly
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

    private static VisionAcquisitionTypeRegistration Collect(BaslerAcquisitionDriverModule module)
    {
        var builder = new CollectingBuilder();
        module.Contribute(builder);
        return builder.Registrations.Single();
    }

    /// <summary>
    /// 把插件输出目录的文件复制到临时目录：证明发现不依赖任何Manifest，
    /// 同时保证厂商依赖程序集（pylon托管程序集）可被解析。
    /// </summary>
    private static string StageDriverDirectory()
    {
        var target = Path.Combine(Path.GetTempPath(), "dp-vision-basler-driver-" + Guid.NewGuid().ToString("N"));
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
