using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>同资源键并发协调回归（实施基线§15「并发」段）。</summary>
[TestClass]
public sealed class SharingPolicyTests
{
    private readonly VisionAcquisitionProviderComposer _composer = new VisionAcquisitionProviderComposer();

    /// <summary>ExclusiveOperation下第二个请求确定性失败，并报告双方身份与策略。</summary>
    [TestMethod]
    public async Task ExclusiveOperation_SecondCaptureFailsWithOccupantDiagnostics()
    {
        var blocking = new BlockingCapture();
        await using var runtime = BuildRuntime("Camera.Top", "camera:serial:A", EVisionSourceSharingPolicy.ExclusiveOperation, blocking.Capture);
        await runtime.StartAsync(CancellationToken.None);

        var first = runtime.CaptureAsync(Source, Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None).AsTask();
        await blocking.Entered;

        var conflict = await Assert.ThrowsExactlyAsync<VisionResourceConflictException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                Source, Request, new VisionAcquisitionOwner("run-2", "node-2"), CancellationToken.None);
        });

        Assert.AreEqual("Camera.Top", conflict.Diagnostics.SourceId);
        Assert.AreEqual("camera:serial:A", conflict.Diagnostics.ResourceKey);
        Assert.AreEqual("run-2", conflict.Diagnostics.RequestOwnerId);
        Assert.AreEqual("node-2", conflict.Diagnostics.RequestOperationId);
        Assert.AreEqual("run-1", conflict.Diagnostics.HolderOwnerId);
        Assert.AreEqual("node-1", conflict.Diagnostics.HolderOperationId);
        Assert.AreEqual(EVisionSourceSharingPolicy.ExclusiveOperation, conflict.Diagnostics.Policy);
        StringAssert.Contains(conflict.Message, "run-1");

        blocking.Release();
        using var firstCaptured = await first;
        Assert.AreEqual("camera:serial:A", firstCaptured.Metadata.ResourceKey);
    }

    /// <summary>不同ResourceKey可以并行，互不阻塞。</summary>
    [TestMethod]
    public async Task DifferentResourceKeys_RunInParallel()
    {
        var top = new BlockingCapture();
        var bottom = new BlockingCapture();
        var composition = _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule("m.top", Registration("dp.fake.top", top.Capture)),
                new FakeVisionProviderModule("m.bottom", Registration("dp.fake.bottom", bottom.Capture))
            },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.top", "top", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Bottom", "dp.fake.bottom", "bottom", "camera:serial:B")
            });
        await using var runtime = new VisionAcquisitionRuntime(composition);
        await runtime.StartAsync(CancellationToken.None);

        var first = runtime.CaptureAsync(
            new VisionSourceReference("Camera.Top"), Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None).AsTask();
        var second = runtime.CaptureAsync(
            new VisionSourceReference("Camera.Bottom"), Request, new VisionAcquisitionOwner("run-1", "node-2"), CancellationToken.None).AsTask();

        await Task.WhenAll(top.Entered, bottom.Entered);
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);

        top.Release();
        bottom.Release();
        using var firstCaptured = await first;
        using var secondCaptured = await second;
        Assert.AreNotEqual(firstCaptured.Metadata.ResourceKey, secondCaptured.Metadata.ResourceKey);
    }

    /// <summary>Serialized按到达顺序排队，两个请求都能完成。</summary>
    [TestMethod]
    public async Task Serialized_QueuesInsteadOfFailing()
    {
        var blocking = new BlockingCapture();
        await using var runtime = BuildRuntime("Camera.Top", "camera:serial:A", EVisionSourceSharingPolicy.Serialized, blocking.Capture);
        await runtime.StartAsync(CancellationToken.None);

        var first = runtime.CaptureAsync(Source, Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None).AsTask();
        await blocking.Entered;
        var second = runtime.CaptureAsync(Source, Request, new VisionAcquisitionOwner("run-2", "node-2"), CancellationToken.None).AsTask();
        Assert.IsFalse(second.IsCompleted);

        blocking.Release();
        using var firstCaptured = await first;
        using var secondCaptured = await second;
        Assert.AreEqual(firstCaptured.Metadata.SourceId, secondCaptured.Metadata.SourceId);
    }

    /// <summary>Serialized等待期间支持取消，取消后不占用租约。</summary>
    [TestMethod]
    public async Task Serialized_WaitingHonoursCancellation()
    {
        var blocking = new BlockingCapture();
        await using var runtime = BuildRuntime("Camera.Top", "camera:serial:A", EVisionSourceSharingPolicy.Serialized, blocking.Capture);
        await runtime.StartAsync(CancellationToken.None);

        var first = runtime.CaptureAsync(Source, Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None).AsTask();
        await blocking.Entered;

        using var cancelled = new CancellationTokenSource();
        var waiting = runtime.CaptureAsync(Source, Request, new VisionAcquisitionOwner("run-2", "node-2"), cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiting);

        blocking.Release();
        using var firstCaptured = await first;
        Assert.AreEqual("Camera.Top", firstCaptured.Metadata.SourceId);
    }

    /// <summary>Serialized等待超过请求超时时明确失败，不做无限排队。</summary>
    [TestMethod]
    public async Task Serialized_WaitingFailsWhenTimeoutElapses()
    {
        var blocking = new BlockingCapture();
        await using var runtime = BuildRuntime("Camera.Top", "camera:serial:A", EVisionSourceSharingPolicy.Serialized, blocking.Capture);
        await runtime.StartAsync(CancellationToken.None);

        var first = runtime.CaptureAsync(Source, Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None).AsTask();
        await blocking.Entered;

        var shortWait = new VisionCaptureRequest(TimeSpan.FromMilliseconds(50));
        var conflict = await Assert.ThrowsExactlyAsync<VisionResourceConflictException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                Source, shortWait, new VisionAcquisitionOwner("run-2", "node-2"), CancellationToken.None);
        });
        StringAssert.Contains(conflict.Message, "Serialized");

        blocking.Release();
        using var firstCaptured = await first;
        Assert.IsNotNull(firstCaptured);
    }

    /// <summary>采集过程中取消仍然释放租约，后续请求可以继续。</summary>
    [TestMethod]
    public async Task CancelledCapture_ReleasesLease()
    {
        var factory = new RecordingProviderFactory();
        await using var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one", capture: CaptureCancelled)) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A") }));
        await runtime.StartAsync(CancellationToken.None);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                Source, Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None);
        });

        // 租约必须已释放：换成正常采集行为后再次请求应当成功。
        var recovered = new RecordingProviderFactory();
        await using var second = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", recovered.Registration("dp.fake.one")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A") }));
        await second.StartAsync(CancellationToken.None);
        using var capturedAgain = await second.CaptureAsync(
            Source, Request, new VisionAcquisitionOwner("run-2", "node-2"), CancellationToken.None);
        Assert.IsNotNull(capturedAgain);
    }

    /// <summary>采集失败后设备被标记故障并阻断后续使用，同时释放租约。</summary>
    [TestMethod]
    public async Task CaptureFailure_MarksDeviceFaultedAndReleasesLease()
    {
        var factory = new RecordingProviderFactory();
        await using var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one", capture: CaptureFailing)) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A") }));
        await runtime.StartAsync(CancellationToken.None);

        await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                Source, Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None);
        });

        // 故障后再次请求仍明确失败，而不是"碰巧成功"或静默返回上一帧。
        var second = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                Source, Request, new VisionAcquisitionOwner("run-1", "node-2"), CancellationToken.None);
        });
        StringAssert.Contains(second.Message, "camera:serial:A");
    }

    /// <summary>打开设备失败发生在Runtime Start阶段：Runtime不Ready，后续采集明确失败且保留打开失败原因。</summary>
    [TestMethod]
    public async Task OpenFailure_DuringStart_LeavesRuntimeNotReady()
    {
        var failing = new FakeVisionProvider("dp.fake.one", binding => throw new InvalidOperationException("打开设备失败。"));
        var composition = _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule(
                    "m.one",
                    new VisionAcquisitionProviderRegistration("dp.fake.one", "1.0.0", () => failing))
            },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A") });
        await using var runtime = new VisionAcquisitionRuntime(composition);

        var state = await runtime.StartAsync(CancellationToken.None);
        Assert.AreEqual(EVisionRuntimeState.NotReady, state);

        var offline = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                Source, Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None);
        });
        StringAssert.Contains(offline.Message, "打开设备失败");

        // 打开失败是会话终态：再次请求仍明确失败，不会"碰巧成功"。
        var again = await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                Source, Request, new VisionAcquisitionOwner("run-2", "node-2"), CancellationToken.None);
        });
        StringAssert.Contains(again.Message, "打开设备失败");
    }

    /// <summary>尚未实现的共享策略明确拒绝，不用进程内锁冒充ExclusiveRun或Broadcast。</summary>
    [TestMethod]
    public async Task UnimplementedPolicies_AreRejectedExplicitly()
    {
        foreach (var policy in new[] { EVisionSourceSharingPolicy.ExclusiveRun, EVisionSourceSharingPolicy.Broadcast })
        {
            await using var runtime = BuildRuntime("Camera.Top", "camera:serial:A", policy, new BlockingCapture().Capture);
            await runtime.StartAsync(CancellationToken.None);
            var exception = await Assert.ThrowsExactlyAsync<VisionSourceConfigurationException>(async () =>
            {
                using var captured = await runtime.CaptureAsync(
                    Source, Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None);
            });
            StringAssert.Contains(exception.Message, policy.ToString());
        }
    }

    /// <summary>两个SourceId映射同一ResourceKey时共享同一互斥状态。</summary>
    [TestMethod]
    public async Task SameResourceKey_TwoSources_ShareOneMutex()
    {
        var blocking = new BlockingCapture();
        var composition = _composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", Registration("dp.fake.one", blocking.Capture)) },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Alias", "dp.fake.one", "top", "camera:serial:A")
            });
        await using var runtime = new VisionAcquisitionRuntime(composition);
        await runtime.StartAsync(CancellationToken.None);

        var first = runtime.CaptureAsync(
            new VisionSourceReference("Camera.Top"), Request, new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None).AsTask();
        await blocking.Entered;

        var conflict = await Assert.ThrowsExactlyAsync<VisionResourceConflictException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                new VisionSourceReference("Camera.Alias"),
                Request,
                new VisionAcquisitionOwner("run-2", "node-2"),
                CancellationToken.None);
        });
        Assert.AreEqual("Camera.Alias", conflict.Diagnostics.SourceId);

        blocking.Release();
        using var firstCaptured = await first;
        Assert.IsNotNull(firstCaptured);
    }

    private static VisionSourceReference Source => new VisionSourceReference("Camera.Top");

    private static VisionCaptureRequest Request => new VisionCaptureRequest(TimeSpan.FromSeconds(5));

    private VisionAcquisitionProviderRegistration Registration(
        string providerId,
        Func<VisionCaptureRequest, CancellationToken, ValueTask<VisionProviderFrame>> capture)
    {
        return new VisionAcquisitionProviderRegistration(
            providerId,
            "1.0.0",
            () => FakeVisionProvider.WithDevices(providerId, capture));
    }

    private VisionAcquisitionRuntime BuildRuntime(
        string sourceId,
        string resourceKey,
        EVisionSourceSharingPolicy policy,
        Func<VisionCaptureRequest, CancellationToken, ValueTask<VisionProviderFrame>> capture)
    {
        var composition = _composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", Registration("dp.fake.one", capture)) },
            new[] { new VisionAcquisitionSourceBinding(sourceId, "dp.fake.one", "top", resourceKey, policy) });
        return new VisionAcquisitionRuntime(composition);
    }

    private static ValueTask<VisionProviderFrame> CaptureCancelled(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        throw new OperationCanceledException(cancellationToken);
    }

    private static ValueTask<VisionProviderFrame> CaptureFailing(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("设备断线。");
    }

    private sealed class BlockingCapture
    {
        private readonly TaskCompletionSource<bool> _entered =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult(true);

        public async ValueTask<VisionProviderFrame> Capture(
            VisionCaptureRequest request,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new VisionProviderFrame(TestImages.Gray8(), DateTimeOffset.UtcNow, null);
        }
    }
}
