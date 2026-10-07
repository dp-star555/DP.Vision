using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using DP.Vision.Zxing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Runtime.Tests;

/// <summary>独立计数算法契约，检查真实资源生命周期而不是反射实现细节。</summary>
[VisionCapability("test.counter", "Test", "Counter")]
public interface ICounter { /// <summary>实例身份。</summary>
    int Number { get; } }

/// <summary>算法目录、共享、并发、取消与准备回滚。</summary>
[TestClass]
public sealed class AlgorithmRuntimeTests
{
    private sealed class Counter(int number, Action release) : ICounter, IDisposable
    { public int Number => number; public void Dispose() => release(); }
    private sealed class Module(string id, Action<IVisionAlgorithmRegistration> register) : IVisionAlgorithmModule
    { public string ExtensionId => id; public void Register(IVisionAlgorithmRegistration registrations) => register(registrations); }
    private static VisionAlgorithmRequest Request(string key, string id = "test.counter", string resource = "model-A") =>
        new VisionAlgorithmRequest(key, typeof(ICounter), new VisionAlgorithmSelection { ImplementationId = id, Settings = new Dictionary<string, string> { ["resource"] = resource } });
    private static VisionAlgorithmRuntime Runtime(IVisionAlgorithmFactory factory) => new VisionAlgorithmRuntime(VisionAlgorithmCatalog.Compose(new[] {
        new Module("test", r => r.Add(new VisionAlgorithmDescriptor("test.counter", "Test", "1", factory))) }));
    private static IVisionAlgorithmFactory Factory(Action created, Action released, EVisionAlgorithmSharing sharing = EVisionAlgorithmSharing.SharedConcurrent) =>
        new VisionAlgorithmFactory<ICounter>((c, _, token) => Task.FromResult(new VisionAlgorithmActivation(c.Settings["resource"], sharing,
            cancellation => { cancellation.ThrowIfCancellationRequested(); created(); return Task.FromResult(new VisionAlgorithmResource(new Counter(7, released))); })));

    /// <summary>真实引擎登记只建立描述，两种块实现并存，掩码不重复登记读码能力。</summary>
    [TestMethod]
    public void EngineCatalog_IsMetadataOnly_WithTwoPatchImplementations()
    {
        var catalog = VisionAlgorithmCatalog.Compose(new IVisionAlgorithmModule[] { new OpenCvVisionAlgorithmModule(), new ZxingVisionAlgorithmModule(), new ManagedVisionAlgorithmModule() });
        Assert.AreEqual(0, catalog.Diagnostics.Count);
        Assert.AreEqual(2, catalog.Implementations.Count(d => d.ContractType == typeof(IPatchAnomalyDetector)));
        Assert.AreEqual(1, catalog.Implementations.Count(d => d.CapabilityId == "code.read"));
        CollectionAssert.Contains(catalog.GetRequired("zxing.code").Features.ToArray(), "masked");
    }

    /// <summary>失败模块原子撤销，身份冲突的两个贡献都不可用。</summary>
    [TestMethod]
    public void FailedAndConflictingModules_DoNotLeakRegistrations()
    {
        var factory = Factory(() => { }, () => { });
        var descriptor = new VisionAlgorithmDescriptor("test.counter", "Test", "1", factory);
        var failed = VisionAlgorithmCatalog.Compose(new[] { new Module("bad", r => { r.Add(descriptor); throw new InvalidOperationException("halfway"); }) });
        Assert.AreEqual(0, failed.Implementations.Count); StringAssert.Contains(failed.Diagnostics.Single().Reason, "halfway");
        var conflict = VisionAlgorithmCatalog.Compose(new[] { new Module("one", r => r.Add(descriptor)), new Module("two", r => r.Add(descriptor)) });
        Assert.AreEqual(0, conflict.Implementations.Count); StringAssert.Contains(conflict.Diagnostics.Single().Reason, "one"); StringAssert.Contains(conflict.Diagnostics.Single().Reason, "two");
    }

    /// <summary>不同绑定共享资源而调用参数互不污染，最后引用归还才销毁。</summary>
    [TestMethod]
    public async Task SameResource_IsCreatedOnce_AndReleasedAfterLastPlan()
    {
        int creates = 0, releases = 0;
        using var runtime = Runtime(Factory(() => Interlocked.Increment(ref creates), () => Interlocked.Increment(ref releases)));
        var first = await runtime.PrepareAsync(new[] { Request("A"), Request("B") });
        var second = await runtime.PrepareAsync(new[] { Request("C") });
        Assert.AreEqual(1, creates);
        Assert.AreEqual(8, first.Invoke<ICounter, int>("A", a => a.Number + 1));
        Assert.AreEqual(9, first.Invoke<ICounter, int>("B", a => a.Number + 2));
        first.Dispose(); Assert.AreEqual(0, releases);
        second.Dispose(); Assert.AreEqual(1, releases);
    }

