using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// V2-9 设备发现：<c>info_boards</c> 返回串的解析与候选映射。
/// <para>
/// HALCON 的返回串格式是 <c>| token:value | token:value |</c>，其中 <c>device:</c> 条目可直接作为
/// <c>open_framegrabber</c> 的 Device 参数。这些用例用真实形态的样例串锁定解析规则，
/// 因此不依赖现场是否接着相机，也不依赖许可证。
/// </para>
/// </summary>
[TestClass]
public sealed class HalconDeviceDiscoveryTests
{
    private const string GigEBoard =
        "device:GigE:001122334455|unique_name:GigE:001122334455|user_name:cam-top"
        + "|interface:GigEVision2|producer:Basler|device_ip:192.168.1.10"
        + "|device_sn:40123456|vendor:Basler|model:acA1920-40gm";

    /// <summary>Provider 暴露发现能力，且发现不依赖已发布绑定。</summary>
    [TestMethod]
    public void Provider_ImplementsDeviceDiscovery()
    {
        Assert.IsTrue(
            new HalconAcquisitionProvider() is IVisionDeviceDiscovery,
            "HALCON Provider 必须提供设备发现能力，否则界面无法生成候选绑定。");
    }

    /// <summary>默认查询工业相机常用的三个采集接口；HALCON 没有"列出已安装接口"的查询。</summary>
    [TestMethod]
    public void DefaultInterfaceNames_CoverIndustrialCameras()
    {
        CollectionAssert.AreEqual(
            new[] { "GigEVision2", "USB3Vision", "GenICamTL" },
            HalconDeviceEnumeration.DefaultInterfaceNames.ToArray());
    }

    /// <summary>按权威 token 读取设备名、序列号、用户自定义名、型号与厂商。</summary>
    [TestMethod]
    public void Parse_ReadsAuthoritativeTokens()
    {
        var device = HalconBoardInfo.Parse(GigEBoard, "GigEVision2");

        Assert.IsNotNull(device);
        Assert.AreEqual("GigE:001122334455", device!.DeviceName, "device: 条目才是可写入 deviceSettings 的设备名。");
        Assert.AreEqual("40123456", device.SerialNumber);
        Assert.AreEqual("cam-top", device.UserDefinedName);
        Assert.AreEqual("acA1920-40gm", device.ModelName);
        Assert.AreEqual("Basler", device.VendorName);
        Assert.AreEqual("GigEVision2", device.InterfaceName);
    }

    /// <summary>首尾竖线与无冒号的条目都必须容忍，否则真实返回串会被整条丢掉。</summary>
    [TestMethod]
    public void Parse_ToleratesPipesAndUnknownEntries()
    {
        var device = HalconBoardInfo.Parse("|device:cam-top|broken-entry||interface:GigEVision2|", "GigEVision2");

        Assert.IsNotNull(device);
        Assert.AreEqual("cam-top", device!.DeviceName);
        Assert.IsNull(device.SerialNumber);
    }

    /// <summary>
    /// 不带 token 与竖线的纯字符串按文档直接视为设备 ID；
    /// 带冒号的裸设备串同样保真，不能被误当成"未知 token 的键值对"而丢掉。
    /// </summary>
    [TestMethod]
    public void Parse_TreatsPlainStringAsDeviceId()
    {
        var plain = HalconBoardInfo.Parse("cam-0001", "GigEVision2");
        var withColon = HalconBoardInfo.Parse("GigE:001122334455", "GigEVision2");

        Assert.AreEqual("cam-0001", plain!.DeviceName);
        Assert.AreEqual("GigE:001122334455", withColon!.DeviceName);
    }

    /// <summary>设备串或接口名缺失时返回空，由调用方跳过而不是伪造身份。</summary>
    [TestMethod]
    public void Parse_ReturnsNullWithoutDeviceOrInterface()
    {
        Assert.IsNull(HalconBoardInfo.Parse(null, "GigEVision2"));
        Assert.IsNull(HalconBoardInfo.Parse("   ", "GigEVision2"));
        Assert.IsNull(HalconBoardInfo.Parse(GigEBoard, null));
    }

