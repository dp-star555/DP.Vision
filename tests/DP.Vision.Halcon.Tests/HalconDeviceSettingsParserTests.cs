using System;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// V2-2：HALCON deviceSettings 解析器回归。Plugin验证自身配置后生成内部绑定身份、
/// 规范ResourceKey与确定性配置摘要；必填字段缺失与未知字段拒绝。
/// </summary>
[TestClass]
public sealed class HalconDeviceSettingsParserTests
{
    /// <summary>最小编制：绑定身份=interface|device、资源键=halcon:camera:iface|device、摘要含默认超时。</summary>
    [TestMethod]
    public void MinimalSettings_ProduceBindingAndCanonicalKey()
    {
        var result = HalconDeviceSettingsParser.Parse(
            "{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\"}");

        Assert.AreEqual("GigEVision2|cam-top", result.ProviderBindingId);
        Assert.AreEqual("halcon:camera:GigEVision2|cam-top", result.ResourceKey);
        Assert.AreEqual(
            "deviceName=cam-top;grabTimeoutMilliseconds=5000;interfaceName=GigEVision2",
            result.ConfigurationSummary);
    }

    /// <summary>带序列号：绑定身份=序列号、资源键=camera:serial:xxx、摘要追加序列号。</summary>
    [TestMethod]
    public void SerialNumber_OverridesInterfaceAndDeviceKey()
    {
        var result = HalconDeviceSettingsParser.Parse(
            "{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\",\"serialNumber\":\"SN-001\"}");

        Assert.AreEqual("SN-001", result.ProviderBindingId);
        Assert.AreEqual("camera:serial:SN-001", result.ResourceKey);
        StringAssert.Contains(result.ConfigurationSummary, "serialNumber=SN-001");
    }

    /// <summary>自定义抓取超时进入绑定身份摘要；无限等待被拒绝。</summary>
    [TestMethod]
    public void GrabTimeoutMilliseconds_IsAppliedAndMustBePositive()
    {
        var result = HalconDeviceSettingsParser.Parse(
            "{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\",\"grabTimeoutMilliseconds\":3000}");

        StringAssert.Contains(result.ConfigurationSummary, "grabTimeoutMilliseconds=3000");

        Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            HalconDeviceSettingsParser.Parse(
                "{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\",\"grabTimeoutMilliseconds\":0}"));
    }

    /// <summary>缺少interfaceName必须拒绝。</summary>
    [TestMethod]
    public void MissingInterfaceName_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            HalconDeviceSettingsParser.Parse("{\"deviceName\":\"cam-top\"}"));

        StringAssert.Contains(failure.Message, "interfaceName");
    }

    /// <summary>缺少deviceName必须拒绝。</summary>
    [TestMethod]
    public void MissingDeviceName_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            HalconDeviceSettingsParser.Parse("{\"interfaceName\":\"GigEVision2\"}"));

        StringAssert.Contains(failure.Message, "deviceName");
    }

    /// <summary>未知字段必须拒绝，避免拼写错误被静默忽略。</summary>
    [TestMethod]
    public void UnknownField_IsRejected()
    {
        var failure = Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            HalconDeviceSettingsParser.Parse(
                "{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\",\"host\":\"192.168.1.1\"}"));

        StringAssert.Contains(failure.Message, "未知字段");
        StringAssert.Contains(failure.Message, "host");
    }

    /// <summary>空deviceSettings必须拒绝：尚未配置设备。</summary>
    [TestMethod]
    public void EmptySettings_AreRejected()
    {
        Assert.ThrowsExactly<VisionSourceConfigurationException>(() =>
            HalconDeviceSettingsParser.Parse(null));
    }
}