    /// <summary>不同资源及独占策略不错误复用。</summary>
    [TestMethod]
    public async Task DifferentResources_AndExclusiveBindings_AreSeparate()
    {
        int creates = 0;
        using var runtime = Runtime(Factory(() => Interlocked.Increment(ref creates), () => { }));
        using var plan = await runtime.PrepareAsync(new[] { Request("A"), Request("B", resource: "model-B") });
        Assert.AreEqual(2, creates);
        using var exclusive = Runtime(Factory(() => Interlocked.Increment(ref creates), () => { }, EVisionAlgorithmSharing.Exclusive));
        using var independent = await exclusive.PrepareAsync(new[] { Request("C"), Request("D") });
        Assert.AreEqual(4, creates);
    }

    /// <summary>静态缺失在创建前汇总；修复后的运行仍可准备。</summary>
    [TestMethod]
    public async Task PreparationFailure_RollsBackEarlierResources()
    {
        int releases = 0;
        using var runtime = Runtime(Factory(() => { }, () => Interlocked.Increment(ref releases)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("A"), Request("B", "missing") }));
        Assert.AreEqual(0, releases);
        using var repaired = await runtime.PrepareAsync(new[] { Request("A") });
        Assert.AreEqual(7, repaired.Invoke<ICounter, int>("A", a => a.Number));
    }

    /// <summary>在途同步调用的资源不能因为计划被停止而提前释放。</summary>
    [TestMethod]
    public async Task DisposePlan_DoesNotReleaseInFlightCall()
    {
        int releases = 0; using var entered = new ManualResetEventSlim(); using var exit = new ManualResetEventSlim();
        using var runtime = Runtime(Factory(() => { }, () => Interlocked.Increment(ref releases)));
        var plan = await runtime.PrepareAsync(new[] { Request("A") });
        var call = Task.Run(() => plan.Invoke<ICounter, int>("A", a => { entered.Set(); Assert.IsTrue(exit.Wait(TimeSpan.FromSeconds(10))); return a.Number; }));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        plan.Dispose(); Assert.AreEqual(0, releases); exit.Set();
        Assert.AreEqual(7, await call); Assert.AreEqual(1, releases);
    }

    /// <summary>异步调用直到Task实际完成后才归还资源。</summary>
    [TestMethod]
    public async Task AsyncCall_HoldsResourceAcrossAwait()
    {
        int releases = 0; var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = Runtime(Factory(() => { }, () => Interlocked.Increment(ref releases)));
        var plan = await runtime.PrepareAsync(new[] { Request("A") });
        var call = plan.InvokeAsync<ICounter, int>("A", async (a, _) => { entered.SetResult(true); await exit.Task; return a.Number; });
        await entered.Task; plan.Dispose(); Assert.AreEqual(0, releases); exit.SetResult(true);
        Assert.AreEqual(7, await call); Assert.AreEqual(1, releases);
    }

    /// <summary>一个准备申请取消不能取消其他使用者所需的共享创建。</summary>
    [TestMethod]
    public async Task CancellingOneWaiter_DoesNotCancelSharedCreation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource(); int creates = 0;
        var factory = new VisionAlgorithmFactory<ICounter>((c, _, _) => Task.FromResult(new VisionAlgorithmActivation(c.Settings["resource"], EVisionAlgorithmSharing.SharedConcurrent, async token =>
        { Interlocked.Increment(ref creates); entered.TrySetResult(true); await exit.Task; token.ThrowIfCancellationRequested(); return new VisionAlgorithmResource(new Counter(4, () => { })); })));
        using var runtime = Runtime(factory);
        var first = runtime.PrepareAsync(new[] { Request("A") }, cancelled.Token); await entered.Task;
        var second = runtime.PrepareAsync(new[] { Request("B") }); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => first); exit.SetResult(true);
        using var plan = await second; Assert.AreEqual(1, creates); Assert.AreEqual(4, plan.Invoke<ICounter, int>("B", a => a.Number));
    }

    /// <summary>串行资源排队可取消，不会进入第二个原生调用。</summary>
    [TestMethod]
    public async Task SerialResource_QueuesAndCancelsSecondCall()
    {
        using var entered = new ManualResetEventSlim(); using var exit = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        using var runtime = Runtime(Factory(() => { }, () => { }, EVisionAlgorithmSharing.SharedSerial));
        using var plan = await runtime.PrepareAsync(new[] { Request("A"), Request("B") });
        var first = Task.Run(() => plan.Invoke<ICounter, int>("A", a => { entered.Set(); Assert.IsTrue(exit.Wait(TimeSpan.FromSeconds(10))); return a.Number; }));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        var second = plan.InvokeAsync<ICounter, int>("B", (_, _) => throw new AssertFailedException("第二次调用不应进入。"), cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => second); exit.Set(); await first;
    }

    /// <summary>依赖循环在创建模型之前被拒绝。</summary>
    [TestMethod]
    public async Task DependencyCycle_IsRejectedBeforeCreation()
    {
        var factory = new VisionAlgorithmFactory<ICounter>((_, _, _) => throw new AssertFailedException("不应创建"),
            _ => new[] { new VisionAlgorithmDependency("self", typeof(ICounter)) });
        using var runtime = Runtime(factory);
        var request = Request("A"); request.Selection.Dependencies["self"] = request.Selection;
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { request }));
        StringAssert.Contains(error.Message, "循环");
    }

    /// <summary>有限的依赖链允许复用同一实现，不将实现身份重复误判为配置引用循环。</summary>
    [TestMethod]
    public async Task RepeatedImplementation_CanComposeFiniteDependencyGraph()
    {
        int creates = 0, releases = 0;
        var factory = new VisionAlgorithmFactory<ICounter>((configuration, dependencies, _) =>
            Task.FromResult(new VisionAlgorithmActivation(configuration.Settings["resource"], EVisionAlgorithmSharing.SharedConcurrent, _ =>
            {
                creates++;
                var number = dependencies.TryGetValue("self", out var child) ? ((ICounter)child).Number + 1 : 1;
                return Task.FromResult(new VisionAlgorithmResource(new Counter(number, () => releases++)));
            })), configuration => configuration.Settings.ContainsKey("next")
                ? new[] { new VisionAlgorithmDependency("self", typeof(ICounter)) } : Array.Empty<VisionAlgorithmDependency>());
        var catalog = VisionAlgorithmCatalog.Compose(new[] { new Module("test", r => r.Add(new VisionAlgorithmDescriptor("test.counter", "Test", "1", factory))) });
        var request = Request("root", resource: "outer");
        request.Selection.Settings["next"] = "true";
        request.Selection.Dependencies["self"] = Request("child", resource: "inner").Selection;
        Assert.IsTrue(new VisionAlgorithmInspection(catalog).Analyze(new[] { request }, checkFiles: false).Success);
        Assert.AreEqual(0, creates);
        using var runtime = new VisionAlgorithmRuntime(catalog);

        using (var plan = await runtime.PrepareAsync(new[] { request }))
        {
            Assert.AreEqual(2, plan.Invoke<ICounter, int>("root", a => a.Number));
            Assert.AreEqual(2, creates);
        }
        Assert.AreEqual(2, releases);
    }

    /// <summary>放宽实现复用不取消配置快照的递归预算。</summary>
    [TestMethod]
    public async Task DependencyDepthBudget_IsRejectedBeforeCreation()
    {
        int creates = 0;
        using var runtime = Runtime(Factory(() => creates++, () => { }));
        var request = Request("root");
        var current = request.Selection;
        for (var i = 0; i < 65; i++)
        {
            var child = Request("child").Selection;
            current.Dependencies["child"] = child; current = child;
        }

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { request }));

        StringAssert.Contains(error.Message, "深度预算"); Assert.AreEqual(0, creates);
    }

    /// <summary>请求键、选择和特征不能依赖发现顺序。</summary>
    [TestMethod]
    public async Task MissingSelection_DuplicateKey_AndFeature_AreRejected()
    {
        using var runtime = Runtime(Factory(() => { }, () => { }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("A", "") }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("A"), Request("A") }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { new VisionAlgorithmRequest("A", typeof(ICounter), Request("A").Selection, new[] { "missing" }) }));
    }

    /// <summary>不响应取消的在途工厂仍持有依赖，直到创建/销毁完成才归还。</summary>
    [TestMethod]
    public async Task CancelledCreation_KeepsDependencyAliveUntilCreatorFinishes()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wasReleased = false;
        var dependency = Factory(() => { }, () => { wasReleased = true; dependencyReleased.TrySetResult(true); });
        var parent = new VisionAlgorithmFactory<ICounter>((_, dependencies, _) => Task.FromResult(new VisionAlgorithmActivation("parent", EVisionAlgorithmSharing.SharedConcurrent, async _ =>
        {
            entered.TrySetResult(true); await resume.Task;
            Assert.IsFalse(wasReleased, "尚未完成的工厂使用的依赖被提前销毁。");
            return new VisionAlgorithmResource(new Counter(((ICounter)dependencies["dependency"]).Number, () => { }));
        })), _ => new[] { new VisionAlgorithmDependency("dependency", typeof(ICounter)) });
        using var runtime = new VisionAlgorithmRuntime(VisionAlgorithmCatalog.Compose(new[] { new Module("deps", r =>
        { r.Add(new VisionAlgorithmDescriptor("test.counter", "Test", "1", dependency)); r.Add(new VisionAlgorithmDescriptor("parent", "Test", "1", parent)); }) }));
        var request = Request("root", "parent"); request.Selection.Dependencies["dependency"] = Request("unused").Selection;
        using var cancel = new CancellationTokenSource();
        var pending = runtime.PrepareAsync(new[] { request }, cancel.Token); await entered.Task;
        cancel.Cancel(); await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => pending);
        Assert.IsFalse(wasReleased); resume.SetResult(true);
        Assert.IsTrue(await Task.WhenAny(dependencyReleased.Task, Task.Delay(TimeSpan.FromSeconds(10))) == dependencyReleased.Task);
        Assert.AreEqual(0, runtime.ReleaseFailures.Count);
    }
}
