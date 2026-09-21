using System;
using System.Collections.Generic;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// 把 HALCON 的 <c>H_ERR_*</c> 错误码翻译成公共采集异常。
/// <para>
/// 放在中立层而不是 SDK 条件编译里，是为了让"哪一类错误算超时、哪一类算许可证、哪一类算设备离线"
/// 这条判定可以在没有相机、甚至没有 HALCON 运行时的机器上被完整验证；
/// SDK 侧只负责把 <c>HOperatorException</c> 的错误码与错误文本交进来。
/// </para>
/// <para>
/// 错误码取值来自 HALCON 23.11 的 <c>include/HErrorDef.h</c>（本机安装目录内），
/// 不是推测值。**许可证错误码不构成连续区间**：2000–2002 与 2100 之后都是别的错误类，
/// 所以 2003–2091 必须逐个列出，不能按区间判断。
/// </para>
/// </summary>
internal static class HalconStreamFaults
{
    /// <summary>抓取超时（<c>H_ERR_FGTIMEOUT</c>）：外部触发下"这一轮没有等到帧"。</summary>
    private const int GrabTimeoutErrorCode = 5322;

    /// <summary>许可证错误码区间下界；2300–2399 全部是 <c>H_ERR_LIC_*</c>。</summary>
    private const int LicenseRangeStart = 2300;

    /// <summary>许可证错误码区间上界（含）。</summary>
    private const int LicenseRangeEnd = 2399;

    /// <summary>2003–2091 段内逐个列出的 <c>H_ERR_LIC_*</c> 错误码。</summary>
    private static readonly HashSet<int> LicenseCodes = new HashSet<int>
    {
        2003, 2005, 2006, 2008, 2009, 2021, 2022, 2024, 2028, 2029,
        2030, 2031, 2033, 2034, 2035, 2036, 2037, 2038, 2041, 2042,
        2043, 2044, 2045, 2047, 2051, 2052, 2055, 2056, 2067, 2069,
        2075, 2076, 2082, 2087, 2091
    };

    /// <summary>设备侧不可用的 53xx 错误码；这些才是"设备离线"。</summary>
    private static readonly HashSet<int> DeviceCodes = new HashSet<int>
    {
        5300, // H_ERR_NFS        No image acquisition device opened
        5302, // H_ERR_FGWD       IA: wrong device
        5304, // H_ERR_FGNV       IA: no video signal
        5305, // H_ERR_UFG        Unknown image acquisition device
        5306, // H_ERR_FGF        IA: failed grabbing of an image
        5312, // H_ERR_FGNI       IA: device cannot be initialized
        5319, // H_ERR_FGDV       IA: device busy
        5332, // H_ERR_FGCLOSE    IA: device could not be closed properly
        5335  // H_ERR_FGDEVLOST  IA: device lost
    };

    /// <summary>该错误码是否表示一次抓取超时。</summary>
    /// <param name="errorCode">HALCON 错误码。</param>
    /// <returns>是抓取超时时返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 只认 <c>H_ERR_FGTIMEOUT</c>（5322）。通用的 <c>H_ERR_TIMEOUT</c>（9400）**不**按抓取超时处理：
    /// 它可能来自任何算子，把它当成"没等到帧"会让真正的故障被静默重试掉。
    /// </remarks>
    public static bool IsGrabTimeout(int errorCode) => errorCode == GrabTimeoutErrorCode;

    /// <summary>该错误码是否属于许可证故障。</summary>
    /// <param name="errorCode">HALCON 错误码。</param>
    /// <returns>属于许可证故障时返回 <see langword="true"/>。</returns>
    public static bool IsLicenseFault(int errorCode) =>
        LicenseCodes.Contains(errorCode) || (errorCode >= LicenseRangeStart && errorCode <= LicenseRangeEnd);

    /// <summary>该错误码是否表示设备侧不可用。</summary>
    /// <param name="errorCode">HALCON 错误码。</param>
    /// <returns>表示设备不可用时返回 <see langword="true"/>。</returns>
    public static bool IsDeviceFault(int errorCode) => DeviceCodes.Contains(errorCode);

    /// <summary>
    /// 把错误码与错误文本翻译成公共采集异常。
    /// <para>
    /// 许可证与设备离线刻意分成两类异常：现场排查路径完全不同——前者查 license 文件、
    /// 许可证服务器与系统时间，后者查供电、网线与设备占用，混在一起会让操作员查错方向。
    /// </para>
    /// </summary>
    /// <param name="errorCode">HALCON 错误码。</param>
    /// <param name="errorMessage">HALCON 错误文本；为空时给出替代说明。</param>
    /// <param name="innerException">底层异常；可选。</param>
    /// <returns>应当抛出的公共采集异常；本方法只构造不抛出，便于直接断言分类结果。</returns>
    public static Exception Classify(int errorCode, string? errorMessage, Exception? innerException = null)
    {
        var detail = string.IsNullOrWhiteSpace(errorMessage)
            ? "HALCON 未提供错误文本。"
            : errorMessage!.Trim();

        if (IsGrabTimeout(errorCode))
            return new HalconGrabTimeoutException($"HALCON 抓取超时（错误码 {errorCode}）：{detail}");

        if (IsLicenseFault(errorCode))
            return new VisionProviderUnavailableException(
                HalconAcquisitionProvider.ProviderIdentity,
                $"HALCON 许可证不可用（错误码 {errorCode}）：{detail}。"
                + "这是运行期许可证问题而不是设备离线；请检查 license 文件或许可证服务器，以及系统时钟是否被回拨。",
                innerException);

        if (IsDeviceFault(errorCode))
            return new VisionDeviceOfflineException(
                $"HALCON 采集设备不可用（错误码 {errorCode}）：{detail}", innerException);

        return new VisionDataException($"HALCON 采集失败（错误码 {errorCode}）：{detail}");
    }
}
