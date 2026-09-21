using System;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// V2-9 机器配置修订存储：候选校验、发布、历史与回滚。
/// <para>
/// 核心不变量是"历史只追加、不移动指针"：回滚产生新修订而不是改写历史，
/// 因此"当前生效的是哪一版"永远是历史里修订号最大的那一条。
/// </para>
/// </summary>
[TestClass]
public sealed class MachineConfigurationRevisionStoreTests
{
    private const string AreaTypeId = "dp.acquisition.test.area";

    private readonly VisionAcquisitionTypeCatalogComposer _catalogComposer = new();

    private VisionAcquisitionTypeCatalog Catalog() =>
        _catalogComposer.Compose(new IVisionAcquisitionDriverModule[] { new ConfigurableAcquisitionDriverModule() });

    private static string Configuration(string serialNumber) =>
        "{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + AreaTypeId
        + "\",\"settingsVersion\":1,\"deviceSettings\":{\"serialNumber\":\"" + serialNumber + "\"}}";

    /// <summary>候选经校验后发布为第 1 号修订；发布后候选被清空，避免同一内容被连点两次发布成两条修订。</summary>
    [TestMethod]
    public void Publish_AppendsFirstRevisionAndClearsCandidate()
    {
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(Catalog());
        Assert.IsNull(store.Current, "尚未发布时不得有当前修订。");
        Assert.AreEqual(0, store.History().Count);

        store.SetCandidate(Configuration("SN-1"));
        Assert.IsTrue(store.HasCandidate);

        var validation = store.ValidateCandidate();
        Assert.IsTrue(validation.IsValid, string.Join("；", validation.Errors));
        Assert.IsFalse(string.IsNullOrWhiteSpace(validation.CompositionId));
        CollectionAssert.AreEqual(new[] { "Camera.Top" }, validation.SourceIds!.ToArray());
        Assert.AreEqual(0, validation.UnavailableSourceIds!.Count);

        var revision = store.Publish();
        Assert.AreEqual(1, revision.Revision);
        Assert.AreEqual(validation.CompositionId, revision.CompositionId, "发布必须用被校验的那份快照。");
        Assert.AreEqual(Configuration("SN-1"), revision.ConfigurationJson);
        Assert.IsFalse(revision.IsRollback);

        Assert.IsFalse(store.HasCandidate, "发布后候选必须清空。");
        Assert.AreEqual(1, store.History().Count);
        Assert.AreEqual(1, store.Current!.Revision);
    }

    /// <summary>构造时给出初始配置等价于"设置候选 + 发布"，直接产生第 1 号修订。</summary>
    [TestMethod]
    public void ConstructorWithInitialConfiguration_PublishesFirstRevision()
    {
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(Catalog(), Configuration("SN-9"));

        Assert.AreEqual(1, store.Current!.Revision);
        Assert.AreEqual(1, store.History().Count);
        Assert.IsFalse(store.HasCandidate);
    }

