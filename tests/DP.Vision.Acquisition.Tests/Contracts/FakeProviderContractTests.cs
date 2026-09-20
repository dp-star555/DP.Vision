using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>内存测试替身的自检：阶段B的组合与并发测试依赖这些行为可信。</summary>
[TestClass]
public sealed class FakeProviderContractTests
{
    /// <summary>打开设备时记录绑定身份，并返回带同一绑定的设备身份。</summary>
    [TestMethod]
    public async Task FakeProvider_RecordsOpenedBindingAndReportsIdentity()
    {
        await using var provider = FakeVisionProvider.WithDevices("dp.fake");
        await using var device = await provider.OpenAsync("top-camera", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "top-camera" }, provider.OpenedBindings.ToArray());
        Assert.AreEqual("dp.fake", device.Identity.ProviderId);
        Assert.AreEqual("top-camera", device.Identity.ProviderBindingId);
    }

    /// <summary>采集返回的中立图像在设备释放后仍可读取像素。</summary>
    [TestMethod]
    public async Task FakeDevice_ReturnsImageReadableAfterDeviceDisposed()
    {
        var device = new FakeVisionDevice(new VisionDeviceIdentity("dp.fake", "top"));
        byte[] pixels;
        int byteLength;
        await using (device)
        {
            using var frame = await device.CaptureAsync(
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
                CancellationToken.None);
            byteLength = frame.Image.Info.ByteLength;
            pixels = new byte[byteLength];
            frame.Image.CopyTo(0, pixels, 0, byteLength);
        }

        Assert.AreEqual(1, device.DisposeCount);
        Assert.IsTrue(pixels.Any(value => value != 0));
    }

    /// <summary>已取消的令牌在设备层立即生效，不产生半成品帧。</summary>
    [TestMethod]
    public async Task FakeDevice_HonoursCancellation()
    {
        await using var device = new FakeVisionDevice(new VisionDeviceIdentity("dp.fake", "top"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            using var frame = await device.CaptureAsync(
                new VisionCaptureRequest(TimeSpan.FromSeconds(1)),
                cancelled.Token);
        });
    }

    /// <summary>Module可注入贡献中途失败，用于验证候选组合的原子性。</summary>
    [TestMethod]
    public void FakeModule_CanInjectContributeFailure()
    {
        var module = new FakeVisionProviderModule(
            "dp.fake.module",
            new VisionAcquisitionProviderRegistration("dp.fake", "1.0.0", () => FakeVisionProvider.WithDevices("dp.fake")))
        {
            ContributeFailure = new InvalidOperationException("注入的贡献失败。")
        };

        Assert.AreEqual(1, module.RegistrationCount);
        Assert.AreEqual("dp.fake.module", module.ExtensionId);
        Assert.ThrowsExactly<InvalidOperationException>(() => module.Contribute(new NoopBuilder()));
        Assert.AreEqual(1, module.ContributeCount);
    }

    private sealed class NoopBuilder : IVisionAcquisitionProviderContributionBuilder
    {
        public void Register(VisionAcquisitionProviderRegistration registration)
        {
        }
    }
}
