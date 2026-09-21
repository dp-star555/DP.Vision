using System;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// Basler作为第二个真实采集Provider插件的回归（实施基线§13阶段E）：
/// 插件目录只靠 <c>plugin.json</c> 与中立插件契约发现Basler；缺pylon运行时给出Provider级诊断；
/// 私有配置由本Provider自己解析与校验，公共层不解释其中任何字段。
/// </summary>
[TestClass]
public sealed class BaslerAcquisitionProviderPluginTests
{
    private const string SampleConfiguration =
        "{\"bindings\":{\"top-camera\":{\"serialNumber\":\"40123456\"}}}";

    private static readonly string PluginDirectory =
        Path.GetDirectoryName(typeof(BaslerAcquisitionProviderPlugin).Assembly.Location)!;

    /// <summary>随程序集发布的Manifest必须与插件入口身份一致，避免插件包与代码漂移。</summary>
    [TestMethod]
    public void ShippedManifest_MatchesShippedPlugin()
    {
        var path = Path.Combine(PluginDirectory, "plugin.json");
        Assert.IsTrue(File.Exists(path), "插件Manifest必须随程序集输出：" + path);

        var manifest = VisionAcquisitionProviderManifest.Read(path);

        Assert.AreEqual(VisionAcquisitionProviderManifest.CurrentManifestVersion, manifest.ManifestVersion);
        Assert.AreEqual(BaslerAcquisitionProviderPlugin.PluginIdentity, manifest.PluginId);
        Assert.AreEqual(BaslerAcquisitionProviderPlugin.PluginVersion, manifest.Version);
        Assert.IsTrue(
            manifest.Modules.TryGetValue(VisionAcquisitionProviderModuleGroups.VisionAcquisition, out var assemblies),
            "Manifest必须把Basler声明到 visionAcquisition 分组。");
        CollectionAssert.Contains(
            assemblies,
            Path.GetFileName(typeof(BaslerAcquisitionProviderPlugin).Assembly.Location));
    }

    /// <summary>宿主只扫描插件目录就能加载Basler，编译期不需要选择具体Provider。</summary>
    [TestMethod]
    public void PluginDirectory_LoadsBaslerProvider()
    {
        var result = new VisionAcquisitionProviderPluginLoader().Load(PluginDirectory, _ => SampleConfiguration);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        var plugin = result.Plugins.Single(item => item.PluginId == BaslerAcquisitionProviderPlugin.PluginIdentity);
        CollectionAssert.Contains(plugin.ProviderIds.ToArray(), BaslerAcquisitionProvider.ProviderIdentity);
        Assert.AreEqual(BaslerAcquisitionProviderPlugin.PluginVersion, plugin.Version);
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
        var package = Path.Combine(root, "dp.vision.basler");
        Directory.CreateDirectory(package);
        var assemblyName = Path.GetFileName(typeof(BaslerAcquisitionProviderPlugin).Assembly.Location);
        File.Copy(Path.Combine(PluginDirectory, assemblyName), Path.Combine(package, assemblyName), overwrite: true);
        File.Copy(Path.Combine(PluginDirectory, "plugin.json"), Path.Combine(package, "plugin.json"), overwrite: true);

        var result = new VisionAcquisitionProviderPluginLoader().Load(root, _ => SampleConfiguration);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        CollectionAssert.Contains(
            result.Plugins.Single().ProviderIds.ToArray(),
            BaslerAcquisitionProvider.ProviderIdentity);
    }

    /// <summary>运行时是否部署必须通过Provider级诊断暴露，而不是等到采集时抛原生异常。</summary>
    [TestMethod]
    public void PluginHealth_ReportsRuntimeAvailability()
    {
        var result = new VisionAcquisitionProviderPluginLoader().Load(PluginDirectory);
        var availability = result.ProviderAvailability
            .Single(item => item.ProviderId == BaslerAcquisitionProvider.ProviderIdentity);

        Assert.AreEqual(BaslerPylonRuntime.IsDeployed, availability.IsAvailable);
        if (BaslerPylonRuntime.IsDeployed)
            Assert.IsNull(availability.Diagnostic);
        else
            StringAssert.Contains(availability.Diagnostic, BaslerAcquisitionProvider.ProviderIdentity);
    }

    /// <summary>缺运行时必须给出带Provider身份的诊断；注入探测结果以便在装有pylon的机器上也能覆盖该分支。</summary>
    [TestMethod]
    public void PluginHealth_WithoutRuntime_ReportsProviderDiagnostic()
    {
        var plugin = new BaslerAcquisitionProviderPlugin(() => false);

        Assert.IsFalse(plugin.TryGetHealth(out var diagnostic));
        StringAssert.Contains(diagnostic, BaslerAcquisitionProvider.ProviderIdentity);
        StringAssert.Contains(diagnostic, BaslerPylonRuntime.NativeBaseLibrary);
    }

