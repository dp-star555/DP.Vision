using System;
using System.Linq;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using DP.Vision.Basler;
using DP.Vision.Halcon;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Integration.Tests;

/// <summary>
/// 实施基线§13阶段E的验收：**同一进程内两个真实厂商Provider各管理自己的设备**。
///
/// 这是"多Provider架构是否真的成立"的关键回归：只有第二个真实Adapter接入后，
/// 多Provider接口才算经过真实变化验证。两个Provider都只通过中立契约被使用，
/// 全程不需要相机、也不需要任何一个厂商SDK在场。
/// </summary>
/// <remarks>
/// 组合走的是**机器配置路径**（Driver Module → TypeCatalog → 机器相机定义 → 不可变组合），
/// 也就是生产装配的那一条。设备字段只写在每个逻辑源的 <c>deviceSettings</c> 里，
/// 由各厂商自己的解析器解析后随公共绑定发布；因此本组用例同时守住
/// "deviceSettings 是设备配置唯一来源"这条不变式——它不需要任何插件私有配置。
/// </remarks>
[TestClass]
public sealed class CrossVendorProviderCoexistenceTests
{
    private const string HalconSourceId = "Camera.Top";
    private const string BaslerSourceId = "Camera.Side";

    /// <summary>
    /// 机器配置路径下组合里的 Provider 键就是 AcquisitionTypeId（一个 Type 一个 Provider 注册），
    /// 而 Provider 自身对外报告的是 <c>dp.vision.*</c> 插件身份。两者都要能对上，不能混用。
    /// </summary>
    private const string HalconCompositionKey = HalconAcquisitionDriverModule.AreaScanTypeId;

    private const string BaslerCompositionKey = BaslerAcquisitionDriverModule.AreaScanTypeId;

    /// <summary>两个真实Provider在同一组合里并存，并按SourceId各自路由到自己的设备。</summary>
    [TestMethod]
    public async Task TwoRealVendors_CoexistAndRouteBySourceId()
    {
        var composition = ComposeTwoVendors();

        Assert.AreEqual(2, composition.Sources.Count, "两个逻辑源都必须被发布。");
        Assert.AreEqual(BaslerSourceId, composition.Sources[0].SourceId);
        Assert.AreEqual(HalconSourceId, composition.Sources[1].SourceId);
        Assert.AreEqual(BaslerCompositionKey, composition.Sources[0].ProviderId);
        Assert.AreEqual(HalconCompositionKey, composition.Sources[1].ProviderId);

        // 公共层只按组合键取工厂，不需要知道任何厂商类型。
        Assert.IsTrue(composition.TryGetProvider(HalconCompositionKey, out var halconRegistration));
        Assert.IsTrue(composition.TryGetProvider(BaslerCompositionKey, out var baslerRegistration));

        await using var halconProvider = halconRegistration!.Factory();
        await using var baslerProvider = baslerRegistration!.Factory();

        Assert.AreEqual(HalconAcquisitionProvider.ProviderIdentity, halconProvider.ProviderId);
        Assert.AreEqual(BaslerAcquisitionProvider.ProviderIdentity, baslerProvider.ProviderId);

        // 绑定取自组合本身：公共绑定携带的插件私有状态正是打开设备所需的那一份。
        await using var halconDevice = await halconProvider.OpenAsync(OpenBinding(composition, HalconSourceId), default);
        await using var baslerDevice = await baslerProvider.OpenAsync(OpenBinding(composition, BaslerSourceId), default);

        // 各自报告自己的身份与规范资源键，没有串台。
        Assert.AreEqual(HalconAcquisitionProvider.ProviderIdentity, halconDevice.Identity.ProviderId);
        Assert.AreEqual("camera:serial:DEMO0001", halconDevice.Identity.CanonicalKey);
        Assert.AreEqual(BaslerAcquisitionProvider.ProviderIdentity, baslerDevice.Identity.ProviderId);
        Assert.AreEqual("camera:serial:40123456", baslerDevice.Identity.CanonicalKey);
        Assert.AreNotEqual(halconDevice.Identity.ProviderBindingId, baslerDevice.Identity.ProviderBindingId);
    }

