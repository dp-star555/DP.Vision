using System;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// HALCON作为正式采集Provider插件的回归（实施基线§7与§13阶段D验收）：
/// 插件目录只靠 <c>plugin.json</c> 与中立插件契约发现HALCON；缺SDK时给出Provider级诊断；
/// 设备字段统一由机器配置的 <c>deviceSettings</c> 提供，插件私有配置不再承载设备绑定。
/// </summary>
[TestClass]
public sealed class HalconAcquisitionProviderPluginTests
{
    private static readonly string PluginDirectory =
        Path.GetDirectoryName(typeof(HalconAcquisitionProviderPlugin).Assembly.Location)!;

    /// <summary>随程序集发布的Manifest必须与插件入口身份一致，避免插件包与代码漂移。</summary>
    [TestMethod]
    public void ShippedManifest_MatchesShippedPlugin()
    {
        var path = Path.Combine(PluginDirectory, "plugin.json");
        Assert.IsTrue(File.Exists(path), "插件Manifest必须随程序集输出：" + path);

        var manifest = VisionAcquisitionProviderManifest.Read(path);

        Assert.AreEqual(VisionAcquisitionProviderManifest.CurrentManifestVersion, manifest.ManifestVersion);
        Assert.AreEqual(HalconAcquisitionProviderPlugin.PluginIdentity, manifest.PluginId);
        Assert.AreEqual(HalconAcquisitionProviderPlugin.PluginVersion, manifest.Version);
        Assert.IsTrue(
            manifest.Modules.TryGetValue(VisionAcquisitionProviderModuleGroups.VisionAcquisition, out var assemblies),
            "Manifest必须把HALCON声明到 visionAcquisition 分组。");
        CollectionAssert.Contains(
            assemblies,
            Path.GetFileName(typeof(HalconAcquisitionProviderPlugin).Assembly.Location));
    }

    /// <summary>宿主只扫描插件目录就能加载HALCON，编译期不需要选择具体Provider。</summary>
    [TestMethod]
    public void PluginDirectory_LoadsHalconProvider()
    {
        var result = new VisionAcquisitionProviderPluginLoader().Load(PluginDirectory);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        var plugin = result.Plugins.Single(item => item.PluginId == HalconAcquisitionProviderPlugin.PluginIdentity);
        CollectionAssert.Contains(plugin.ProviderIds.ToArray(), HalconAcquisitionProvider.ProviderIdentity);
        Assert.AreEqual(HalconAcquisitionProviderPlugin.PluginVersion, plugin.Version);
    }

    /// <summary>宿主只给出 plugins 根目录时，子目录里的插件包同样能被发现——与示例的投放布局一致。</summary>
    /// <remarks>
    /// 暂存目录放在测试输出目录下而不是 %TEMP%：.NET Framework 会锁定已加载的程序集，
    /// 临时目录里的副本删不掉，用固定路径可以让重复运行不产生无限增长。
    /// </remarks>
    [TestMethod]
    public void PluginPackageInSubdirectory_IsDiscoveredFromRoot()
    {
        var root = Path.Combine(PluginDirectory, "plugin-staging");
        var package = Path.Combine(root, "dp.vision.halcon");
        Directory.CreateDirectory(package);
        var assemblyName = Path.GetFileName(typeof(HalconAcquisitionProviderPlugin).Assembly.Location);
        File.Copy(Path.Combine(PluginDirectory, assemblyName), Path.Combine(package, assemblyName), overwrite: true);
        File.Copy(Path.Combine(PluginDirectory, "plugin.json"), Path.Combine(package, "plugin.json"), overwrite: true);

        var result = new VisionAcquisitionProviderPluginLoader().Load(root);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        CollectionAssert.Contains(
            result.Plugins.Single().ProviderIds.ToArray(),
            HalconAcquisitionProvider.ProviderIdentity);
    }

    /// <summary>SDK是否部署必须通过Provider级诊断暴露，而不是等到采集时才失败。</summary>
    [TestMethod]
    public void PluginHealth_ReportsSdkAvailability()
    {
        var result = new VisionAcquisitionProviderPluginLoader().Load(PluginDirectory);
        var availability = result.ProviderAvailability
            .Single(item => item.ProviderId == HalconAcquisitionProvider.ProviderIdentity);

        Assert.AreEqual(HalconStreamCameras.IsSdkEnabled, availability.IsAvailable);
        if (HalconStreamCameras.IsSdkEnabled)
            Assert.IsNull(availability.Diagnostic);
        else
            StringAssert.Contains(availability.Diagnostic, HalconAcquisitionProvider.ProviderIdentity);
    }

