using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 插件目录加载回归（实施基线§7「Provider组合与加载」与§13阶段D验收）。
/// 这些测试证明：宿主只认识 <c>plugin.json</c> 与中立插件契约，不安装某个Provider插件时其他流程照常工作，
/// 而"已安装但不可用"必须给出Provider级诊断而不是静默降级。
/// </summary>
[TestClass]
public sealed class VisionAcquisitionProviderPluginLoaderTests
{
    private readonly VisionAcquisitionProviderPluginLoader _loader = new VisionAcquisitionProviderPluginLoader();
    private readonly List<string> _roots = new List<string>();

    /// <summary>清理本次测试创建的临时插件目录。</summary>
    [TestCleanup]
    public void Cleanup()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        _roots.Clear();
    }

    /// <summary>插件目录不存在时只报告失败，不抛出，也不假装加载成功。</summary>
    [TestMethod]
    public void MissingPluginDirectory_IsReportedAsFailure()
    {
        var result = _loader.Load(Path.Combine(Path.GetTempPath(), "dp-vision-missing-" + Guid.NewGuid().ToString("N")));

        Assert.AreEqual(0, result.Plugins.Count);
        Assert.AreEqual(1, result.Failures.Count);
        StringAssert.Contains(result.Failures[0].Reason, "插件目录不存在");
    }

    /// <summary>插件目录没有某个Provider时，其他Provider的逻辑源照常组合——不安装HALCON不影响非HALCON流程。</summary>
    [TestMethod]
    public void AbsentProvider_DoesNotBlockOtherProviders()
    {
        var root = CreateRoot();
        StageTestPlugin(root);

        var result = _loader.Load(root);
        Assert.AreEqual(0, result.Failures.Count, Describe(result));

        var composition = new VisionAcquisitionProviderComposer().Compose(
            result.Modules,
            new[]
            {
                new VisionAcquisitionSourceBinding(
                    "Camera.Test",
                    ConfigurableAcquisitionProviderPlugin.ProviderIdentity,
                    "test-binding",
                    "camera:serial:TEST")
            });

        Assert.AreEqual(1, composition.Sources.Count);
        Assert.IsFalse(
            result.ProviderAvailability.Any(item => item.ProviderId == "dp.vision.halcon"),
            "未安装的Provider不应出现在可用性视图里。");
    }

    /// <summary>加载真实插件程序集，并报告Manifest身份与该Module实际贡献的Provider身份。</summary>
    [TestMethod]
    public void PluginAssembly_IsLoadedAndProviderIdsAreCollected()
    {
        var root = CreateRoot();
        StageTestPlugin(root);

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        var plugin = result.Plugins.Single();
        Assert.AreEqual(ConfigurableAcquisitionProviderPlugin.PluginIdentity, plugin.PluginId);
        Assert.AreEqual("1.0.0", plugin.Version);
        Assert.AreEqual("测试采集 Provider", plugin.DisplayName);
        CollectionAssert.AreEqual(
            new[] { ConfigurableAcquisitionProviderPlugin.ProviderIdentity },
            plugin.ProviderIds.ToArray());
        Assert.AreEqual(1, result.Modules.Count);
    }

    /// <summary>插件私有配置被原样转交：公共层既不解析也不改写，因此这里用一段不是JSON的文本也能通过。</summary>
    [TestMethod]
    public void PrivateConfiguration_IsPassedThroughVerbatim()
    {
        var root = CreateRoot();
        StageTestPlugin(root);
        const string configuration = "opaque provider-private text; not json";

        var result = _loader.Load(root, _ => configuration);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        Assert.AreEqual(
            ConfigurableAcquisitionProviderPlugin.ModuleIdentity
                + ConfigurableAcquisitionProviderPlugin.ConfigurationSeparator
                + configuration,
            result.Modules.Single().ExtensionId);
    }

    /// <summary>Provider已安装但当前不可用时，可用性视图必须给出Provider级诊断，且不算作加载失败。</summary>
    [TestMethod]
    public void UnavailableProvider_IsReportedWithProviderLevelDiagnostic()
    {
        var root = CreateRoot();
        StageTestPlugin(root);

        var result = _loader.Load(root, _ => "unavailable");

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        var availability = result.ProviderAvailability
            .Single(item => item.ProviderId == ConfigurableAcquisitionProviderPlugin.ProviderIdentity);
        Assert.IsFalse(availability.IsAvailable);
        StringAssert.Contains(availability.Diagnostic, ConfigurableAcquisitionProviderPlugin.ProviderIdentity);
    }

    /// <summary>插件不报告健康状态时按可用处理，避免"没实现可选能力"被误判成故障。</summary>
    [TestMethod]
    public void HealthyProvider_IsReportedAvailableWithoutDiagnostic()
    {
        var root = CreateRoot();
        StageTestPlugin(root);

        var result = _loader.Load(root);

        var availability = result.ProviderAvailability.Single();
        Assert.IsTrue(availability.IsAvailable);
        Assert.IsNull(availability.Diagnostic);
        Assert.AreEqual(ConfigurableAcquisitionProviderPlugin.PluginIdentity, availability.PluginId);
    }

    /// <summary>一个插件包失败不阻断同目录其他插件，但失败必须被报告。</summary>
    [TestMethod]
    public void BrokenPackage_DoesNotBlockSiblingPackage()
    {
        var root = CreateRoot();
        StageTestPlugin(root);
        WriteManifest(Path.Combine(root, "broken"), "dp.vision.broken", "missing.dll");

        var result = _loader.Load(root);

        Assert.AreEqual(1, result.Plugins.Count);
        Assert.AreEqual(1, result.Failures.Count);
        StringAssert.Contains(result.Failures[0].Reason, "插件程序集不存在");
    }

    /// <summary>Manifest不是有效JSON时拒绝该插件。</summary>
    [TestMethod]
    public void InvalidManifestJson_IsRejected()
    {
        var package = Path.Combine(CreateRoot(), "invalid");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "plugin.json"), "{ not json");

        var result = _loader.Load(Path.GetDirectoryName(package)!);

        Assert.AreEqual(0, result.Plugins.Count);
        Assert.AreEqual(1, result.Failures.Count);
        StringAssert.Contains(result.Failures[0].Reason, "不是有效JSON");
    }

    /// <summary>Manifest契约版本不受支持时拒绝该插件，避免用新契约的字段被旧宿主静默忽略。</summary>
    [TestMethod]
    public void UnsupportedManifestVersion_IsRejected()
    {
        var root = CreateRoot();
        StageTestPlugin(root, manifestVersion: 99);

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Plugins.Count);
        Assert.AreEqual(1, result.Failures.Count);
        StringAssert.Contains(result.Failures[0].Reason, "不受支持的契约版本");
    }

    /// <summary>程序集路径不得离开插件包目录，否则插件可以借Manifest加载任意路径的程序集。</summary>
    [TestMethod]
    public void AssemblyPathOutsidePackage_IsRejected()
    {
        var root = CreateRoot();
        StageTestPlugin(root, assemblyFile: Path.Combine("..", "outside.dll"));

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Plugins.Count);
        Assert.AreEqual(1, result.Failures.Count);
        StringAssert.Contains(result.Failures[0].Reason, "不能离开插件包目录");
    }

    /// <summary>插件报告的身份与Manifest声明不一致时拒绝加载，避免身份漂移。</summary>
    [TestMethod]
    public void PluginIdentityMismatch_IsRejected()
    {
        var root = CreateRoot();
        StageTestPlugin(root, pluginId: "dp.vision.other");

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Plugins.Count);
        Assert.AreEqual(1, result.Failures.Count);
        StringAssert.Contains(result.Failures[0].Reason, "不一致");
    }

    /// <summary>声明了采集分组但程序集里没有插件入口时拒绝，避免"看起来装上了其实没入口"。</summary>
    [TestMethod]
    public void GroupWithoutPluginEntry_IsRejected()
    {
        var root = CreateRoot();
        var package = Path.Combine(root, "no-entry");
        Directory.CreateDirectory(package);
        var assemblyName = Path.GetFileName(typeof(VisionSourceReference).Assembly.Location);
        File.Copy(typeof(VisionSourceReference).Assembly.Location, Path.Combine(package, assemblyName), overwrite: true);
        WriteManifest(package, "dp.vision.noentry", assemblyName);

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Plugins.Count);
        Assert.AreEqual(1, result.Failures.Count);
        StringAssert.Contains(result.Failures[0].Reason, "没有实现");
    }

    /// <summary>只声明其他宿主分组的插件包不产生采集插件，也不算失败——同一个plugin.json可以服务多类宿主。</summary>
    [TestMethod]
    public void PackageWithoutAcquisitionGroup_IsIgnoredWithoutFailure()
    {
        var root = CreateRoot();
        var package = Path.Combine(root, "workflow-only");
        Directory.CreateDirectory(package);
        File.WriteAllText(
            Path.Combine(package, "plugin.json"),
            "{\"manifestVersion\":1,\"pluginId\":\"dp.workflow.only\",\"version\":\"1.0.0\","
            + "\"modules\":{\"runtime\":[\"DP.WorkFlow.Some.dll\"]}}");

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Plugins.Count);
        Assert.AreEqual(0, result.Failures.Count);
    }

    private string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dp-vision-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static void StageTestPlugin(
        string root,
        string pluginId = ConfigurableAcquisitionProviderPlugin.PluginIdentity,
        int manifestVersion = 1,
        string? assemblyFile = null)
    {
        var package = Path.Combine(root, "dp.vision.test");
        Directory.CreateDirectory(package);
        var assemblyName = Path.GetFileName(typeof(ConfigurableAcquisitionProviderPlugin).Assembly.Location);
        File.Copy(
            typeof(ConfigurableAcquisitionProviderPlugin).Assembly.Location,
            Path.Combine(package, assemblyName),
            overwrite: true);
        WriteManifest(package, pluginId, assemblyFile ?? assemblyName, manifestVersion);
    }

    private static void WriteManifest(
        string package,
        string pluginId,
        string assemblyFile,
        int manifestVersion = 1)
    {
        Directory.CreateDirectory(package);
        File.WriteAllText(
            Path.Combine(package, "plugin.json"),
            "{\"manifestVersion\":" + manifestVersion + ","
            + "\"pluginId\":\"" + pluginId + "\","
            + "\"version\":\"1.0.0\","
            + "\"displayName\":\"测试采集 Provider\","
            + "\"modules\":{\"visionAcquisition\":[\"" + assemblyFile.Replace("\\", "\\\\") + "\"]}}");
    }

    private static string Describe(VisionAcquisitionProviderPluginLoadResult result) =>
        string.Join("；", result.Failures.Select(failure => failure.ManifestPath + " -> " + failure.Reason));
}
