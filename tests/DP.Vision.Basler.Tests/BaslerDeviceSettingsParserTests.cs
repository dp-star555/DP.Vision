using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// V2-2：Basler deviceSettings 解析器回归。Plugin验证自身配置后生成内部绑定身份、
/// 规范ResourceKey与确定性配置摘要；未知字段与歧义选择器拒绝。
/// </summary>
[TestClass]
public sealed class BaslerDeviceSettingsParserTests
{
    /// <summary>仅序列号：绑定身份=序列号、资源键=camera:serial:xxx、摘要只含出现的字段。</summary>
    [TestMethod]
    public void SerialNumber_ProducesBindingAndCanonicalKey()
    {
        var result = BaslerDeviceSettingsParser.Parse("{\"serialNumber\":\"40123456\"}");

        Assert.AreEqual("40123456", result.ProviderBindingId);
        Assert.AreEqual("camera:serial:40123456", result.ResourceKey);
        Assert.AreEqual("serialNumber=40123456", result.ConfigurationSummary);
    }

    /// <summary>仅用户自定义名：绑定身份=名字、资源键=camera:name:xxx。</summary>
    [TestMethod]
    public void UserDefinedName_ProducesBindingAndCanonicalKey()
    {
        var result = BaslerDeviceSettingsParser.Parse("{\"userDefinedName\":\"SideView\"}");

        Assert.AreEqual("SideView", result.ProviderBindingId);
        Assert.AreEqual("camera:name:SideView", result.ResourceKey);
        Assert.AreEqual("userDefinedName=SideView", result.ConfigurationSummary);
    }

    /// <summary>摘要按字段名排序且只含出现的字段；触发源与像素格式进入摘要。</summary>
    [TestMethod]
    public void Summary_IsDeterministicAndSorted()
    {
        var result = BaslerDeviceSettingsParser.Parse(
            "{\"serialNumber\":\"40123456\",\"triggerSource\":\"Line1\",\"pixelFormat\":\"Mono8\"}");

        Assert.AreEqual(
            "pixelFormat=Mono8;serialNumber=40123456;triggerSource=Line1",
            result.ConfigurationSummary);
    }

    /// <summary>同时给出两个选择器必须拒绝：无法判断以哪个为准。</summary>
    [TestMethod]
    public void AmbiguousSelectors_AreRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            BaslerDeviceSettingsParser.Parse(
                "{\"serialNumber\":\"40123456\",\"userDefinedName\":\"SideView\"}"));

        StringAssert.Contains(failure.Message, "且只能指定");
    }

    /// <summary>两个选择器都不给必须拒绝：会匹配到任意一台设备。</summary>
    [TestMethod]
    public void NoSelector_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            BaslerDeviceSettingsParser.Parse("{}"));

        StringAssert.Contains(failure.Message, "且只能指定");
    }

    /// <summary>未知字段必须拒绝，避免拼写错误被静默忽略。</summary>
    [TestMethod]
    public void UnknownField_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            BaslerDeviceSettingsParser.Parse("{\"serialNumber\":\"40123456\",\"host\":\"192.168.1.1\"}"));

        StringAssert.Contains(failure.Message, "未知字段");
        StringAssert.Contains(failure.Message, "host");
    }

    /// <summary>空deviceSettings必须拒绝：尚未配置设备。</summary>
    [TestMethod]
    public void EmptySettings_AreRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            BaslerDeviceSettingsParser.Parse(null));
    }

    /// <summary>
    /// 解析结果必须把私有绑定对象一并带出。设备字段只在这里解析一次，
    /// 之后随绑定一路带到打开设备；丢掉它，Provider 就只能另找一条配置通道重写同一台相机。
    /// </summary>
    [TestMethod]
    public void ParsedSettings_CarryPrivateBinding()
    {
        var result = BaslerDeviceSettingsParser.Parse(
            "{\"serialNumber\":\"40123456\",\"triggerSource\":\"Line1\"}");

        var binding = Assert.IsInstanceOfType<BaslerAcquisitionBinding>(result.ProviderState);
        Assert.AreEqual("40123456", binding.BindingId);
        Assert.AreEqual("40123456", binding.SerialNumber);
        Assert.AreEqual("Line1", binding.TriggerSource);
        Assert.AreEqual("camera:serial:40123456", binding.CanonicalKey);
    }

    /// <summary>
    /// 像素格式必须**随绑定**带到设备，而不只是进配置摘要：
    /// 只进摘要的话，改了 deviceSettings 会让 CompositionId 变化、看上去"生效了"，
    /// 但设备从头到尾没被写过这个参数——现场表现就是"设置了但不生效"。
    /// </summary>
    [TestMethod]
    public void PixelFormat_ReachesPrivateBinding()
    {
        var result = BaslerDeviceSettingsParser.Parse(
            "{\"serialNumber\":\"40123456\",\"pixelFormat\":\"Mono8\"}");

        var binding = Assert.IsInstanceOfType<BaslerAcquisitionBinding>(result.ProviderState);
        Assert.AreEqual("Mono8", binding.PixelFormat);

        // 摘要里也要有：它让"改了像素格式"产生新的组合身份。
        StringAssert.Contains(result.ConfigurationSummary, "pixelFormat=Mono8");
    }

    /// <summary>未配置像素格式时绑定上必须为空，表示保持设备当前设置，而不是猜一个默认格式。</summary>
    [TestMethod]
    public void MissingPixelFormat_LeavesBindingNull()
    {
        var result = BaslerDeviceSettingsParser.Parse("{\"serialNumber\":\"40123456\"}");

        var binding = Assert.IsInstanceOfType<BaslerAcquisitionBinding>(result.ProviderState);
        Assert.IsNull(binding.PixelFormat);
    }

    /// <summary>
    /// Provider 只凭 deviceSettings 解析出的绑定就能构建设备，全程不需要插件私有配置、也不需要相机。
    /// 这是"deviceSettings 是设备配置唯一来源"在厂商侧的证据。
    /// </summary>
    [TestMethod]
    public async Task Provider_OpensFromParsedBindingWithoutPrivateConfiguration()
    {
        var result = BaslerDeviceSettingsParser.Parse("{\"serialNumber\":\"40123456\"}");

        await using var provider = new BaslerAcquisitionProvider();
        await using var device = await provider.OpenAsync(
            new VisionAcquisitionProviderBinding(result.ProviderBindingId, result.ProviderState),
            CancellationToken.None);

        Assert.AreEqual(BaslerAcquisitionProvider.ProviderIdentity, device.Identity.ProviderId);
        Assert.AreEqual("40123456", device.Identity.ProviderBindingId);
        Assert.AreEqual("camera:serial:40123456", device.Identity.CanonicalKey);
    }

    /// <summary>绑定里没有 Basler 私有状态时必须明确拒绝，不能猜成"某台设备"。</summary>
    [TestMethod]
    public async Task Provider_RejectsBindingWithoutBaslerState()
    {
        await using var provider = new BaslerAcquisitionProvider();

        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            provider.OpenAsync(new VisionAcquisitionProviderBinding("40123456"), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult());

        StringAssert.Contains(failure.Message, "40123456");
    }
}
