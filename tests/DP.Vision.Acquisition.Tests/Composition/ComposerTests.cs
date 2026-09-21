using System;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>Provider组合的候选贡献与原子发布回归（实施基线§15「组合」段）。</summary>
[TestClass]
public sealed class ComposerTests
{
    private readonly VisionAcquisitionProviderComposer _composer = new VisionAcquisitionProviderComposer();

    /// <summary>两个不同ProviderId可以同时发布并各自绑定逻辑源。</summary>
    [TestMethod]
    public void TwoDistinctProviders_PublishTogether()
    {
        var composition = _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule("module.one", Registration("dp.fake.one")),
                new FakeVisionProviderModule("module.two", Registration("dp.fake.two"))
            },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Bottom", "dp.fake.two", "bottom", "camera:serial:B")
            });

        Assert.AreEqual(2, composition.Sources.Count);
        Assert.IsTrue(composition.TryGetProvider("dp.fake.one", out _));
        Assert.IsTrue(composition.TryGetProvider("dp.fake.two", out _));
        Assert.IsFalse(string.IsNullOrWhiteSpace(composition.CompositionId));
    }

    /// <summary>重复ProviderId拒绝发布，避免同身份实现互相覆盖。</summary>
    [TestMethod]
    public void DuplicateProviderId_IsRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule("module.one", Registration("dp.fake.one")),
                new FakeVisionProviderModule("module.two", Registration("dp.fake.one"))
            },
            Array.Empty<VisionAcquisitionSourceBinding>()));
    }

    /// <summary>重复Module身份拒绝发布。</summary>
    [TestMethod]
    public void DuplicateModuleId_IsRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule("module.one", Registration("dp.fake.one")),
                new FakeVisionProviderModule("module.one", Registration("dp.fake.two"))
            },
            Array.Empty<VisionAcquisitionSourceBinding>()));
    }

    /// <summary>Module贡献中途抛异常时丢弃候选，已发布的正式组合保持不变。</summary>
    [TestMethod]
    public void ModuleFailure_LeavesPublishedCompositionUnchanged()
    {
        var published = _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A") });
        var sourceCountBefore = published.Sources.Count;
        var compositionIdBefore = published.CompositionId;

        var failing = new FakeVisionProviderModule("module.two", Registration("dp.fake.two"))
        {
            ContributeFailure = new InvalidOperationException("注入的贡献失败。")
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")), failing },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A") }));

        Assert.AreEqual(sourceCountBefore, published.Sources.Count);
        Assert.AreEqual(compositionIdBefore, published.CompositionId);
        Assert.IsTrue(published.TryGetSource("Camera.Top", out _));
    }

    /// <summary>绑定到不存在Provider的Source拒绝发布。</summary>
    [TestMethod]
    public void SourceWithoutProvider_IsRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.missing", "top", "camera:serial:A") }));
    }

    /// <summary>重复SourceId拒绝发布。</summary>
    [TestMethod]
    public void DuplicateSourceId_IsRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top2", "camera:serial:B")
            }));
    }

    /// <summary>两个Source映射同一ResourceKey但Provider不同时拒绝发布，避免形成两个锁域。</summary>
    [TestMethod]
    public void SameResourceKey_DifferentProvider_IsRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[]
            {
                new FakeVisionProviderModule("module.one", Registration("dp.fake.one")),
                new FakeVisionProviderModule("module.two", Registration("dp.fake.two"))
            },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Alias", "dp.fake.two", "top", "camera:serial:A")
            }));
    }

    /// <summary>同一ResourceKey混用不同共享策略时拒绝发布，避免冲突行为不可预测。</summary>
    [TestMethod]
    public void SameResourceKey_DifferentSharingPolicy_IsRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A"),
                new VisionAcquisitionSourceBinding(
                    "Camera.Alias",
                    "dp.fake.one",
                    "top",
                    "camera:serial:A",
                    EVisionSourceSharingPolicy.Serialized)
            }));
    }

    /// <summary>同一ResourceKey的多个Source可以共存，并共享同一Provider绑定。</summary>
    [TestMethod]
    public void SameResourceKey_SameProviderAndBinding_IsAccepted()
    {
        var composition = _composer.Compose(
            new[] { new FakeVisionProviderModule("module.one", Registration("dp.fake.one")) },
            new[]
            {
                new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A"),
                new VisionAcquisitionSourceBinding("Camera.Alias", "dp.fake.one", "top", "camera:serial:A")
            });

        Assert.AreEqual(2, composition.Sources.Count);
        Assert.IsTrue(composition.TryGetSource("Camera.Alias", out _));
    }

    /// <summary>相同输入产生相同组合身份；版本变化必须产生不同身份。</summary>
    [TestMethod]
    public void CompositionId_IsStableForSameInputAndChangesWithVersion()
    {
        var bindings = new[]
        {
            new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A")
        };
        var first = _composer.Compose(new[] { new FakeVisionProviderModule("m", Registration("dp.fake.one", "1.0.0")) }, bindings);
        var second = _composer.Compose(new[] { new FakeVisionProviderModule("m", Registration("dp.fake.one", "1.0.0")) }, bindings);
        var upgraded = _composer.Compose(new[] { new FakeVisionProviderModule("m", Registration("dp.fake.one", "1.0.1")) }, bindings);

        Assert.AreEqual(first.CompositionId, second.CompositionId);
        Assert.AreNotEqual(first.CompositionId, upgraded.CompositionId);
        CollectionAssert.AreEqual(new[] { "dp.fake.one@1.0.0" }, first.ProviderManifest.ToArray());
    }

    /// <summary>
    /// 只读注册清单是版本清单的结构化形式：按身份排序、一项一个Provider，
    /// 未配置任何Source的Provider同样在列，声明的显示名原样带出。
    /// </summary>
    [TestMethod]
    public void Providers_ExposesStructuredRegistrationList()
    {
        var composition = _composer.Compose(
            new[]
            {
                new VisionAcquisitionProviderRegistration(
                    "dp.fake.two", "2.0.0", () => FakeVisionProvider.WithDevices("dp.fake.two")),
                new VisionAcquisitionProviderRegistration(
                    "dp.fake.one", "1.0.0", () => FakeVisionProvider.WithDevices("dp.fake.one"))
                {
                    DisplayName = "一号Provider"
                }
            },
            new[] { new VisionAcquisitionSourceBinding("Camera.Top", "dp.fake.one", "top", "camera:serial:A") });

        CollectionAssert.AreEqual(
            new[] { "dp.fake.one", "dp.fake.two" },
            composition.Providers.Select(provider => provider.ProviderId).ToArray(),
            "注册清单必须按ProviderId排序，界面顺序不得依赖字典枚举顺序。");
        CollectionAssert.AreEqual(
            composition.ProviderManifest.ToArray(),
            composition.Providers.Select(provider => provider.ProviderId + "@" + provider.Version).ToArray(),
            "注册清单与版本清单必须表达同一批Provider，否则消费方会看到两套事实。");

        Assert.AreEqual("1.0.0", composition.Providers[0].Version);
        Assert.AreEqual("一号Provider", composition.Providers[0].DisplayName);
        Assert.IsNull(composition.Providers[1].DisplayName, "未声明显示名时保持为空，由消费方回退为ProviderId。");

        // 未配置Source的Provider同样在列：发现正是要在"还没配置"时先看见现场。
        Assert.AreEqual(1, composition.Sources.Count);
        Assert.AreEqual(2, composition.Providers.Count);
    }

    private static VisionAcquisitionProviderRegistration Registration(string providerId, string version = "1.0.0")
    {
        return new VisionAcquisitionProviderRegistration(
            providerId,
            version,
            () => FakeVisionProvider.WithDevices(providerId));
    }
}
