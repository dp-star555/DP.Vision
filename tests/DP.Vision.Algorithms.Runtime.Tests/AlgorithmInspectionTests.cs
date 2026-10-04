using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Runtime.Tests;

/// <summary>静态检查、资源引用、结构化失败与显式升级。</summary>
[TestClass]
public sealed class AlgorithmInspectionTests
{
    private sealed class Counter : ICounter { public int Number => 8; }
    private sealed class Module(VisionAlgorithmDescriptor descriptor) : IVisionAlgorithmModule
    { public string ExtensionId => "test.inspection"; public void Register(IVisionAlgorithmRegistration registrations) => registrations.Add(descriptor); }
    private static VisionAlgorithmCatalog Catalog(IVisionAlgorithmFactory factory, params VisionAlgorithmParameter[] parameters) =>
        VisionAlgorithmCatalog.Compose(new[] { new Module(new VisionAlgorithmDescriptor("test.counter", "Test", "1", factory, parameters: parameters)) });
    private static VisionAlgorithmSelection Selection(string id = "test.counter") => new VisionAlgorithmSelection { ImplementationId = id };
    private static VisionAlgorithmRequest Request(string key, VisionAlgorithmSelection selection) => new VisionAlgorithmRequest(key, typeof(ICounter), selection);

    /// <summary>静态检查汇总独立错误且不创建资源。</summary>
    [TestMethod]
    public async Task Inspection_AggregatesNodeAndDependencyIssues_WithoutCreatingAnything()
    {
        var factory = new VisionAlgorithmFactory<ICounter>((_, _, _) => throw new AssertFailedException("静态失败不应初始化"),
            _ => new[] { new VisionAlgorithmDependency("model", typeof(ICounter)) })
            .WithConfigurationPolicy(c => VisionAlgorithmConfigurationRules.ValidateVersionOne(c, new[] { "scale" }));
        var catalog = Catalog(factory, new VisionAlgorithmParameter("scale", "缩放", typeof(double), minimum: .5, maximum: 3));
        var selection = Selection(); selection.Settings["scale"] = "NaN"; selection.Settings["obsolete"] = "x";
        selection.Dependencies["unused"] = Selection();
        var requests = new[] { Request("node/first", Selection("missing")), Request("node/second", selection) };
        var report = new VisionAlgorithmInspection(catalog).Analyze(requests);
        CollectionAssert.AreEquivalent(new[] { "ALG_IMPLEMENTATION_MISSING", "ALG_CONFIGURATION_INVALID", "ALG_PARAMETER_INVALID", "ALG_DEPENDENCY_INVALID", "ALG_DEPENDENCY_MISSING" }, report.Issues.Select(i => i.Code).ToArray());
        Assert.AreEqual("node/first", report.Issues.First().BindingKey);
        using var runtime = new VisionAlgorithmRuntime(catalog);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(requests));
        Assert.AreEqual(5, VisionAlgorithmExceptionDiagnostics.Read(error).Count);
    }

    /// <summary>路径解析保持配方原值，未保存目录与越界引用明确拒绝。</summary>
    [TestMethod]
    public async Task RelativeResources_ResolveAgainstRecipe_AndDoNotRewritePersistedSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "vision-inspection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = Path.Combine(root, "model.bin"); File.WriteAllText(model, "model");
        string? captured = null;
        try
        {
            var factory = new VisionAlgorithmFactory<ICounter>((c, _, _) =>
            {
                captured = c.Settings["modelPath"];
                return Task.FromResult(new VisionAlgorithmActivation(captured, EVisionAlgorithmSharing.SharedConcurrent,
                    _ => Task.FromResult(new VisionAlgorithmResource(new Counter()))));
            });
            var catalog = Catalog(factory, new VisionAlgorithmParameter("modelPath", "模型", typeof(string), isFilePath: true));
            var selection = Selection(); selection.Settings["modelPath"] = "model.bin";
            var resources = new VisionAlgorithmResourceContext(root, root);
            using var runtime = new VisionAlgorithmRuntime(catalog, resources);
            using var plan = await runtime.PrepareAsync(new[] { Request("recipe/node", selection) });
            Assert.AreEqual(model, captured); Assert.AreEqual("model.bin", selection.Settings["modelPath"]);
            Assert.AreEqual(model, resources.Resolve("resource:model.bin"));
            Assert.ThrowsExactly<ArgumentException>(() => resources.Resolve("resource:../outside.bin"));
            Assert.ThrowsExactly<InvalidOperationException>(() => new VisionAlgorithmResourceContext(resourceDirectory: root).Resolve("model.bin"));
            var unsaved = new VisionAlgorithmInspection(catalog).Analyze(new[] { Request("unsaved", selection) }, new VisionAlgorithmResourceContext(resourceDirectory: root));
            Assert.AreEqual("ALG_PARAMETER_INVALID", unsaved.Issues.Single().Code);
            selection.Settings["modelPath"] = "missing.bin";
            Assert.AreEqual("ALG_RESOURCE_MISSING", new VisionAlgorithmInspection(catalog).Analyze(new[] { Request("missing", selection) }, resources).Issues.Single().Code);
        }
        finally { File.Delete(model); Directory.Delete(root); }
    }

    /// <summary>升级保留原配置，失败时不提交无效范围。</summary>
    [TestMethod]
    public void Migration_IsExplicitAndTransactional_WithFinalMetadataValidation()
    {
        var original = new VisionAlgorithmFactory<ICounter>((_, _, _) => throw new AssertFailedException("升级不初始化"));
        var factory = original.WithConfigurationPolicy(c => c.SettingsVersion == 2 ? Array.Empty<string>() : new[] { "需要升级到版本2" },
            c => c.SettingsVersion == 1 ? new VisionAlgorithmConfiguration(2, new Dictionary<string, string> { ["scale"] = c.Settings["legacy"] }) : null);
        var inspector = new VisionAlgorithmInspection(Catalog(factory, new VisionAlgorithmParameter("scale", "缩放", typeof(double), minimum: 1, maximum: 3)));
        var selection = Selection(); selection.Settings["legacy"] = "2";
        var upgraded = inspector.Migrate(selection);
        Assert.AreEqual(2, upgraded.SettingsVersion); Assert.AreEqual("2", upgraded.Settings["scale"]);
        Assert.AreEqual(1, selection.SettingsVersion); Assert.AreEqual("2", selection.Settings["legacy"]);
        Assert.AreEqual(0, original.ValidateConfiguration(new VisionAlgorithmConfiguration(1, selection.Settings)).Count);
        selection.Settings["legacy"] = "9";
        Assert.ThrowsExactly<InvalidOperationException>(() => inspector.Migrate(selection));
        Assert.AreEqual(1, selection.SettingsVersion); Assert.AreEqual("9", selection.Settings["legacy"]);
    }

    /// <summary>初始化失败区别于登记，并保留结构化位置。</summary>
    [TestMethod]
    public async Task PreparationFailure_PreservesNodeIdentityAndOriginalException()
    {
        var factory = new VisionAlgorithmFactory<ICounter>((_, _, _) => Task.FromResult(new VisionAlgorithmActivation("native", EVisionAlgorithmSharing.SharedConcurrent,
            _ => throw new InvalidOperationException("native licence unavailable"))));
        var catalog = Catalog(factory);
        Assert.IsTrue(new VisionAlgorithmInspection(catalog).Analyze(new[] { Request("$|node|slot", Selection()) }).Success);
        using var runtime = new VisionAlgorithmRuntime(catalog);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("$|node|slot", Selection()) }));
        var issue = VisionAlgorithmExceptionDiagnostics.Read(error).Single();
        Assert.AreEqual("$|node|slot", issue.BindingKey); Assert.AreEqual("Preparation", issue.Phase);
        StringAssert.Contains(issue.Detail!, "native licence unavailable");
        Assert.IsNotNull(catalog.Origins["test.counter"].AssemblyPath);
    }
}
