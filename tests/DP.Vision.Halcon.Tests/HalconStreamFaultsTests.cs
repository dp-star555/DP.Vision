using System;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// HALCON 错误码分类的回归。
/// <para>
/// 这些取值来自 HALCON 23.11 的 <c>include/HErrorDef.h</c>，不是推测。
/// 其中最要紧的一条：**许可证错误码不构成连续区间**——2000、2001、2002、2100–2108、2200 之后都是别的错误类，
/// 所以按区间判断会把"参数写错""图像尺寸非法"误报成许可证问题，把现场排查引到完全错误的方向。
/// </para>
/// </summary>
[TestClass]
public sealed class HalconStreamFaultsTests
{
    /// <summary>抓取超时必须只由 H_ERR_FGTIMEOUT 判定。</summary>
    [TestMethod]
    public void GrabTimeout_IsRecognisedByItsOwnCode()
    {
        Assert.IsTrue(HalconStreamFaults.IsGrabTimeout(5322));
        Assert.IsInstanceOfType<HalconGrabTimeoutException>(
            HalconStreamFaults.Classify(5322, "Image acquisition: timeout"));
    }

    /// <summary>
    /// 通用的 H_ERR_TIMEOUT（9400）不能当成抓取超时：它可能来自任何算子，
    /// 把它当成"这一轮没等到帧"会让真正的故障被静默重试掉。
    /// </summary>
    [TestMethod]
    public void GenericTimeout_IsNotAGrabTimeout()
    {
        Assert.IsFalse(HalconStreamFaults.IsGrabTimeout(9400));
        Assert.IsFalse(HalconStreamFaults.Classify(9400, "Timeout occurred") is HalconGrabTimeoutException);
    }

    /// <summary>许可证错误码必须归到 Provider 不可用，而不是设备离线。</summary>
    /// <param name="errorCode">HALCON 错误码。</param>
    /// <param name="message">错误文本。</param>
    [TestMethod]
    [DataRow(2003, "No license found")]
    [DataRow(2006, "No license for this operator")]
    [DataRow(2021, "System clock has been set back")]
    [DataRow(2029, "All licenses in use")]
    [DataRow(2038, "Cannot connect to a license server")]
    [DataRow(2091, "This feature is available in a different license pool")]
    [DataRow(2300, "Dongle not attached")]
    [DataRow(2335, "License server SSL/TLS certificate invalid")]
    [DataRow(2384, "License file does not support this version")]
    public void LicenseCodes_AreClassifiedAsProviderUnavailable(int errorCode, string message)
    {
        Assert.IsTrue(HalconStreamFaults.IsLicenseFault(errorCode));
        var failure = HalconStreamFaults.Classify(errorCode, message);
        Assert.IsInstanceOfType<VisionProviderUnavailableException>(failure);
        StringAssert.Contains(failure.Message, HalconAcquisitionProvider.ProviderIdentity);
    }

    /// <summary>
    /// 与许可证同处 2xxx 段但不是许可证的错误码必须被排除。
    /// 这条用例专门守住"不能按区间判断"这一事实。
    /// </summary>
    /// <param name="errorCode">HALCON 错误码。</param>
    /// <param name="message">错误文本。</param>
    [TestMethod]
    [DataRow(2000, "Wrong specification of parameter")]
    [DataRow(2001, "Initialize Halcon")]
    [DataRow(2002, "Used number of symbolic object names")]
    [DataRow(2100, "Wrong index for output object parameter")]
    [DataRow(2106, "Wrong image width")]
    [DataRow(2200, "Inconsistent data of data base")]
    [DataRow(2299, "not a defined HALCON error code")]
    public void NonLicenseCodesInTheSameRange_AreNotClassifiedAsLicense(int errorCode, string message)
    {
        Assert.IsFalse(HalconStreamFaults.IsLicenseFault(errorCode));
        Assert.IsFalse(HalconStreamFaults.Classify(errorCode, message) is VisionProviderUnavailableException);
    }

    /// <summary>许可证错误码区间的边界必须准确。</summary>
    [TestMethod]
    public void LicenseRange_StartsAndEndsWhereTheHeaderSays()
    {
        Assert.IsTrue(HalconStreamFaults.IsLicenseFault(2300));
        Assert.IsTrue(HalconStreamFaults.IsLicenseFault(2399));
        Assert.IsFalse(HalconStreamFaults.IsLicenseFault(2299));
        Assert.IsFalse(HalconStreamFaults.IsLicenseFault(2400));
    }

    /// <summary>图像采集类的 53xx 错误码才表示设备不可用。</summary>
    /// <param name="errorCode">HALCON 错误码。</param>
    /// <param name="message">错误文本。</param>
    [TestMethod]
    [DataRow(5300, "No image acquisition device opened")]
    [DataRow(5304, "No video signal")]
    [DataRow(5306, "Failed grabbing of an image")]
    [DataRow(5312, "Device cannot be initialized")]
    [DataRow(5319, "Device busy")]
    [DataRow(5335, "Device lost")]
    public void DeviceCodes_AreClassifiedAsDeviceOffline(int errorCode, string message)
    {
        Assert.IsTrue(HalconStreamFaults.IsDeviceFault(errorCode));
        Assert.IsInstanceOfType<VisionDeviceOfflineException>(HalconStreamFaults.Classify(errorCode, message));
    }

    /// <summary>设备故障与许可证故障必须是两类，现场排查路径完全不同。</summary>
    [TestMethod]
    public void DeviceAndLicenseFaults_AreDistinguishable()
    {
        var license = HalconStreamFaults.Classify(2003, "No license found");
        var device = HalconStreamFaults.Classify(5335, "Device lost");

        Assert.AreNotEqual(license.GetType(), device.GetType());
        Assert.IsFalse(license is VisionDeviceOfflineException);
        Assert.IsFalse(device is VisionProviderUnavailableException);
    }

    /// <summary>其余错误码按数据/采集失败处理，不做过度归因。</summary>
    /// <param name="errorCode">HALCON 错误码。</param>
    /// <param name="message">错误文本。</param>
    [TestMethod]
    [DataRow(5321, "IA: unsupported parameter")]
    [DataRow(5326, "IA: invalid parameter value")]
    [DataRow(5336, "IA: grab aborted")]
    [DataRow(12345, "unknown")]
    public void OtherCodes_FallBackToAcquisitionDataFailure(int errorCode, string message)
    {
        var failure = HalconStreamFaults.Classify(errorCode, message);

        Assert.IsInstanceOfType<VisionDataException>(failure);
        StringAssert.Contains(failure.Message, errorCode.ToString());
    }

    /// <summary>没有错误文本时仍然要给出可读诊断，而不是空消息。</summary>
    [TestMethod]
    public void MissingMessage_StillProducesReadableDiagnostic()
    {
        var failure = HalconStreamFaults.Classify(2003, null);

        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Message));
        StringAssert.Contains(failure.Message, "2003");
    }
}