    /// <summary>
    /// 厂商解析器写出的私有绑定不可互换：把 HALCON 的绑定交给 Basler Provider 必须明确失败，
    /// 而不是被静默当成"没有配置"。
    /// </summary>
    [TestMethod]
    public async Task Provider_RejectsAnotherVendorsBinding()
    {
        var composition = ComposeTwoVendors();
        Assert.IsTrue(composition.TryGetProvider(HalconCompositionKey, out var halconRegistration));
        Assert.IsTrue(composition.TryGetProvider(BaslerCompositionKey, out var baslerRegistration));

        await using var halconProvider = halconRegistration!.Factory();
        await using var baslerProvider = baslerRegistration!.Factory();

        var halconBinding = OpenBinding(composition, HalconSourceId);
        var baslerBinding = OpenBinding(composition, BaslerSourceId);

        Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => halconProvider.OpenAsync(baslerBinding, default).AsTask().GetAwaiter().GetResult());
        Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => baslerProvider.OpenAsync(halconBinding, default).AsTask().GetAwaiter().GetResult());

        // 完全没有私有状态的绑定同样不能被猜成"某台设备"。
        Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => baslerProvider
                .OpenAsync(new VisionAcquisitionProviderBinding(baslerBinding.ProviderBindingId), default)
                .AsTask().GetAwaiter().GetResult());
    }

    /// <summary>组合清单要能作为运行制品导出：两个Provider的实现版本都在里面。</summary>
    [TestMethod]
    public void CompositionManifest_ListsBothVendorImplementations()
    {
        var manifest = string.Join("；", ComposeTwoVendors().ProviderManifest);

        StringAssert.Contains(manifest, HalconCompositionKey);
        StringAssert.Contains(manifest, BaslerCompositionKey);
        StringAssert.Contains(manifest, BaslerAcquisitionDriverModule.TypeVersion);
    }

    /// <summary>
    /// 两个真实Provider加上两个公共Source绑定构成的机器配置；设备字段只在 deviceSettings 里。
    /// </summary>
    /// <returns>不可变组合快照。</returns>
    private static VisionAcquisitionProviderComposition ComposeTwoVendors()
    {
        var catalog = new VisionAcquisitionTypeCatalogComposer().Compose(
            new IVisionAcquisitionDriverModule[]
            {
                new HalconAcquisitionDriverModule(),
                new BaslerAcquisitionDriverModule()
            });

        var cameras = VisionAcquisitionMachineConfigurationParser.Parse(
            "["
            + "{\"sourceId\":\"" + HalconSourceId + "\","
            + "\"acquisitionType\":\"" + HalconAcquisitionDriverModule.AreaScanTypeId + "\","
            + "\"settingsVersion\":" + HalconAcquisitionDriverModule.DeviceSettingsVersion + ","
            + "\"deviceSettings\":{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\","
            + "\"serialNumber\":\"DEMO0001\"}},"
            + "{\"sourceId\":\"" + BaslerSourceId + "\","
            + "\"acquisitionType\":\"" + BaslerAcquisitionDriverModule.AreaScanTypeId + "\","
            + "\"settingsVersion\":" + BaslerAcquisitionDriverModule.DeviceSettingsVersion + ","
            + "\"deviceSettings\":{\"serialNumber\":\"40123456\"}}"
            + "]");

        return new VisionAcquisitionMachineConfigurationComposer().Compose(catalog, cameras);
    }

    /// <summary>从组合里取出某个逻辑源的打开绑定；公共层只转交私有状态，不解释它。</summary>
    private static VisionAcquisitionProviderBinding OpenBinding(
        VisionAcquisitionProviderComposition composition,
        string sourceId)
    {
        var source = composition.Sources.Single(item => item.SourceId == sourceId);
        Assert.IsNotNull(source.ProviderState, $"逻辑源 {sourceId} 的 deviceSettings 没有解析出私有绑定。");
        return new VisionAcquisitionProviderBinding(source.ProviderBindingId, source.ProviderState);
    }
}
