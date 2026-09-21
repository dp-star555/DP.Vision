using System;
using System.Collections.Generic;
using System.Globalization;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// 公共采集参数到 HALCON 设备参数的语义回归：触发三态、软触发的真实写法、曝光/增益的单位与"零不等于没给"。
/// <para>
/// 这些结论全部落在 <see cref="HalconFramegrabberParameters"/> 这一层，它不接触 <c>HFramegrabber</c>，
/// 因此可以在没有相机、没有许可证的机器上确定性验证——而它们的错法只在现场表现为
/// "设置了但不生效"或"采集到的不是触发那一刻的图"。
/// </para>
/// </summary>
[TestClass]
public sealed class HalconFramegrabberParametersTests
{
    /// <summary>保持设备当前设置必须一个触发参数都不写。</summary>
    [TestMethod]
    public void KeepCurrent_WritesNoTriggerParameter()
    {
        var parameters = HalconFramegrabberParameters.ResolveTrigger(EVisionTriggerMode.KeepCurrent, "Line1");

        Assert.AreEqual(0, parameters.Count);
    }

    /// <summary>自由运行必须显式关闭外部触发。</summary>
    [TestMethod]
    public void FreeRun_DisablesExternalTrigger()
    {
        var parameters = HalconFramegrabberParameters.ResolveTrigger(EVisionTriggerMode.FreeRun, null);

        Assert.AreEqual(1, parameters.Count);
        Assert.AreEqual("false", ValueOf(parameters, "external_trigger"));
    }

    /// <summary>
    /// 软触发按 MVTec 官方示例配置：触发方式取 <c>'Software'</c>、采集模式连续，
    /// 并且**不**写 <c>external_trigger</c>（两者会互相覆盖）。
    /// </summary>
    [TestMethod]
    public void Software_ConfiguresConsumerTriggerAndContinuousAcquisition()
    {
        var parameters = HalconFramegrabberParameters.ResolveTrigger(EVisionTriggerMode.Software, null);

        Assert.AreEqual("Software", ValueOf(parameters, "[Consumer]trigger"));
        Assert.AreEqual("Continuous", ValueOf(parameters, "AcquisitionMode"));
        Assert.IsNull(ValueOf(parameters, "external_trigger"), "软触发不能同时写 external_trigger，否则两种触发方式会互相覆盖。");
    }

    /// <summary>只有软触发需要在抓取前单独发一条触发命令。</summary>
    /// <param name="mode">触发模式。</param>
    /// <param name="expected">是否需要先发触发命令。</param>
    [TestMethod]
    [DataRow(EVisionTriggerMode.Software, true)]
    [DataRow(EVisionTriggerMode.FreeRun, false)]
    [DataRow(EVisionTriggerMode.External, false)]
    [DataRow(EVisionTriggerMode.KeepCurrent, false)]
    public void RequiresSoftwareTriggerCommand_OnlyForSoftware(EVisionTriggerMode mode, bool expected) =>
        Assert.AreEqual(expected, HalconFramegrabberParameters.RequiresSoftwareTriggerCommand(mode));

    /// <summary>软触发命令写的是 <c>[Consumer]trigger_software</c>：只设触发方式不会自己产生触发。</summary>
    [TestMethod]
    public void SoftwareTriggerCommand_IsAnExplicitSingleShotWrite()
    {
        var command = HalconFramegrabberParameters.SoftwareTriggerCommand();

        Assert.AreEqual("[Consumer]trigger_software", command.Name);
        Assert.AreEqual(1, command.Value);
    }

    /// <summary>外部触发缺少触发源时明确拒绝，不猜物理接线，也不静默退回自由运行。</summary>
    [TestMethod]
    public void External_WithoutDeclaredTriggerSource_IsRejected() =>
        Assert.ThrowsExactly<VisionSourceConfigurationException>(
            () => HalconFramegrabberParameters.ResolveTrigger(EVisionTriggerMode.External, "  "));

    /// <summary>外部触发写入绑定声明的触发源与 GenICam SFNC 触发节点。</summary>
    [TestMethod]
    public void External_WritesDeclaredTriggerSource()
    {
        var parameters = HalconFramegrabberParameters.ResolveTrigger(EVisionTriggerMode.External, " Line1 ");

        Assert.AreEqual("true", ValueOf(parameters, "external_trigger"));
        Assert.AreEqual("FrameStart", ValueOf(parameters, "TriggerSelector"));
        Assert.AreEqual("Line1", ValueOf(parameters, "TriggerSource"), "触发源必须去掉首尾空白后原样写入。");
        Assert.AreEqual("On", ValueOf(parameters, "TriggerMode"));
    }

    /// <summary>两个物理量都没给时不写任何参数，即保持设备当前设置。</summary>
    [TestMethod]
    public void CaptureParameters_NothingGiven_WritesNothing()
    {
        var parameters = HalconFramegrabberParameters.ResolveCaptureParameters(null, null);

        Assert.AreEqual(0, parameters.Count);
    }

    /// <summary>写手动值之前必须先关掉自动算法，否则手动值会被自动算法立刻覆盖。</summary>
    [TestMethod]
    public void CaptureParameters_WritesManualValuesWithAutomaticAlgorithmsOff()
    {
        var parameters = HalconFramegrabberParameters.ResolveCaptureParameters(1500, 2.5);

        Assert.AreEqual("Off", ValueOf(parameters, "ExposureAuto"));
        Assert.AreEqual(1500d, ValueOf(parameters, "ExposureTime"));
        Assert.AreEqual("Off", ValueOf(parameters, "GainAuto"));
        Assert.AreEqual(2.5d, ValueOf(parameters, "Gain"), "增益写的是 SFNC 的 Gain 节点，单位分贝。");
    }

    /// <summary>显式给出的 0 必须真的写到设备上：0 dB 是合法的单位增益，不能因为"零"被当成没给。</summary>
    [TestMethod]
    public void CaptureParameters_ExplicitZeroIsWrittenToTheDevice()
    {
        var parameters = HalconFramegrabberParameters.ResolveCaptureParameters(0, 0);

        Assert.AreEqual(0d, ValueOf(parameters, "ExposureTime"));
        Assert.AreEqual(0d, ValueOf(parameters, "Gain"));
    }

    /// <summary>只给一个物理量时不得顺手改写另一个。</summary>
    [TestMethod]
    public void CaptureParameters_OnlyGainGiven_LeavesExposureUntouched()
    {
        var parameters = HalconFramegrabberParameters.ResolveCaptureParameters(null, 3);

        Assert.IsNull(ValueOf(parameters, "ExposureTime"));
        Assert.IsNull(ValueOf(parameters, "ExposureAuto"), "曝光没给就不该关自动曝光。");
        Assert.AreEqual("Off", ValueOf(parameters, "GainAuto"));
        Assert.AreEqual(3d, ValueOf(parameters, "Gain"));
    }

    /// <summary>诊断文本里的数值必须与区域设置无关：小数点不能跟着宿主变成逗号。</summary>
    [TestMethod]
    public void DisplayValue_UsesInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var parameter = new HalconDeviceParameter("Gain", 2.5);

            Assert.AreEqual("2.5", parameter.DisplayValue);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static object? ValueOf(IReadOnlyList<HalconDeviceParameter> parameters, string name)
    {
        foreach (var parameter in parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
                return parameter.Value;
        }

        return null;
    }
}