using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// Driver Module自动发现回归（V2-1 §20与V2验收§21.1）。
/// 这些测试证明：扫描受信任插件目录中的DLL就能发现 <see cref="IVisionAcquisitionDriverModule"/>，
/// Manifest不是加载依据，机器相机配置不参与"列出已安装Type"。
/// </summary>
[TestClass]
public sealed class VisionAcquisitionDriverModuleLoaderTests
{
    private readonly VisionAcquisitionDriverModuleLoader _loader = new VisionAcquisitionDriverModuleLoader();
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

    /// <summary>插件目录不存在时只报告失败，不抛出，也不假装发现成功。</summary>
    [TestMethod]
    public void MissingPluginDirectory_IsReportedAsFailure()
    {
        var result = _loader.Load(Path.Combine(Path.GetTempPath(), "dp-vision-driver-missing-" + Guid.NewGuid().ToString("N")));

        Assert.AreEqual(0, result.Modules.Count);
        Assert.AreEqual(1, result.Failures.Count);
        StringAssert.Contains(result.Failures[0].Reason, "插件目录不存在");
    }

    /// <summary>不写Manifest也能发现Driver Module：加载依据是接口扫描，不是plugin.json。</summary>
    [TestMethod]
    public void DriverModuleDll_IsDiscoveredWithoutManifest()
    {
        var root = CreateRoot();
        StageTestAssembly(root);

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        CollectionAssert.Contains(
            result.Modules.Select(module => module.ExtensionId).ToArray(),
            ConfigurableAcquisitionDriverModule.ModuleIdentity);
    }

    /// <summary>依赖DLL（托管但没有Driver Module）被静默跳过，不算失败也不算Module。</summary>
    [TestMethod]
    public void DependencyDll_WithoutDriverModule_IsIgnored()
    {
        var root = CreateRoot();
        StageTestAssembly(root);
        File.Copy(
            typeof(VisionSourceReference).Assembly.Location,
            Path.Combine(root, "DP.Vision.Acquisition.Abstractions.dll"),
            overwrite: true);

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        Assert.AreEqual(2, result.Modules.Count);
    }

    /// <summary>原生SDK依赖DLL（非托管）被静默跳过，不产生失败噪声。</summary>
    [TestMethod]
    public void NativeDll_IsSkippedSilently()
    {
        var root = CreateRoot();
        StageTestAssembly(root);
        File.WriteAllText(
            Path.Combine(root, "vendor-native.dll"),
            "MZ this is not a managed assembly, just a fake native blob.");

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        Assert.AreEqual(2, result.Modules.Count);
    }

    /// <summary>扫描结果顺序确定：同一目录重复扫描得到相同的Module顺序，并按ExtensionId排序。</summary>
    [TestMethod]
    public void DiscoveryOrder_IsDeterministic()
    {
        var root = CreateRoot();
        StageTestAssembly(root);

        var first = _loader.Load(root);
        var second = _loader.Load(root);

        Assert.AreEqual(0, first.Failures.Count, Describe(first));
        CollectionAssert.AreEqual(
            first.Modules.Select(module => module.ExtensionId).ToArray(),
            second.Modules.Select(module => module.ExtensionId).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ConfigurableAcquisitionDriverModule.ModuleIdentity,
                SecondAcquisitionDriverModule.ModuleIdentity
            },
            first.Modules.Select(module => module.ExtensionId).ToArray());
    }

    /// <summary>
    /// 部署约定把整个输出目录当作插件包，因此插件根目录里既有宿主提供的契约程序集，
    /// 也可能存在被复制到子目录的同身份插件副本；这两者都不得让加载器再装第二份。
    /// .NET Framework 的 <c>LoadFrom</c> 会把副本装进另一个加载上下文，入口类型实现的是另一份契约接口，
    /// <c>IsAssignableFrom</c> 静默为 false，模块被漏掉（§21.1 的"自动发现"在 net48 下失效）。
    /// </summary>
    [TestMethod]
    public void ContractCopyAndSubdirectoryCopy_DoNotHideDriverModules()
    {
        var root = CreateRoot();
        StageTestAssembly(Path.Combine(root, "dp.vision.plugin"));
        File.Copy(
            typeof(VisionSourceReference).Assembly.Location,
            Path.Combine(root, "DP.Vision.Acquisition.Abstractions.dll"),
            overwrite: true);

        var result = _loader.Load(root);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        CollectionAssert.Contains(
            result.Modules.Select(module => module.ExtensionId).ToArray(),
            ConfigurableAcquisitionDriverModule.ModuleIdentity);
    }

    /// <summary>V2-1完成条件：不读取机器相机配置，也能列出已安装AcquisitionType并冻结Catalog。</summary>
    [TestMethod]
    public void CatalogTypes_AreListableWithoutMachineConfiguration()
    {
        var root = CreateRoot();
        StageTestAssembly(root);

        var result = _loader.Load(root);
        var catalog = new VisionAcquisitionTypeCatalogComposer().Compose(result.Modules);

        Assert.AreEqual(0, result.Failures.Count, Describe(result));
        Assert.IsTrue(catalog.TryGetType(ConfigurableAcquisitionDriverModule.AreaScanTypeId, out var area));
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, area!.Kind);
        Assert.IsTrue(catalog.TryGetType(ConfigurableAcquisitionDriverModule.LineScanTestTypeId, out var line));
        Assert.AreEqual(EVisionAcquisitionKind.LineScan, line!.Kind);
        CollectionAssert.AreEqual(
            new[]
            {
                ConfigurableAcquisitionDriverModule.AreaScanTypeId,
                ConfigurableAcquisitionDriverModule.LineScanTestTypeId,
                SecondAcquisitionDriverModule.AreaScanTypeId
            },
            catalog.Types.Select(item => item.AcquisitionTypeId).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ConfigurableAcquisitionDriverModule.AreaScanTypeId,
                SecondAcquisitionDriverModule.AreaScanTypeId
            },
            catalog.GetByKind(EVisionAcquisitionKind.AreaScan).Select(item => item.AcquisitionTypeId).ToArray());
    }

    private string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dp-vision-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static void StageTestAssembly(string directory)
    {
        Directory.CreateDirectory(directory);
        var assemblyName = Path.GetFileName(typeof(ConfigurableAcquisitionDriverModule).Assembly.Location);
        File.Copy(
            typeof(ConfigurableAcquisitionDriverModule).Assembly.Location,
            Path.Combine(directory, assemblyName),
            overwrite: true);
    }

    private static string Describe(VisionAcquisitionDriverModuleLoadResult result) =>
        string.Join("；", result.Failures.Select(failure => failure.AssemblyPath + " -> " + failure.Reason));
}
