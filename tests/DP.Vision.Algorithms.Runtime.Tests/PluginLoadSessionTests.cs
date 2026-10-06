using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DP.Plugins;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
#if !NET48
using System.Runtime.Loader;
#endif

namespace DP.Vision.Algorithms.Runtime.Tests;

/// <summary>独立编译的包加载，不依赖测试宿主静态引用插件。</summary>
[TestClass]
public sealed class PluginLoadSessionTests
{
    /// <summary>同一模块多次发现不会重复构造，共享扩展契约跨包身份保持一致。</summary>
    [TestMethod]
    public async Task Discovery_SharedExternalContracts_AndPrivateDependencyVersions()
    {
        var root = Stage(); var session = new PluginLoadSession();
        var loader = new VisionAlgorithmModuleLoader(session); session.RegisterSharedContracts(root);
        var first = session.Discover<IVisionAlgorithmModule>(root);
        var repeated = session.Discover<IVisionAlgorithmModule>(root);
        Assert.AreSame(first.Modules.First(), repeated.Modules.First());
        Assert.IsFalse(typeof(PluginLoadSessionTests).Assembly.GetReferencedAssemblies().Any(a => a.Name == "Probe.Contracts" || a.Name == "EngineV1"));
        var catalog = loader.Load(root);
#if NET48
        Assert.AreEqual(1, catalog.Implementations.Count);
        Assert.IsTrue(catalog.Diagnostics.Any(d => d.Reason.Contains("统一依赖版本约束冲突")));
#else
        Assert.AreEqual(0, catalog.Diagnostics.Count);
        Assert.AreEqual(2, catalog.Implementations.Count);
        Assert.AreSame(catalog.Implementations[0].ContractType, catalog.Implementations[1].ContractType);
        Assert.AreNotSame(AssemblyLoadContext.GetLoadContext(first.Modules[0].GetType().Assembly), AssemblyLoadContext.GetLoadContext(first.Modules[1].GetType().Assembly));
        Assert.AreNotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(first.Modules[0].GetType().Assembly));
#endif
        using var runtime = new VisionAlgorithmRuntime(catalog);
        foreach (var descriptor in catalog.Implementations)
        {
            using var plan = await runtime.PrepareAsync(new[] { new VisionAlgorithmRequest("probe", descriptor.ContractType, new VisionAlgorithmSelection { ImplementationId = descriptor.ImplementationId }) });
            var value = plan.Invoke<object, int>("probe", instance => (int)descriptor.ContractType.GetMethod("Read")!.Invoke(instance, null)!);
            Assert.AreEqual(descriptor.ImplementationId.EndsWith("v1", StringComparison.Ordinal) ? 1 : 2, value);
        }
    }

    /// <summary>同身份不同文件内容不能靠加载顺序掩盖。</summary>
    [TestMethod]
    public void DuplicateIdentity_DifferentContent_IsDeploymentConflict()
    {
        var root = Stage(); var session = new PluginLoadSession();
        _ = new VisionAlgorithmModuleLoader(session); session.RegisterSharedContracts(root);
        var original = Path.Combine(root, "a", "EngineV1.dll");
        session.LoadAssembly(original);
        var duplicate = Path.Combine(root, "modified.dll"); File.Copy(original, duplicate);
        using (var stream = new FileStream(duplicate, FileMode.Append)) stream.WriteByte(0);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => session.LoadAssembly(duplicate));
        StringAssert.Contains(error.Message, "内容冲突");
    }

    /// <summary>内置入口所在契约被重复投放，不能使托管算法因重复 Module 消失。</summary>
    [TestMethod]
    public void SharedContractCopy_DoesNotDuplicateExplicitBuiltinModule()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "PluginTestRuns", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.Copy(typeof(IVisionAlgorithmModule).Assembly.Location, Path.Combine(root, "DP.Vision.Algorithms.dll"));
        var catalog = new VisionAlgorithmModuleLoader().Load(root, new[] { new ManagedVisionAlgorithmModule() });
        Assert.AreEqual(0, catalog.Diagnostics.Count); Assert.AreEqual(8, catalog.Implementations.Count);
        Assert.AreEqual(1, catalog.Implementations.Count(d => d.ImplementationId == "managed.geometry" && d.ContractType == typeof(IGeometryMeasurer)));
    }

    private static string Stage()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "DP.Vision.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var root = Path.Combine(AppContext.BaseDirectory, "PluginTestRuns", Guid.NewGuid().ToString("N"));
        Copy("Probe.Contracts", "netstandard2.0", "Probe.Contracts.dll", "contracts");
#if NET48
        const string framework = "net48";
#else
        const string framework = "net8.0";
#endif
        Copy("EngineV1", framework, "EngineV1.dll", "a"); Copy("PrivateV1", "netstandard2.0", "Fixture.PrivateDependency.dll", "a");
        Copy("EngineV2", framework, "EngineV2.dll", "b"); Copy("PrivateV2", "netstandard2.0", "Fixture.PrivateDependency.dll", "b");
        // 两个包同时携带契约副本，显式共享目录使它们仍复用同一份。
        Copy("Probe.Contracts", "netstandard2.0", "Probe.Contracts.dll", "a"); Copy("Probe.Contracts", "netstandard2.0", "Probe.Contracts.dll", "b");
        return root;
        void Copy(string project, string target, string file, string package)
        {
            var destination = Path.Combine(root, package); Directory.CreateDirectory(destination);
            File.Copy(Path.Combine(repo!.FullName, "tests", "PluginFixtures", project, "bin", configuration, target, file), Path.Combine(destination, file));
        }
    }
}
