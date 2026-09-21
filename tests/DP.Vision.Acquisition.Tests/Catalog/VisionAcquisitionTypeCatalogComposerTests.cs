using System;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// AcquisitionType Catalog的候选贡献与一次Freeze回归（V2-1 §20）。
/// 覆盖：重复TypeId/Module身份拒绝、空工厂/未知配置版本/能力不一致拒绝、
/// 任一Module失败不留下半个Catalog、面阵/线扫按Kind列出。
/// </summary>
[TestClass]
public sealed class VisionAcquisitionTypeCatalogComposerTests
{
    private readonly VisionAcquisitionTypeCatalogComposer _composer = new VisionAcquisitionTypeCatalogComposer();

    /// <summary>空Module集合冻结为空Catalog，而不是失败。</summary>
    [TestMethod]
    public void EmptyModules_FreezeEmptyCatalog()
    {
        var catalog = _composer.Compose(Array.Empty<IVisionAcquisitionDriverModule>());

        Assert.AreEqual(0, catalog.Types.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(catalog.CatalogId));
    }

    /// <summary>面阵Type被冻结并可列出；CatalogId有内容。</summary>
    [TestMethod]
    public void AreaScanType_IsFrozenAndListed()
    {
        var catalog = _composer.Compose(new[] { Module("module.one", Registration()) });

        var descriptor = catalog.Types.Single();
        Assert.AreEqual("dp.acquisition.test.area", descriptor.AcquisitionTypeId);
        Assert.AreEqual("dp.vision.test", descriptor.PluginId);
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, descriptor.Kind);
        Assert.AreEqual(1, descriptor.DeviceSettingsVersion);
        Assert.IsNotNull(descriptor.Factory);
        Assert.IsTrue(catalog.TryGetType("dp.acquisition.test.area", out _));
        CollectionAssert.AreEqual(
            new[] { "dp.acquisition.test.area" },
            catalog.GetByKind(EVisionAcquisitionKind.AreaScan).Select(item => item.AcquisitionTypeId).ToArray());
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            catalog.GetByKind(EVisionAcquisitionKind.LineScan).Select(item => item.AcquisitionTypeId).ToArray());
    }

    /// <summary>线扫测试Type先允许冻结：Catalog能按LineScan列出，节点Source下拉据此过滤。</summary>
    [TestMethod]
    public void LineScanTestType_IsFrozenAndListed()
    {
        var catalog = _composer.Compose(
            new[] { Module("module.one", Registration(typeId: "dp.acquisition.test.line", kind: EVisionAcquisitionKind.LineScan)) });

        var descriptor = catalog.Types.Single();
        Assert.AreEqual(EVisionAcquisitionKind.LineScan, descriptor.Kind);
        CollectionAssert.AreEqual(
            new[] { "dp.acquisition.test.line" },
            catalog.GetByKind(EVisionAcquisitionKind.LineScan).Select(item => item.AcquisitionTypeId).ToArray());
    }

    /// <summary>重复AcquisitionTypeId拒绝发布（V2验收§21.2）。</summary>
    [TestMethod]
    public void DuplicateAcquisitionTypeId_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[]
            {
                Module("module.one", Registration()),
                Module("module.two", Registration())
            }));

        StringAssert.Contains(failure.Message, "重复");
        StringAssert.Contains(failure.Message, "dp.acquisition.test.area");
    }

    /// <summary>重复Module身份拒绝发布。</summary>
    [TestMethod]
    public void DuplicateModuleExtensionId_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[]
            {
                Module("module.one", Registration()),
                Module("module.one", Registration(typeId: "dp.acquisition.test.line"))
            }));

        StringAssert.Contains(failure.Message, "Module 身份重复");
    }

    /// <summary>空工厂在冻结前被拒绝。</summary>
    [TestMethod]
    public void MissingFactory_IsRejected()
    {
        var registration = new VisionAcquisitionTypeRegistration(
            "dp.acquisition.test.area",
            "dp.vision.test",
            "1.0.0",
            EVisionAcquisitionKind.AreaScan,
            1,
            "测试",
            new VisionAcquisitionTypeCapabilities(SupportsFreeRun: true),
            null!);

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { Module("module.one", registration) }));

        StringAssert.Contains(failure.Message, "缺少设备Adapter工厂");
    }

    /// <summary>未知采集形态在冻结前被拒绝。</summary>
    [TestMethod]
    public void UnknownKind_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { Module("module.one", Registration(kind: (EVisionAcquisitionKind)42)) }));

        StringAssert.Contains(failure.Message, "未知的采集形态");
    }

    /// <summary>配置版本必须为正整数。</summary>
    [TestMethod]
    public void InvalidDeviceSettingsVersion_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { Module("module.one", Registration(settingsVersion: 0)) }));

        StringAssert.Contains(failure.Message, "配置版本");
    }

    /// <summary>缺失版本在冻结前被拒绝。</summary>
    [TestMethod]
    public void MissingVersion_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { Module("module.one", Registration(version: " ")) }));

        StringAssert.Contains(failure.Message, "缺少版本");
    }

    /// <summary>没有任何取图路径的能力声明在冻结前被拒绝。</summary>
    [TestMethod]
    public void CapabilitiesWithoutAnyCapturePath_AreRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[]
            {
                Module("module.one", Registration(capabilities: new VisionAcquisitionTypeCapabilities()))
            }));

        StringAssert.Contains(failure.Message, "不包含任何取图路径");
    }

    /// <summary>声明外部触发但没有完整帧回调在冻结前被拒绝：外部触发Source必须使用OnConnect流。</summary>
    [TestMethod]
    public void ExternalTriggerWithoutCompleteFrameCallback_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[]
            {
                Module("module.one", Registration(capabilities: new VisionAcquisitionTypeCapabilities(
                    SupportsExternalTrigger: true)))
            }));

        StringAssert.Contains(failure.Message, "外部触发");
    }

    /// <summary>Module贡献中途抛异常时丢弃候选，已发布的正式Catalog保持不变（先校验后发布）。</summary>
    [TestMethod]
    public void ContributeFailure_DoesNotLeaveHalfCatalog()
    {
        var frozen = _composer.Compose(new[] { Module("module.one", Registration()) });
        var typeCountBefore = frozen.Types.Count;
        var catalogIdBefore = frozen.CatalogId;

        var failing = new FakeAcquisitionDriverModule(
            "module.two",
            Registration(typeId: "dp.acquisition.test.line"))
        {
            ContributeFailure = new InvalidOperationException("注入的贡献失败。")
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => _composer.Compose(
            new[] { Module("module.one", Registration()), failing }));

        Assert.AreEqual(typeCountBefore, frozen.Types.Count);
        Assert.AreEqual(catalogIdBefore, frozen.CatalogId);
        Assert.IsTrue(frozen.TryGetType("dp.acquisition.test.area", out _));
    }

    /// <summary>相同输入产生相同CatalogId；Type清单变化必须产生不同CatalogId。</summary>
    [TestMethod]
    public void CatalogId_IsStableForSameContentAndChangesWithTypeSet()
    {
        var first = _composer.Compose(new[] { Module("module.one", Registration()) });
        var second = _composer.Compose(new[] { Module("module.one", Registration()) });
        var changed = _composer.Compose(
            new[]
            {
                Module("module.one", Registration()),
                Module("module.two", Registration(typeId: "dp.acquisition.test.line"))
            });

        Assert.AreEqual(first.CatalogId, second.CatalogId);
        Assert.AreNotEqual(first.CatalogId, changed.CatalogId);
        Assert.AreEqual(2, changed.Types.Count);
    }

    /// <summary>能力变化必须进入Catalog身份，否则改能力不会产生新Catalog。</summary>
    [TestMethod]
    public void CatalogId_ChangesWhenCapabilitiesChange()
    {
        var freeRun = _composer.Compose(
            new[] { Module("module.one", Registration()) });
        var callbackOnly = _composer.Compose(
            new[]
            {
                Module("module.one", Registration(
                    capabilities: new VisionAcquisitionTypeCapabilities(SupportsCompleteFrameCallback: true)))
            });

        Assert.AreNotEqual(freeRun.CatalogId, callbackOnly.CatalogId);
    }

    /// <summary>空设备配置解析器在冻结前被拒绝（V2-2：机器配置无法解析其deviceSettings）。</summary>
    [TestMethod]
    public void MissingDeviceSettingsParser_IsRejected()
    {
        var registration = new VisionAcquisitionTypeRegistration(
            "dp.acquisition.test.area",
            "dp.vision.test",
            "1.0.0",
            EVisionAcquisitionKind.AreaScan,
            1,
            "测试",
            new VisionAcquisitionTypeCapabilities(SupportsFreeRun: true),
            () => FakeVisionProvider.WithDevices("dp.vision.test.provider"),
            null);

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => _composer.Compose(
            new[] { Module("module.one", registration) }));

        StringAssert.Contains(failure.Message, "缺少设备配置解析器");
    }

    private static FakeAcquisitionDriverModule Module(
        string extensionId,
        params VisionAcquisitionTypeRegistration[] registrations) =>
        new FakeAcquisitionDriverModule(extensionId, registrations);

    private static VisionAcquisitionTypeRegistration Registration(
        string typeId = "dp.acquisition.test.area",
        string pluginId = "dp.vision.test",
        string version = "1.0.0",
        EVisionAcquisitionKind kind = EVisionAcquisitionKind.AreaScan,
        int settingsVersion = 1,
        VisionAcquisitionTypeCapabilities? capabilities = null,
        Func<IVisionAcquisitionProvider>? factory = null) =>
        new VisionAcquisitionTypeRegistration(
            typeId,
            pluginId,
            version,
            kind,
            settingsVersion,
            "测试",
            capabilities ?? new VisionAcquisitionTypeCapabilities(SupportsFreeRun: true),
            factory ?? (() => FakeVisionProvider.WithDevices("dp.vision.test.provider")),
            TestDeviceSettingsParser.Parse);
}
