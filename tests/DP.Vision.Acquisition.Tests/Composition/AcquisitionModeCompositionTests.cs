using System;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>外部回调缓冲在组合期可判定的约束回归（ACQUISITION_RUNTIME_V1 §V1-A）。</summary>
[TestClass]
public sealed class AcquisitionModeCompositionTests
{
    private readonly VisionAcquisitionProviderComposer _composer = new VisionAcquisitionProviderComposer();

    /// <summary>V1一台物理相机只对应一个逻辑源：同一资源键上两个缓冲Source拒绝发布。</summary>
    [TestMethod]
    public void TwoBufferedSourcesOnSameResourceKey_AreRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
            new[]
            {
                Buffered("Camera.Top", "dp.fake.one", "top", "camera:serial:A"),
                Buffered("Camera.Top.Mirror", "dp.fake.one", "top", "camera:serial:A")
            }));
    }

    /// <summary>被动Source允许帧先于节点到达，必须由根运行独占；其他策略拒绝发布。</summary>
    [TestMethod]
    public void BufferedSource_RequiresExclusiveRun()
    {
        foreach (var policy in new[]
                 {
                     EVisionSourceSharingPolicy.ExclusiveOperation,
                     EVisionSourceSharingPolicy.Serialized
                 })
        {
            Assert.ThrowsExactly<VisionSourceConfigurationException>(
                () => _composer.Compose(
                    new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
                    new[] { Buffered("Camera.Top", "dp.fake.one", "top", "camera:serial:A", policy) }),
                policy.ToString());
        }
    }

    /// <summary>同一物理相机不能同时被当成主动采集源和外部回调缓冲源。</summary>
    [TestMethod]
    public void MixedAcquisitionModeOnSameResourceKey_IsRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A"),
                Buffered("Camera.Top.Buffered", "dp.fake.one", "top", "camera:serial:A")
            }));
    }

    /// <summary>合法的缓冲Source可以发布，模式与策略都保留在组合里。</summary>
    [TestMethod]
    public void BufferedSourceWithExclusiveRun_Publishes()
    {
        var composition = _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
            new[] { Buffered("Camera.Top", "dp.fake.one", "top", "camera:serial:A") });

        Assert.AreEqual(1, composition.Sources.Count);
        var published = composition.Sources[0];
        Assert.AreEqual(EVisionAcquisitionMode.BufferedExternal, published.AcquisitionMode);
        Assert.AreEqual(EVisionSourceSharingPolicy.ExclusiveRun, published.SharingPolicy);
        Assert.IsNotNull(published.InboxPolicy);
    }

    /// <summary>队列策略直接决定"能收多少、留多久"，必须进组合身份；否则改容量不会产生新身份。</summary>
    [TestMethod]
    public void CompositionId_ChangesWithInboxPolicy()
    {
        var module = new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) };

        var small = _composer.Compose(module, new[]
        {
            Buffered("Camera.Top", "dp.fake.one", "top", "camera:serial:A",
                inbox: new VisionFrameInboxPolicy(2, 1024, TimeSpan.FromSeconds(1)))
        });
        var larger = _composer.Compose(module, new[]
        {
            Buffered("Camera.Top", "dp.fake.one", "top", "camera:serial:A",
                inbox: new VisionFrameInboxPolicy(4, 1024, TimeSpan.FromSeconds(1)))
        });
        var sameAgain = _composer.Compose(module, new[]
        {
            Buffered("Camera.Top", "dp.fake.one", "top", "camera:serial:A",
                inbox: new VisionFrameInboxPolicy(2, 1024, TimeSpan.FromSeconds(1)))
        });

        Assert.AreNotEqual(small.CompositionId, larger.CompositionId, "容量变化必须产生新的组合身份。");
        Assert.AreEqual(small.CompositionId, sameAgain.CompositionId, "相同配置必须得到稳定身份。");
    }

    /// <summary>模式本身也必须进组合身份，否则同键上换模式不会产生新身份。</summary>
    [TestMethod]
    public void CompositionId_ChangesWithAcquisitionMode()
    {
        var module = new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) };

        var onDemand = _composer.Compose(module, new[]
        {
            new VisionAcquisitionSourceBinding(
                "Camera.Top", "dp.fake.one", "top", "camera:serial:A",
                EVisionSourceSharingPolicy.ExclusiveRun, EVisionAcquisitionMode.OnDemand)
        });
        var buffered = _composer.Compose(module, new[]
        {
            Buffered("Camera.Top", "dp.fake.one", "top", "camera:serial:A")
        });

        Assert.AreNotEqual(onDemand.CompositionId, buffered.CompositionId);
    }

    private static VisionAcquisitionSourceBinding Buffered(
        string sourceId,
        string providerId,
        string providerBindingId,
        string resourceKey,
        EVisionSourceSharingPolicy policy = EVisionSourceSharingPolicy.ExclusiveRun,
        VisionFrameInboxPolicy? inbox = null)
    {
        return new VisionAcquisitionSourceBinding(
            sourceId,
            providerId,
            providerBindingId,
            resourceKey,
            policy,
            EVisionAcquisitionMode.BufferedExternal,
            inbox ?? new VisionFrameInboxPolicy(4, 4096, TimeSpan.FromSeconds(1)));
    }

    private static VisionAcquisitionProviderRegistration Registration(string providerId)
    {
        return new VisionAcquisitionProviderRegistration(
            providerId,
            "1.0.0",
            () => FakeVisionProvider.WithDevices(providerId));
    }
}