    /// <summary>SDK缺失时必须给出带Provider身份的诊断；注入探测结果以便在装有SDK的机器上也能覆盖该分支。</summary>
    [TestMethod]
    public void PluginHealth_WithoutSdk_ReportsProviderDiagnostic()
    {
        var plugin = new HalconAcquisitionProviderPlugin(() => false);

        Assert.IsFalse(plugin.TryGetHealth(out var diagnostic));
        StringAssert.Contains(diagnostic, HalconAcquisitionProvider.ProviderIdentity);
        StringAssert.Contains(diagnostic, "HALCON SDK");
    }

    /// <summary>SDK已部署时不产生诊断，避免把正常状态误报成故障。</summary>
    [TestMethod]
    public void PluginHealth_WithSdk_ReportsAvailable()
    {
        var plugin = new HalconAcquisitionProviderPlugin(() => true);

        Assert.IsTrue(plugin.TryGetHealth(out var diagnostic));
        Assert.IsNull(diagnostic);
    }

    /// <summary>无私有配置时Module照常贡献Provider注册，组合不需要SDK在场。</summary>
    [TestMethod]
    public void ModuleWithoutPrivateConfiguration_ComposesIntoPublishedSources()
    {
        var composition = new VisionAcquisitionProviderComposer().Compose(
            new IVisionAcquisitionProviderModule[] { new HalconAcquisitionProviderModule() },
            new[]
            {
                new VisionAcquisitionSourceBinding(
                    "Camera.Top",
                    HalconAcquisitionProvider.ProviderIdentity,
                    "GigEVision2|cam-top",
                    "halcon:camera:GigEVision2|cam-top")
            });

        Assert.AreEqual(1, composition.Sources.Count);
        Assert.IsTrue(composition.TryGetProvider(HalconAcquisitionProvider.ProviderIdentity, out _));
    }

    /// <summary>
    /// 非空的插件私有配置必须被拒绝：设备字段已经统一到 deviceSettings。
    /// 静默忽略遗留配置会把"配置没生效"藏起来，那比直接失败更难查。
    /// </summary>
    /// <param name="configuration">遗留的私有配置文本。</param>
    [TestMethod]
    [DataRow("{\"bindings\":{\"top-camera\":{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\"}}}")]
    [DataRow("{}")]
    [DataRow("not json at all")]
    public void PluginEntry_RejectsLegacyPrivateConfiguration(string configuration)
    {
        var plugin = new HalconAcquisitionProviderPlugin();

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => plugin.CreateModule(configuration));

        StringAssert.Contains(failure.Message, "deviceSettings");
    }

    /// <summary>空或空白配置表示"没有私有配置"，这是合法状态，Module 照常创建。</summary>
    /// <param name="configuration">空的私有配置。</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void PluginEntry_AcceptsEmptyPrivateConfiguration(string? configuration)
    {
        var plugin = new HalconAcquisitionProviderPlugin();

        var module = Assert.IsInstanceOfType<HalconAcquisitionProviderModule>(plugin.CreateModule(configuration));

        Assert.AreEqual(HalconAcquisitionProviderModule.ModuleIdentity, module.ExtensionId);
    }

    /// <summary>
    /// 插件程序集所在目录必须自包含它的厂商依赖。
    /// 加载器用 <c>Assembly.GetExportedTypes()</c> 发现入口，依赖解析不到就会整包加载失败；
    /// 部署脚本按"整个输出目录"投放插件包，所以这里守住"输出目录里有厂商程序集"这一条。
    /// 宿主提供的契约程序集（<c>DP.Vision</c>、<c>DP.Vision.Acquisition.Abstractions</c>）不在检查范围：
    /// 它们必须来自宿主，放进插件包反而会让插件拿到第二份契约类型，与宿主的中立接口不是同一个类型。
    /// </summary>
    [TestMethod]
    public void PluginDirectory_ContainsVendorDependencies()
    {
        var missing = typeof(HalconAcquisitionProviderPlugin).Assembly
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

    private static string Describe(VisionAcquisitionProviderPluginLoadResult result) =>
        string.Join("；", result.Failures.Select(failure => failure.ManifestPath + " -> " + failure.Reason));
}
