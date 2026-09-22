using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests.Architecture;

/// <summary>
/// 旧采集插件路径（Manifest + ProviderPlugin 入口 + ProviderModule）删除后的防复发回归。
/// </summary>
/// <remarks>
/// 采集发现现在只有一条路：<c>VisionAcquisitionDriverModuleLoader</c> 扫描插件目录里的托管程序集，
/// 找出实现 <c>IVisionAcquisitionDriverModule</c> 的公开类型，再由 <c>VisionAcquisitionTypeCatalogComposer</c>
/// 一次冻结成 Type Catalog。旧路径（读 <c>plugin.json</c> 的 Manifest → ProviderPlugin 入口 → ProviderModule）
/// 与它并列存在时会产生两类难查问题：同一份插件包被两套机制各发现一次，以及
/// "部署了插件却找不到" / "私有配置看起来生效、其实设备配置只认 deviceSettings"。
/// <para>
/// 这里用互相独立的三条证据守住删除结果：运行期类型不存在、生产源码里连标识符都不出现、
/// 插件包不再投放 Manifest。任一条单独都可能被绕过，三条同时成立才说明旧路径真的没了。
/// </para>
/// </remarks>
[TestClass]
public sealed class LegacyProviderPluginPathTests
{
    /// <summary>公共契约与组合层里已删除、不得重新出现的类型名。</summary>
    private static readonly string[] RemovedFrameworkTypeNames =
    {
        "IVisionAcquisitionProviderPlugin",
        "IVisionAcquisitionProviderHealth",
        "VisionAcquisitionProviderPluginLoader",
        "VisionAcquisitionProviderManifest",
        "VisionAcquisitionProviderModuleGroups",
        "VisionAcquisitionProviderPluginLoadResult",
        "VisionAcquisitionProviderPluginFailure",
        "VisionAcquisitionProviderPlugin",
        "VisionAcquisitionProviderAvailability",
    };

    /// <summary>厂商程序集里已删除的插件入口与 Module 类型名。</summary>
    private static readonly string[] RemovedVendorTypeNames =
    {
        "HalconAcquisitionProviderPlugin",
        "BaslerAcquisitionProviderPlugin",
        "HalconAcquisitionProviderModule",
        "BaslerAcquisitionProviderModule",
    };

    /// <summary>插件目录不得再投放 Manifest：目录发现不读它，留着只会让人以为它还在生效。</summary>
    private static readonly string[] VendorProjectDirectories = { "DP.Vision.Halcon", "DP.Vision.Basler" };

    /// <summary>公共契约程序集里不得再有旧插件类型。</summary>
    [TestMethod]
    public void RemovedPluginTypes_AreAbsentFromPublicContracts()
    {
        AssertTypeNamesAbsent(typeof(VisionSourceReference).Assembly, RemovedFrameworkTypeNames);
    }

    /// <summary>组合/运行时程序集里不得再有旧插件加载器与 Manifest 类型。</summary>
    [TestMethod]
    public void RemovedPluginTypes_AreAbsentFromRuntime()
    {
        AssertTypeNamesAbsent(typeof(VisionAcquisitionProviderComposition).Assembly, RemovedFrameworkTypeNames);
    }

    /// <summary>生产源码里不得再出现任何旧插件路径标识符。</summary>
    [TestMethod]
    public void RemovedPluginTypes_AreAbsentFromProductionSources()
    {
        var root = Path.Combine(FindVisionRoot(), "src");
        var sources = Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToArray();
        // 源码扫描必须真的扫到东西，否则这个断言永远为真。
        Assert.IsTrue(sources.Length > 100, $"只扫到 {sources.Length} 个生产源文件，扫描范围可能失效。");

        var names = RemovedFrameworkTypeNames.Concat(RemovedVendorTypeNames).ToArray();
        var hits = new List<string>();
        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            foreach (var name in names)
            {
                if (text.IndexOf(name, StringComparison.Ordinal) >= 0)
                    hits.Add(RelativeTo(root, path) + " -> " + name);
            }
        }

        Assert.AreEqual(0, hits.Count, "生产源码仍在引用旧采集插件路径：" + string.Join("; ", hits));
    }

    /// <summary>两个厂商插件工程都不再投放 <c>plugin.json</c>，也不再在工程文件里引用它。</summary>
    [TestMethod]
    public void PluginManifestFiles_AreNoLongerShipped()
    {
        var visionRoot = FindVisionRoot();
        var hits = new List<string>();
        foreach (var directory in VendorProjectDirectories)
        {
            var projectRoot = Path.Combine(visionRoot, "src", directory);
            Assert.IsTrue(Directory.Exists(projectRoot), $"找不到厂商工程目录：{projectRoot}");

            var manifest = Path.Combine(projectRoot, "plugin.json");
            if (File.Exists(manifest))
                hits.Add(RelativeTo(visionRoot, manifest));

            foreach (var project in Directory.GetFiles(projectRoot, "*.csproj"))
            {
                if (File.ReadAllText(project).IndexOf("plugin.json", StringComparison.OrdinalIgnoreCase) >= 0)
                    hits.Add(RelativeTo(visionRoot, project) + " -> plugin.json");
            }
        }

        Assert.AreEqual(0, hits.Count, "插件 Manifest 仍在投放或被工程引用：" + string.Join("; ", hits));
    }

    private static void AssertTypeNamesAbsent(System.Reflection.Assembly assembly, string[] removedNames)
    {
        var exported = assembly.GetExportedTypes();
        // 防止"扫描到 0 个类型也算通过"的假绿。
        Assert.IsTrue(exported.Length > 15, $"程序集 {assembly.GetName().Name} 只导出 {exported.Length} 个类型，扫描可能失效。");

        var survivors = exported
            .Where(type => removedNames.Contains(type.Name, StringComparer.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.AreEqual(
            0,
            survivors.Length,
            "程序集 " + assembly.GetName().Name + " 里旧采集插件路径重新出现：" + string.Join(",", survivors));
    }

    private static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>net48 没有 <c>Path.GetRelativePath</c>，这里手工截前缀。</summary>
    private static string RelativeTo(string root, string path)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : path;
    }

    /// <summary>从测试输出目录向上找到含 <c>src/DP.Vision.Acquisition.Runtime</c> 的仓库根。</summary>
    private static string FindVisionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DP.Vision.Acquisition.Runtime");
            if (Directory.Exists(candidate))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Cannot locate the DP.Vision repository root.");
    }
}
