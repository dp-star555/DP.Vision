using System;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// V2-9 设备发现：pylon 枚举结果到公共候选描述的映射。
/// <para>
/// 这里不碰真实枚举（它依赖 pylon 原生运行时是否存在），只锁定两条可判定的规则：
/// 候选绑定身份必须与手工填写 deviceSettings 得到的绑定完全一致；
/// 拿不到稳定身份的相机必须被跳过，而不是伪造一个会随上电顺序变化的身份。
/// </para>
/// </summary>
[TestClass]
public sealed class BaslerDeviceDiscoveryTests
{
    /// <summary>Provider 暴露发现能力，宿主可据此列出候选设备。</summary>
    [TestMethod]
    public void Provider_ImplementsDeviceDiscovery()
    {
        Assert.IsTrue(
            new BaslerAcquisitionProvider() is IVisionDeviceDiscovery,
            "Basler Provider 必须提供设备发现能力，否则界面无法生成候选绑定。");
    }

    /// <summary>有序列号时以序列号为绑定身份与资源键，与 deviceSettings 解析结果一致。</summary>
    [TestMethod]
    public void DiscoveredCameraWithSerial_MatchesDeviceSettingsParser()
    {
        var descriptor = BaslerDeviceDiscovery.ToDescriptors(new[]
        {
            new BaslerDiscoveredCamera("40123456", "cam-top", "acA1920-40gm", "Basler", "Basler acA1920 (cam-top)")
        }).Single();

        var parsed = BaslerDeviceSettingsParser.Parse("{\"serialNumber\":\"40123456\"}");
        Assert.AreEqual(BaslerAcquisitionProvider.ProviderIdentity, descriptor.ProviderId);
        Assert.AreEqual(parsed.ProviderBindingId, descriptor.ProviderBindingId);
        Assert.AreEqual(parsed.ResourceKey, descriptor.CanonicalKey);
        Assert.AreEqual("Basler acA1920 (cam-top)", descriptor.DisplayName);
        Assert.AreEqual("Basler", descriptor.VendorName);
        Assert.AreEqual("acA1920-40gm", descriptor.ModelName);
        Assert.AreEqual("40123456", descriptor.SerialNumber);
    }

    /// <summary>没有序列号时退到用户自定义名，资源键同样与 deviceSettings 解析结果一致。</summary>
    [TestMethod]
    public void DiscoveredCameraWithoutSerial_FallsBackToUserDefinedName()
    {
        var descriptor = BaslerDeviceDiscovery.ToDescriptors(new[]
        {
            new BaslerDiscoveredCamera(null, "cam-side", null, null, null)
        }).Single();

        var parsed = BaslerDeviceSettingsParser.Parse("{\"userDefinedName\":\"cam-side\"}");
        Assert.AreEqual(parsed.ProviderBindingId, descriptor.ProviderBindingId);
        Assert.AreEqual("camera:name:cam-side", descriptor.CanonicalKey);
        Assert.AreEqual(parsed.ResourceKey, descriptor.CanonicalKey);
        Assert.AreEqual("cam-side", descriptor.DisplayName, "没有友好名与型号时以绑定身份兜底，不能出现空显示名。");
        Assert.IsNull(descriptor.VendorName);
        Assert.IsNull(descriptor.ModelName);
        Assert.IsNull(descriptor.SerialNumber);
    }

    /// <summary>序列号与自定义名都拿不到时跳过该相机：按枚举顺序编号会在换一次上电顺序后指向别的设备。</summary>
    [TestMethod]
    public void CameraWithoutStableIdentity_IsSkipped()
    {
        var descriptors = BaslerDeviceDiscovery.ToDescriptors(new[]
        {
            new BaslerDiscoveredCamera(null, null, "acA1920-40gm", "Basler", "相机 1"),
            new BaslerDiscoveredCamera("   ", null, null, null, null),
            new BaslerDiscoveredCamera("40123456", null, null, null, null)
        });

        Assert.AreEqual(1, descriptors.Count);
        Assert.AreEqual("40123456", descriptors[0].ProviderBindingId);
    }

    /// <summary>排序按规范资源键，因此枚举顺序抖动不会让候选列表每次换位。</summary>
    [TestMethod]
    public void Descriptors_AreOrderedByCanonicalKey()
    {
        var first = BaslerDeviceDiscovery.ToDescriptors(new[]
        {
            new BaslerDiscoveredCamera("SN-2", null, null, null, null),
            new BaslerDiscoveredCamera("SN-1", null, null, null, null)
        });
        var reversed = BaslerDeviceDiscovery.ToDescriptors(new[]
        {
            new BaslerDiscoveredCamera("SN-1", null, null, null, null),
            new BaslerDiscoveredCamera("SN-2", null, null, null, null)
        });

        CollectionAssert.AreEqual(
            new[] { "camera:serial:SN-1", "camera:serial:SN-2" },
            first.Select(item => item.CanonicalKey).ToArray());
        CollectionAssert.AreEqual(
            first.Select(item => item.ProviderBindingId).ToArray(),
            reversed.Select(item => item.ProviderBindingId).ToArray(),
            "两次枚举必须给出同一顺序。");
    }

    /// <summary>枚举结果为空时不得抛出；"现场没有相机"是正常结果。</summary>
    [TestMethod]
    public void EmptyEnumeration_ReturnsEmptyDescriptors()
    {
        Assert.AreEqual(0, BaslerDeviceDiscovery.ToDescriptors(Array.Empty<BaslerDiscoveredCamera>()).Count);
        Assert.ThrowsExactly<ArgumentNullException>(() => BaslerDeviceDiscovery.ToDescriptors(null!));
    }
}