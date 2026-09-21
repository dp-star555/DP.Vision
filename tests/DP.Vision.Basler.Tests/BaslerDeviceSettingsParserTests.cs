using System;
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
}