    /// <summary>有序列号时以序列号作为绑定身份与资源键，与 deviceSettings 解析结果一致。</summary>
    [TestMethod]
    public void DescriptorsWithSerial_MatchDeviceSettingsParser()
    {
        var descriptor = HalconBoardInfo.ToDescriptors(new[] { HalconBoardInfo.Parse(GigEBoard, "GigEVision2")! }).Single();

        var parsed = HalconDeviceSettingsParser.Parse(
            "{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"GigE:001122334455\",\"serialNumber\":\"40123456\"}");
        Assert.AreEqual(HalconAcquisitionProvider.ProviderIdentity, descriptor.ProviderId);
        Assert.AreEqual(parsed.ProviderBindingId, descriptor.ProviderBindingId);
        Assert.AreEqual(parsed.ResourceKey, descriptor.CanonicalKey);
        Assert.AreEqual("cam-top", descriptor.DisplayName);
        Assert.AreEqual("40123456", descriptor.SerialNumber);
    }

    /// <summary>没有序列号时以"接口名|设备名"作为绑定身份，资源键同样与 deviceSettings 一致。</summary>
    [TestMethod]
    public void DescriptorsWithoutSerial_MatchDeviceSettingsParser()
    {
        var discovered = new HalconDiscoveredDevice("GigEVision2", "GigE:001122334455", null, null, null, null);
        var descriptor = HalconBoardInfo.ToDescriptors(new[] { discovered }).Single();

        var parsed = HalconDeviceSettingsParser.Parse(
            "{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"GigE:001122334455\"}");
        Assert.AreEqual(parsed.ProviderBindingId, descriptor.ProviderBindingId);
        Assert.AreEqual("halcon:camera:GigEVision2|GigE:001122334455", descriptor.CanonicalKey);
        Assert.AreEqual(parsed.ResourceKey, descriptor.CanonicalKey);
        Assert.AreEqual("GigE:001122334455", descriptor.DisplayName, "没有用户自定义名时以设备名兜底。");
    }

    /// <summary>
    /// 同一台相机可能同时出现在 GigEVision2 与 GenICamTL 下：必须按资源键去重，
    /// 否则界面会列出两个指向同一物理设备的候选。
    /// </summary>
    [TestMethod]
    public void SameCameraOnTwoInterfaces_IsDeduplicatedAndOrdered()
    {
        var descriptors = HalconBoardInfo.ToDescriptors(new[]
        {
            HalconBoardInfo.Parse(GigEBoard, "GigEVision2")!,
            HalconBoardInfo.Parse(GigEBoard, "GenICamTL")!,
            new HalconDiscoveredDevice("USB3Vision", "cam-side", null, null, null, null)
        });

        CollectionAssert.AreEqual(
            new[] { "camera:serial:40123456", "halcon:camera:USB3Vision|cam-side" },
            descriptors.Select(item => item.CanonicalKey).ToArray(),
            "按规范资源键排序，枚举顺序不影响候选顺序。");
    }

    /// <summary>解析结果为空时不得抛出；"现场没有相机"是正常结果。</summary>
    [TestMethod]
    public void EmptyInput_ReturnsEmptyDescriptors()
    {
        Assert.AreEqual(0, HalconBoardInfo.ToDescriptors(Array.Empty<HalconDiscoveredDevice>()).Count);
        Assert.ThrowsExactly<ArgumentNullException>(() => HalconBoardInfo.ToDescriptors(null!));
    }

    /// <summary>
    /// 所有接口都查不通时必须明确失败：返回空列表会让界面把"接口不可用"显示成"现场没有相机"，
    /// 操作员会去查相机电源，而真正的问题在采集接口或许可证。
    /// </summary>
    [TestMethod]
    public async Task AllInterfacesUnavailable_FailsInsteadOfReportingNoDevices()
    {
        var failure = Assert.ThrowsExactly<VisionProviderUnavailableException>(() =>
            HalconDeviceEnumeration.Enumerate(new[] { "dp-not-an-interface" }));

        Assert.AreEqual(HalconAcquisitionProvider.ProviderIdentity, failure.ProviderId);

        var provider = new HalconAcquisitionProvider(discoveryInterfaceNames: new[] { "dp-not-an-interface" });
        await Assert.ThrowsExactlyAsync<VisionProviderUnavailableException>(
            async () => await provider.DiscoverAsync(CancellationToken.None));
    }
}