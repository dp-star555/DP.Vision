using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>逻辑源路由与来源事实回归（实施基线§15「路由」段）。</summary>
[TestClass]
public sealed class RoutingTests
{
    private readonly VisionAcquisitionProviderComposer _composer = new VisionAcquisitionProviderComposer();

    /// <summary>Camera.Top只调用其绑定Provider，并记录完整来源事实。</summary>
    [TestMethod]
    public async Task BoundSource_OnlyCallsItsOwnProvider()
    {
        var top = new RecordingProviderFactory();
        var bottom = new RecordingProviderFactory();
        await using var runtime = new VisionAcquisitionRuntime(ComposeTwoProviders(top, bottom));

        using var captured = await runtime.CaptureAsync(
            new VisionSourceReference("Camera.Top"),
            new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
            new VisionAcquisitionOwner("run-1", "node-1"),
            CancellationToken.None);

        Assert.AreEqual(1, top.Created.Count);
        Assert.AreEqual(0, bottom.Created.Count);
        CollectionAssert.AreEqual(new[] { "top-camera" }, top.Last.OpenedBindings.ToArray());
        Assert.AreEqual("Camera.Top", captured.Metadata.SourceId);
        Assert.AreEqual("dp.fake.top", captured.Metadata.ProviderId);
        Assert.AreEqual("camera:serial:A", captured.Metadata.ResourceKey);
        Assert.IsFalse(string.IsNullOrWhiteSpace(captured.Metadata.CaptureId));
        Assert.AreEqual(captured.Metadata.CaptureId, captured.Frame.FrameId);
    }

    /// <summary>只改机器映射即可更换Provider，工作流文档与SourceId不变。</summary>
    [TestMethod]
    public async Task RemappingMachineConfig_SwitchesProviderWithoutDocumentChange()
    {
        var first = new RecordingProviderFactory();
        var second = new RecordingProviderFactory();
        var source = new VisionSourceReference("Camera.Top");

        await using (var before = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", first.Registration("dp.fake.one")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top-camera", "camera:serial:A") })))
        {
            using var captured = await before.CaptureAsync(
                source, new VisionCaptureRequest(TimeSpan.FromSeconds(1)), new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None);
            Assert.AreEqual("dp.fake.one", captured.Metadata.ProviderId);
        }

        await using (var after = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.two", second.Registration("dp.fake.two")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.two", "top-camera", "camera:serial:A") })))
        {
            using var captured = await after.CaptureAsync(
                source, new VisionCaptureRequest(TimeSpan.FromSeconds(1)), new VisionAcquisitionOwner("run-1", "node-1"), CancellationToken.None);
            Assert.AreEqual("dp.fake.two", captured.Metadata.ProviderId);
        }

        Assert.AreEqual(1, first.Created.Count);
        Assert.AreEqual(1, second.Created.Count);
    }

    /// <summary>Provider失败时明确失败，不自动尝试其他Provider。</summary>
    [TestMethod]
    public async Task ProviderFailure_DoesNotFallBackToOtherProvider()
    {
        var failing = new RecordingProviderFactory();
        var standby = new RecordingProviderFactory();
        var composition = _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule("m.failing", failing.Registration(
                    "dp.fake.failing",
                    capture: (request, token) => throw new InvalidOperationException("设备断线。"))),
                new FakeVisionProviderModule("m.standby", standby.Registration("dp.fake.standby"))
            },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.failing", "top-camera", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Bottom", "dp.fake.standby", "bottom-camera", "camera:serial:B")
            });
        await using var runtime = new VisionAcquisitionRuntime(composition);

        await Assert.ThrowsExactlyAsync<VisionDeviceOfflineException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                new VisionSourceReference("Camera.Top"),
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
                new VisionAcquisitionOwner("run-1", "node-1"),
                CancellationToken.None);
        });

        Assert.AreEqual(1, failing.Created.Count);
        Assert.AreEqual(0, standby.Created.Count);
    }

    /// <summary>未发布的Source在执行采集前失败，不触碰任何Provider。</summary>
    [TestMethod]
    public async Task UnknownSource_FailsBeforeTouchingProvider()
    {
        var factory = new RecordingProviderFactory();
        await using var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top-camera", "camera:serial:A") }));

        await Assert.ThrowsExactlyAsync<VisionSourceConfigurationException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                new VisionSourceReference("Camera.Unknown"),
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
                new VisionAcquisitionOwner("run-1", "node-1"),
                CancellationToken.None);
        });

        Assert.AreEqual(0, factory.Created.Count);
    }

    /// <summary>设备报告的规范身份与配置资源键不一致时拒绝继续，避免形成两个锁域。</summary>
    [TestMethod]
    public async Task CanonicalKeyMismatch_IsRejected()
    {
        var factory = new RecordingProviderFactory();
        await using var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one", canonicalKey: "camera:serial:OTHER")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top-camera", "camera:serial:A") }));

        await Assert.ThrowsExactlyAsync<VisionSourceConfigurationException>(async () =>
        {
            using var captured = await runtime.CaptureAsync(
                new VisionSourceReference("Camera.Top"),
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
                new VisionAcquisitionOwner("run-1", "node-1"),
                CancellationToken.None);
        });
    }

    /// <summary>Provider设备释放后返回图像仍可读取；Runtime退役不回收已交给调用方的像素。</summary>
    [TestMethod]
    public async Task CapturedImage_SurvivesRuntimeDisposal()
    {
        var factory = new RecordingProviderFactory();
        var runtime = new VisionAcquisitionRuntime(_composer.Compose(
            new[] { new FakeVisionProviderModule("m.one", factory.Registration("dp.fake.one")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top-camera", "camera:serial:A") }));

        using var captured = await runtime.CaptureAsync(
            new VisionSourceReference("Camera.Top"),
            new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
            new VisionAcquisitionOwner("run-1", "node-1"),
            CancellationToken.None);
        await runtime.DisposeAsync();

        var pixels = new byte[captured.Frame.Image.Info.ByteLength];
        captured.Frame.Image.CopyTo(0, pixels, 0, pixels.Length);
        Assert.IsTrue(pixels.Any(value => value != 0));
    }

    private VisionAcquisitionProviderComposition ComposeTwoProviders(
        RecordingProviderFactory top,
        RecordingProviderFactory bottom)
    {
        return _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule("m.top", top.Registration("dp.fake.top")),
                new FakeVisionProviderModule("m.bottom", bottom.Registration("dp.fake.bottom"))
            },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.top", "top-camera", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Bottom", "dp.fake.bottom", "bottom-camera", "camera:serial:B")
            });
    }
}
