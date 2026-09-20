using System;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// HALCON作为正式采集Provider插件的回归（实施基线§7与§13阶段D验收）：
/// 插件目录只靠 <c>plugin.json</c> 与中立插件契约发现HALCON；缺SDK时给出Provider级诊断；
/// 私有配置由本Provider自己解析与校验，公共层不解释其中任何字段。
/// </summary>
[TestClass]
public sealed class HalconAcquisitionProviderPluginTests
{
    private const string SampleConfiguration =
        "{\"bindings\":{\"top-camera\":{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\",\"serialNumber\":\"DEMO0001\"}}}";

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
        var result = new VisionAcquisitionProviderPluginLoader().Load(PluginDirectory, _ => SampleConfiguration);

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

        var result = new VisionAcquisitionProviderPluginLoader().Load(root, _ => SampleConfiguration);

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

        Assert.AreEqual(HalconCameraCapture.IsSdkEnabled, availability.IsAvailable);
        if (HalconCameraCapture.IsSdkEnabled)
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

    /// <summary>插件私有配置加上公共Source绑定可以完成一次正式组合，全程不需要SDK在场。</summary>
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
                    HalconAcquisitionProvider.ProviderIdentity,
                    "top-camera",
                    "camera:serial:DEMO0001")
            });

        Assert.AreEqual(1, composition.Sources.Count);
        Assert.IsTrue(composition.TryGetProvider(HalconAcquisitionProvider.ProviderIdentity, out _));
    }

    /// <summary>私有配置被解析为Provider私有绑定；这些字段不进入公共配置。</summary>
    [TestMethod]
    public void PrivateConfiguration_ParsesBindings()
    {
        var binding = HalconProviderConfiguration.ParseBindings(SampleConfiguration).Single();

        Assert.AreEqual("top-camera", binding.BindingId);
        Assert.AreEqual("GigEVision2|cam-top", binding.CameraId);
        Assert.AreEqual("camera:serial:DEMO0001", binding.CanonicalKey);
    }

    /// <summary>插件入口把私有配置交给Provider Module；缺少配置表示尚未配置设备而不是解析失败。</summary>
    [TestMethod]
    public void PluginEntry_CreatesModuleWithConfiguredBindings()
    {
        var plugin = new HalconAcquisitionProviderPlugin();

        var module = (HalconAcquisitionProviderModule)plugin.CreateModule(SampleConfiguration);
        Assert.AreEqual("top-camera", module.Bindings.Single().BindingId);
        Assert.AreEqual(0, ((HalconAcquisitionProviderModule)plugin.CreateModule(null)).Bindings.Count);
    }

    /// <summary>非法私有配置必须在创建Module时就被拒绝，而不是得到一个半可用的Provider。</summary>
    [TestMethod]
    public void PluginEntry_RejectsInvalidPrivateConfiguration()
    {
        var plugin = new HalconAcquisitionProviderPlugin();

        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => plugin.CreateModule("{\"unknown\":1}"));
    }

    /// <summary>私有配置校验必须拒绝未知字段、缺失字段和重复绑定身份，避免拼写错误被静默忽略。</summary>
    /// <param name="configuration">待校验的私有配置文本。</param>
    /// <param name="expectedFragment">诊断中必须出现的关键片段。</param>
    [TestMethod]
    [DataRow("{\"binding\":{}}", "未知字段")]
    [DataRow("{\"bindings\":{\"top\":{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam\",\"serial\":\"X\"}}}", "未知字段")]
    [DataRow("{\"bindings\":{\"top\":{\"deviceName\":\"cam\"}}}", "interfaceName")]
    [DataRow("{\"bindings\":{\"top\":{\"interfaceName\":\"GigEVision2\"}}}", "deviceName")]
    [DataRow("{\"bindings\":{\"top\":{\"interfaceName\":\"GigEVision2\",\"deviceName\":12}}}", "必须是字符串")]
    [DataRow("{\"bindings\":{\"top\":{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"\"}}}", "不能为空")]
    [DataRow("{\"bindings\":{\"top\":{\"interfaceName\":\"A\",\"deviceName\":\"B\"},\"top\":{\"interfaceName\":\"A\",\"deviceName\":\"C\"}}}", "重复")]
    [DataRow("[1,2]", "必须是JSON对象")]
    [DataRow("{ not json", "不是有效JSON")]
    public void PrivateConfiguration_RejectsInvalidInput(string configuration, string expectedFragment)
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => HalconProviderConfiguration.ParseBindings(configuration));

        StringAssert.Contains(failure.Message, expectedFragment);
    }

    private static string Describe(VisionAcquisitionProviderPluginLoadResult result) =>
        string.Join("；", result.Failures.Select(failure => failure.ManifestPath + " -> " + failure.Reason));
}
