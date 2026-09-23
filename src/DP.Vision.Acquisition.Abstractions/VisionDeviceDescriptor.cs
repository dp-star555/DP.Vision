namespace DP.Vision.Acquisition;

/// <summary>设备发现结果；只描述可枚举的候选设备，不代表设备已打开或已占用。</summary>
/// <param name="ProviderId">提供该候选的Provider稳定身份。</param>
/// <param name="ProviderBindingId">Provider配置内的设备绑定身份。</param>
/// <param name="CanonicalKey">规范资源键；Provider无法提供时为空。</param>
/// <param name="DisplayName">面向操作员的显示名称。</param>
/// <param name="VendorName">厂商名称；不可得时为空。</param>
/// <param name="ModelName">型号名称；不可得时为空。</param>
/// <param name="SerialNumber">设备序列号；不可得时为空。</param>
public sealed record VisionDeviceDescriptor(
    string ProviderId,
    string ProviderBindingId,
    string? CanonicalKey,
    string DisplayName,
    string? VendorName = null,
    string? ModelName = null,
    string? SerialNumber = null);
