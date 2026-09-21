using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Basler.Tests;

/// <summary>Provider私有绑定的选择器语义回归：绑定必须唯一确定一台设备。</summary>
[TestClass]
public sealed class BaslerAcquisitionBindingTests
{
    /// <summary>按序列号选择时报告规范资源键，供跨运行互斥协调使用。</summary>
    [TestMethod]
    public void SerialNumberSelector_ReportsCanonicalKey()
    {
        var binding = new BaslerAcquisitionBinding("top-camera", serialNumber: "40123456");

        Assert.AreEqual("top-camera", binding.BindingId);
        Assert.AreEqual("40123456", binding.SerialNumber);
        Assert.IsNull(binding.UserDefinedName);
        Assert.AreEqual("camera:serial:40123456", binding.CanonicalKey);
        Assert.AreEqual("SerialNumber", binding.SelectorKey);
        Assert.AreEqual("40123456", binding.SelectorValue);
    }

    /// <summary>按用户自定义名选择时无法报告规范身份，必须显式表现为"没有规范键"。</summary>
    [TestMethod]
    public void UserDefinedNameSelector_HasNoCanonicalKey()
    {
        var binding = new BaslerAcquisitionBinding("side-camera", userDefinedName: "SideView");

        Assert.IsNull(binding.CanonicalKey);
        Assert.AreEqual("UserDefinedName", binding.SelectorKey);
        Assert.AreEqual("SideView", binding.SelectorValue);
    }

    /// <summary>外部触发的触发源属于机器接线事实，随绑定一起配置。</summary>
    [TestMethod]
    public void TriggerSource_IsOptionalAndTrimmed()
    {
        Assert.AreEqual("Line1", new BaslerAcquisitionBinding("c", serialNumber: "1", triggerSource: "  Line1  ").TriggerSource);
        Assert.IsNull(new BaslerAcquisitionBinding("c", serialNumber: "1", triggerSource: "   ").TriggerSource);
    }

    /// <summary>两个选择器都给或都不给都必须拒绝：前者无法判断以哪个为准，后者会匹配到任意一台。</summary>
    /// <param name="serial">序列号参数。</param>
    /// <param name="name">用户自定义名参数。</param>
    [TestMethod]
    [DataRow("40123456", "SideView")]
    [DataRow(null, null)]
    [DataRow("   ", "   ")]
    public void AmbiguousSelector_IsRejected(string? serial, string? name)
    {
        var failure = Assert.ThrowsExactly<ArgumentException>(() => new BaslerAcquisitionBinding("c", serial, name));

        StringAssert.Contains(failure.Message, "只能指定");
    }

    /// <summary>绑定身份不能为空，否则私有配置无法被公共层引用。</summary>
    /// <param name="bindingId">绑定身份。</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void EmptyBindingId_IsRejected(string bindingId)
    {
        var failure = Assert.ThrowsExactly<ArgumentException>(
            () => new BaslerAcquisitionBinding(bindingId, serialNumber: "40123456"));

        StringAssert.Contains(failure.Message, "绑定身份");
    }
}
