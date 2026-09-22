using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 工程级依赖边界：测的是**声明**方向，不是编译产物方向。
/// <para>
/// 为什么必须单独测声明：Roslyn 只为**实际用到**的引用写出 <c>AssemblyRef</c>，
/// 所以"加了 <c>ProjectReference</c> 却没用到"在程序集引用清单里完全看不见——
/// 但它照样会把对方整条依赖链拷进每个消费者的输出目录。实测过：把
/// <c>DP.Vision.Acquisition.Runtime</c> 的引用加回 <c>DP.Vision.UI.csproj</c>，
/// 基于程序集引用的断言**全绿**（两个 TFM 各 1206 例 0 失败）。
/// </para>
/// <para>
/// 因此这里直接读 <c>.csproj</c> **全文**而不是只看 <c>ProjectReference</c> 元素，
/// 这样 <c>&lt;Reference HintPath=…&gt;</c> / <c>PackageReference</c> 之类绕道也拦得住。
/// </para>
/// </summary>
[TestClass]
public sealed class SolutionDependencyBoundaryTests
{
    /// <summary>被禁止的依赖声明：工程文件 → 不得出现的片段 → 理由。</summary>
    private static readonly (string Project, string Forbidden, string Reason)[] Rules =
    {
        (
            "src/DP.Vision.UI/DP.Vision.UI.csproj",
            "DP.Vision.Acquisition",
            "平台中立的 UI 套件（画布 / ROI / 结果浏览器）不得依赖采集层；"
            + "否则只想画图的宿主会被拖上整条采集运行时。"),
        (
            "src/DP.Vision.Acquisition.Management/DP.Vision.Acquisition.Management.csproj",
            "DP.Vision.UI",
            "采集管理层是采集运行时之上的编排视图，不得反过来依赖宿主侧的 UI 套件。"),
        (
            "src/DP.Vision.Acquisition.WinForms/DP.Vision.Acquisition.WinForms.csproj",
            "DP.Vision.UI",
            "采集会话视图只依赖采集管理层，不依赖平台中立 UI 套件。"),
        (
            "src/DP.Vision.Acquisition.WinForms/DP.Vision.Acquisition.WinForms.csproj",
            "DP.WorkFlow",
            "DP.Vision 不认识 Workflow。")
    };

    /// <summary>上述工程文件的依赖声明里不得出现被禁片段。</summary>
    [TestMethod]
    public void ProjectFiles_DoNotDeclareForbiddenDependencies()
    {
        var root = FindRepositoryRoot();
        foreach (var rule in Rules)
        {
            var path = Path.Combine(root, rule.Project.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), "找不到工程文件：" + path);

            var text = File.ReadAllText(path);

            // 反例保护：确认真的读到了工程文件内容，否则"零命中"可能只是读了个空文件。
            Assert.IsTrue(
                text.IndexOf("<ProjectReference", StringComparison.Ordinal) >= 0,
                "工程文件里没有任何 ProjectReference，扫描可能失效：" + path);

            Assert.IsTrue(
                text.IndexOf(rule.Forbidden, StringComparison.Ordinal) < 0,
                rule.Project + " 的依赖声明里出现了 " + rule.Forbidden + "：" + rule.Reason);
        }
    }

    /// <summary>从测试输出目录向上找到含 <c>DP.Vision.sln</c> 的仓库根。</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DP.Vision.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "从 " + AppContext.BaseDirectory + " 向上找不到 DP.Vision.sln，无法定位工程文件。");
    }
}
