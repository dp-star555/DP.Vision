using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>
/// 旧采集抽象 <c>ICameraCapture</c> / <c>CameraCaptureOptions</c> 的清除回归。
/// </summary>
/// <remarks>
/// 采集已经统一到 <c>DP.Vision.Acquisition</c> 的中立契约与 Provider 插件体系，
/// 这两个类型既没有实现者也没有消费者，留在 <c>DP.Vision.Algorithms</c> 只会让人误以为
/// 还存在"绕过 Provider 直接抓一帧"的第二条采集路径。这里用两条互相独立的证据守住它：
/// 一是运行期类型不存在，二是生产源码里连标识符都不出现（防止有人只加回声明而未被引用）。
/// </remarks>
[TestClass]
public sealed class LegacyCaptureAbstractionTests
{
    /// <summary>已删除、不得重新出现的类型名。</summary>
    private static readonly string[] RemovedTypeNames = { "ICameraCapture", "CameraCaptureOptions" };

    /// <summary>生产程序集里不得再有这两个类型。</summary>
    [TestMethod]
    public void RemovedCaptureTypes_AreAbsentFromAlgorithmsAssembly()
    {
        var exported = typeof(EAlgorithmStatus).Assembly.GetExportedTypes();
        // 防止"扫描到 0 个类型也算通过"的假绿。
        Assert.IsTrue(exported.Length > 20, $"只导出 {exported.Length} 个类型，程序集扫描可能失效。");

        var survivors = exported
            .Where(type => RemovedTypeNames.Contains(type.Name, StringComparer.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.AreEqual(
            0,
            survivors.Length,
            "旧采集抽象重新出现：" + string.Join(",", survivors));
    }

    /// <summary>生产源码里不得再出现这两个标识符。</summary>
    [TestMethod]
    public void RemovedCaptureTypes_AreAbsentFromProductionSources()
    {
        var root = Path.Combine(FindVisionRoot(), "src");
        var sources = Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToArray();
        // 源码扫描必须真的扫到东西，否则这个断言永远为真。
        Assert.IsTrue(sources.Length > 100, $"只扫到 {sources.Length} 个生产源文件，扫描范围可能失效。");

        var hits = new List<string>();
        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            foreach (var name in RemovedTypeNames)
            {
                if (text.IndexOf(name, StringComparison.Ordinal) >= 0)
                    hits.Add(RelativeTo(root, path) + " -> " + name);
            }
        }

        Assert.AreEqual(0, hits.Count, "生产源码仍在引用旧采集抽象：" + string.Join("; ", hits));
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

    /// <summary>从测试输出目录向上找到含 <c>src/DP.Vision.Algorithms</c> 的仓库根。</summary>
    private static string FindVisionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DP.Vision.Algorithms");
            if (Directory.Exists(candidate))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Cannot locate the DP.Vision repository root.");
    }
}
