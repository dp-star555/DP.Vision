using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// 主动单次采集的触发映射回归。
/// <para>
/// HALCON 的打开参数 <c>external_trigger</c> 有三个取值：<c>'default'</c>、<c>'false'</c>、<c>'true'</c>。
/// 把"保持设备当前设置"写成 <c>'false'</c> 会在打开设备时显式关闭外部触发，
/// 也就是把一台硬件触发的相机顺手改成自由运行——这类问题只在现场表现为"采集到的不是触发那一刻的图"。
/// </para>
/// </summary>
[TestClass]
public sealed class HalconTriggerMappingTests
{
    /// <summary>保持当前设置必须一个触发参数都不写。</summary>
    [TestMethod]
    public void KeepCurrent_LeavesTheDeviceTriggerSettingUntouched()
    {
        var value = HalconCameraCapture.MapTriggerValue(EVisionTriggerMode.KeepCurrent);

        Assert.AreEqual("default", value);
        Assert.AreNotEqual("false", value, "把 KeepCurrent 写成 'false' 会显式关闭外部触发，把硬件触发相机改成自由运行。");
    }

    /// <summary>自由运行必须显式关闭外部触发。</summary>
    [TestMethod]
    public void FreeRun_DisablesExternalTrigger() =>
        Assert.AreEqual("false", HalconCameraCapture.MapTriggerValue(EVisionTriggerMode.FreeRun));

    /// <summary>外部触发必须显式打开。</summary>
    [TestMethod]
    public void External_EnablesExternalTrigger() =>
        Assert.AreEqual("true", HalconCameraCapture.MapTriggerValue(EVisionTriggerMode.External));

    /// <summary>软件触发无法用打开参数表达时明确拒绝，而不是静默按自由运行采集。</summary>
    [TestMethod]
    public void Software_IsExplicitlyRejected() =>
        Assert.ThrowsExactly<VisionParameterNotSupportedException>(
            () => HalconCameraCapture.MapTriggerValue(EVisionTriggerMode.Software));
}
