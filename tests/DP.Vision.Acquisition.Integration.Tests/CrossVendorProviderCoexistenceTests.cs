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
/// 全程不需要相机、也不需要任何一个厂商SDK在场（设备身份由Provider私有绑定决定）。
/// </summary>
[TestClass]
public sealed class CrossVendorProviderCoexistenceTests
{
    private const string HalconBindingId = "top-camera";
    private const string BaslerBindingId = "side-camera";

    /// <summary>两个真实Provider在同一组合里并存，并按SourceId各自路由到自己的设备。</summary>
    [TestMethod]
    public async Task TwoRealVendors_CoexistAndRouteBySourceId()
    {
        var composition = ComposeTwoVendors();

        Assert.AreEqual(2, composition.Sources.Count, "两个逻辑源都必须被发布。");
        Assert.AreEqual("Camera.Side", composition.Sources[0].SourceId);
        Assert.AreEqual("Camera.Top", composition.Sources[1].SourceId);

        // 公共层只按ProviderId取工厂，不需要知道任何厂商类型。
        Assert.IsTrue(composition.TryGetProvider(HalconAcquisitionProvider.ProviderIdentity, out var halconRegistration));
        Assert.IsTrue(composition.TryGetProvider(BaslerAcquisitionProvider.ProviderIdentity, out var baslerRegistration));

        await using var halconProvider = halconRegistration!.Factory();
        await using var baslerProvider = baslerRegistration!.Factory();

        Assert.AreEqual(HalconAcquisitionProvider.ProviderIdentity, halconProvider.ProviderId);
        Assert.AreEqual(BaslerAcquisitionProvider.ProviderIdentity, baslerProvider.ProviderId);

        await using var halconDevice = await halconProvider.OpenAsync(HalconBindingId, default);
        await using var baslerDevice = await baslerProvider.OpenAsync(BaslerBindingId, default);

        // 各自报告自己的身份与规范资源键，没有串台。
        Assert.AreEqual(HalconAcquisitionProvider.ProviderIdentity, halconDevice.Identity.ProviderId);
        Assert.AreEqual("camera:serial:DEMO0001", halconDevice.Identity.CanonicalKey);
        Assert.AreEqual(BaslerAcquisitionProvider.ProviderIdentity, baslerDevice.Identity.ProviderId);
        Assert.AreEqual("camera:serial:40123456", baslerDevice.Identity.CanonicalKey);
        Assert.AreNotEqual(halconDevice.Identity.ProviderBindingId, baslerDevice.Identity.ProviderBindingId);
    }

    /// <summary>Provider只能打开自己私有配置里的绑定，不接受另一个厂商的绑定身份。</summary>
    [TestMethod]
    public async Task Provider_DoesNotResolveAnotherVendorsBinding()
    {
        var composition = ComposeTwoVendors();
        Assert.IsTrue(composition.TryGetProvider(HalconAcquisitionProvider.ProviderIdentity, out var halconRegistration));
        Assert.IsTrue(composition.TryGetProvider(BaslerAcquisitionProvider.ProviderIdentity, out var baslerRegistration));

        await using var halconProvider = halconRegistration!.Factory();
        await using var baslerProvider = baslerRegistration!.Factory();

        Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => halconProvider.OpenAsync(BaslerBindingId, default).AsTask().GetAwaiter().GetResult());
        Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => baslerProvider.OpenAsync(HalconBindingId, default).AsTask().GetAwaiter().GetResult());
    }

    /// <summary>把Source绑到未参与本次组合的Provider必须在组合阶段失败，而不是留到运行时。</summary>
    [TestMethod]
    public void Composition_RejectsSourceBoundToUndeclaredProvider()
    {
        var modules = new IVisionAcquisitionProviderModule[]
        {
            new BaslerAcquisitionProviderModule(new[] { new BaslerAcquisitionBinding(BaslerBindingId, serialNumber: "40123456") })
        };

        Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            new VisionAcquisitionProviderComposer().Compose(
                modules,
                new[]
                {
                    new VisionAcquisitionSourceBinding(
                        "Camera.Top",
                        HalconAcquisitionProvider.ProviderIdentity,
                        HalconBindingId,
                        "camera:serial:DEMO0001")
                }));
    }

    /// <summary>组合清单要能作为运行制品导出：两个Provider的实现版本都在里面。</summary>
    [TestMethod]
    public void CompositionManifest_ListsBothVendorImplementations()
    {
        var manifest = string.Join("；", ComposeTwoVendors().ProviderManifest);

        StringAssert.Contains(manifest, BaslerAcquisitionProvider.ProviderIdentity);
        StringAssert.Contains(manifest, HalconAcquisitionProvider.ProviderIdentity);
        StringAssert.Contains(manifest, BaslerAcquisitionProviderModule.ProviderVersion);
    }

    /// <summary>两个真实Provider加上两个公共Source绑定构成的机器配置。</summary>
    /// <returns>不可变组合快照。</returns>
    private static VisionAcquisitionProviderComposition ComposeTwoVendors()
    {
        var halconModule = new HalconAcquisitionProviderModule(new[]
        {
            new HalconAcquisitionBinding(HalconBindingId, "GigEVision2", "cam-top", serialNumber: "DEMO0001")
        });
        var baslerModule = new BaslerAcquisitionProviderModule(new[]
        {
            new BaslerAcquisitionBinding(BaslerBindingId, serialNumber: "40123456")
        });

        return new VisionAcquisitionProviderComposer().Compose(
            new IVisionAcquisitionProviderModule[] { halconModule, baslerModule },
            new[]
            {
                new VisionAcquisitionSourceBinding(
                    "Camera.Top",
                    HalconAcquisitionProvider.ProviderIdentity,
                    HalconBindingId,
                    "camera:serial:DEMO0001"),
                new VisionAcquisitionSourceBinding(
                    "Camera.Side",
                    BaslerAcquisitionProvider.ProviderIdentity,
                    BaslerBindingId,
                    "camera:serial:40123456")
            });
    }
}