    /// <summary>运行时已部署时不产生诊断，避免把正常状态误报成故障。</summary>
    [TestMethod]
    public void PluginHealth_WithRuntime_ReportsAvailable()
    {
        var plugin = new BaslerAcquisitionProviderPlugin(() => true);

        Assert.IsTrue(plugin.TryGetHealth(out var diagnostic));
        Assert.IsNull(diagnostic);
    }

    /// <summary>插件私有配置加上公共Source绑定可以完成一次正式组合，全程不需要pylon运行时在场。</summary>
    [TestMethod]
    public void PrivateConfiguration_ComposesIntoPublishedSources()
    {
        var result = new VisionAcquisitionProviderPluginLoader().Load(PluginDirectory, _ => SampleConfiguration);

        var composition = new VisionAcquisitionProviderComposer().Compose(
            result.Modules,
            new[]
            {
                new VisionAcquisitionSourceBinding(
                    "Camera.Top",
                    BaslerAcquisitionProvider.ProviderIdentity,
                    "top-camera",
                    "camera:serial:40123456")
            });

        Assert.AreEqual(1, composition.Sources.Count);
        Assert.IsTrue(composition.TryGetProvider(BaslerAcquisitionProvider.ProviderIdentity, out _));
    }

    /// <summary>私有配置被解析为Provider私有绑定；这些字段不进入公共配置。</summary>
    [TestMethod]
    public void PrivateConfiguration_ParsesBindings()
    {
        var binding = BaslerProviderConfiguration.ParseBindings(SampleConfiguration).Single();

        Assert.AreEqual("top-camera", binding.BindingId);
        Assert.AreEqual("camera:serial:40123456", binding.CanonicalKey);
    }

    /// <summary>插件入口把私有配置交给Provider Module；缺少配置表示尚未配置设备而不是解析失败。</summary>
    [TestMethod]
    public void PluginEntry_CreatesModuleWithConfiguredBindings()
    {
        var plugin = new BaslerAcquisitionProviderPlugin();

        var module = (BaslerAcquisitionProviderModule)plugin.CreateModule(SampleConfiguration);
        Assert.AreEqual("top-camera", module.Bindings.Single().BindingId);
        Assert.AreEqual(0, ((BaslerAcquisitionProviderModule)plugin.CreateModule(null)).Bindings.Count);
    }

    /// <summary>非法私有配置必须在创建Module时就被拒绝，而不是得到一个半可用的Provider。</summary>
    [TestMethod]
    public void PluginEntry_RejectsInvalidPrivateConfiguration()
    {
        var plugin = new BaslerAcquisitionProviderPlugin();

        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => plugin.CreateModule("{\"unknown\":1}"));
    }

    /// <summary>私有配置校验必须拒绝未知字段、缺失字段、非法类型和重复绑定身份，避免拼写错误被静默忽略。</summary>
    /// <param name="configuration">待校验的私有配置文本。</param>
    /// <param name="expectedFragment">诊断中必须出现的关键片段。</param>
    [TestMethod]
    [DataRow("{\"binding\":{}}", "未知字段")]
    [DataRow("{\"bindings\":{\"top\":{\"serial\":\"40123456\"}}}", "未知字段")]
    [DataRow("{\"bindings\":{\"top\":{}}}", "只能指定")]
    [DataRow("{\"bindings\":{\"top\":{\"serialNumber\":\"1\",\"userDefinedName\":\"A\"}}}", "只能指定")]
    [DataRow("{\"bindings\":{\"top\":{\"serialNumber\":12}}}", "必须是字符串")]
    [DataRow("{\"bindings\":{\"top\":{\"serialNumber\":\"\"}}}", "不能为空")]
    [DataRow("{\"bindings\":{\"top\":{\"serialNumber\":\"1\"},\"top\":{\"serialNumber\":\"2\"}}}", "重复")]
    [DataRow("[1,2]", "必须是JSON对象")]
    [DataRow("{ not json", "不是有效JSON")]
    public void PrivateConfiguration_RejectsInvalidInput(string configuration, string expectedFragment)
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => BaslerProviderConfiguration.ParseBindings(configuration));

        StringAssert.Contains(failure.Message, expectedFragment);
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
        var missing = typeof(BaslerAcquisitionProviderPlugin).Assembly
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
