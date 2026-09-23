namespace DP.Vision.Acquisition;

/// <summary>已打开设备报告的规范身份；用于校验机器配置声明的ResourceKey是否指向同一物理硬件。</summary>
/// <param name="ProviderId">报告该身份的Provider稳定身份。</param>
/// <param name="ProviderBindingId">Provider配置内的设备绑定身份。</param>
/// <param name="CanonicalKey">规范资源键，例如camera:serial:DA123456；Provider无法提供时为空。</param>
/// <param name="SerialNumber">设备序列号；不可得时为空。</param>
/// <param name="VendorName">厂商名称；不可得时为空。</param>
/// <param name="ModelName">型号名称；不可得时为空。</param>
public sealed record VisionDeviceIdentity(
    string ProviderId,
    string ProviderBindingId,
    string? CanonicalKey = null,
    string? SerialNumber = null,
    string? VendorName = null,
    string? ModelName = null)
{
    /// <summary>判断本身份是否报告了可用于互斥协调的规范资源键。</summary>
    public bool HasCanonicalKey => !string.IsNullOrWhiteSpace(CanonicalKey);
}