    /// <summary>初始配置非法时构造失败，且不留下半发布状态。</summary>
    [TestMethod]
    public void ConstructorWithInvalidConfiguration_Throws()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            new VisionAcquisitionMachineConfigurationRevisionStore(Catalog(), "{不是JSON"));
    }

    /// <summary>校验失败时给出错误文本但不抛异常；此时发布必须明确失败且历史不变。</summary>
    [TestMethod]
    public void InvalidCandidate_ReportsErrorAndRefusesPublish()
    {
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(Catalog());
        store.SetCandidate(
            "{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + AreaTypeId
            + "\",\"settingsVersion\":1,\"deviceSettings\":{\"serial\":\"SN-1\"}}");

        var validation = store.ValidateCandidate();
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(1, validation.Errors.Count);
        StringAssert.Contains(validation.Errors[0], "serialNumber", "必须把 Plugin 的解析失败原因透出来。");
        Assert.AreEqual(1, store.CandidateErrors.Count);

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => store.Publish());
        StringAssert.Contains(failure.Message, "serialNumber");
        Assert.AreEqual(0, store.History().Count, "校验不通过时不得产生任何修订。");
    }

    /// <summary>尚未设置候选时校验与发布都必须明确失败。</summary>
    [TestMethod]
    public void WithoutCandidate_ValidationAndPublishFail()
    {
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(Catalog());

        var validation = store.ValidateCandidate();
        Assert.IsFalse(validation.IsValid);
        StringAssert.Contains(validation.Errors[0], "尚未设置候选");

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => store.Publish());
        StringAssert.Contains(failure.Message, "尚未设置候选");
    }

    /// <summary>空配置不得作为候选；空候选会让"校验通过"变成一句空话。</summary>
    [TestMethod]
    public void EmptyCandidate_IsRejected()
    {
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(Catalog());
        Assert.ThrowsExactly<ArgumentException>(() => store.SetCandidate("   "));
    }

    /// <summary>私有配置变化产生新的修订与新的组合身份；修订号连续递增。</summary>
    [TestMethod]
    public void EachPublish_AppendsNextRevisionAndChangesCompositionOnSettingsChange()
    {
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(Catalog());

        store.SetCandidate(Configuration("SN-1"));
        var first = store.Publish();
        store.SetCandidate(Configuration("SN-2"));
        var second = store.Publish();

        Assert.AreEqual(1, first.Revision);
        Assert.AreEqual(2, second.Revision);
        Assert.AreNotEqual(first.CompositionId, second.CompositionId, "改序列号必须产生新的组合身份。");
        Assert.AreEqual(2, store.Current!.Revision, "当前修订恒为最后一条。");

        var history = store.History();
        CollectionAssert.AreEqual(new[] { 1, 2 }, history.Select(item => item.Revision).ToArray(), "历史按修订号升序。");
        Assert.AreEqual(second.CompositionId, store.Find(2)!.CompositionId);
        Assert.IsNull(store.Find(99));
        Assert.AreEqual(Catalog().CatalogId, first.CatalogId);
    }

    /// <summary>
    /// 回滚追加一条内容等于目标修订的新修订，不删除中间修订：
    /// 历史是审计记录，删掉中间修订会让"当时机器上跑的是什么"无从解释。
    /// </summary>
    [TestMethod]
    public void Rollback_AppendsNewRevisionWithTargetContent()
    {
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(Catalog());
        store.SetCandidate(Configuration("SN-1"));
        var first = store.Publish();
        store.SetCandidate(Configuration("SN-2"));
        store.Publish();

        var rolledBack = store.Rollback(1);

        Assert.AreEqual(3, rolledBack.Revision, "回滚产生新修订，而不是回到修订 1。");
        Assert.AreEqual(Configuration("SN-1"), rolledBack.ConfigurationJson);
        Assert.AreEqual(first.CompositionId, rolledBack.CompositionId, "回滚后的组合身份必须等于目标修订。");
        Assert.AreEqual(1, rolledBack.RestoredFromRevision);
        Assert.IsTrue(rolledBack.IsRollback);
        Assert.AreEqual(3, store.Current!.Revision, "当前修订为回滚产生的新修订。");

        var history = store.History();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, history.Select(item => item.Revision).ToArray());
        Assert.AreEqual(Configuration("SN-2"), store.Find(2)!.ConfigurationJson, "中间修订不得被删除或改写。");
    }

    /// <summary>回滚到不存在的修订明确失败，并列出可用修订号。</summary>
    [TestMethod]
    public void Rollback_UnknownRevision_Throws()
    {
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(Catalog());
        store.SetCandidate(Configuration("SN-1"));
        store.Publish();

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() => store.Rollback(7));
        StringAssert.Contains(failure.Message, "修订 7 不存在");
        StringAssert.Contains(failure.Message, "可用修订号：1");
        Assert.AreEqual(1, store.History().Count);
    }

    /// <summary>
    /// 未安装 Type 的 Source 仍然是合法配置：校验通过，但必须显式列出不可用源。
    /// 现场最容易出错的正是"插件没部署"被当成"相机没接"。
    /// </summary>
    [TestMethod]
    public void UninstalledType_IsValidButReportsUnavailableSource()
    {
        var emptyCatalog = _catalogComposer.Compose(Array.Empty<IVisionAcquisitionDriverModule>());
        var store = new VisionAcquisitionMachineConfigurationRevisionStore(emptyCatalog);
        store.SetCandidate(Configuration("SN-1"));

        var validation = store.ValidateCandidate();
        Assert.IsTrue(validation.IsValid, "未安装 Type 不构成配置错误，只让 Source 不可用。");
        CollectionAssert.AreEqual(new[] { "Camera.Top" }, validation.UnavailableSourceIds!.ToArray());
        Assert.AreEqual(0, validation.SourceIds!.Count);

        var revision = store.Publish();
        Assert.AreEqual(1, revision.Revision);
    }
}