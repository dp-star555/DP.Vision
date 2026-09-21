using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// Plugin解析自身deviceSettings后生成的验证结果。公共层不解释设备字段，
/// 只使用其绑定身份、规范资源键与配置摘要；字段含义与校验由对应Plugin拥有。
/// </summary>
public sealed record VisionDeviceSettingsParseResult
{
    /// <summary>创建解析结果。</summary>
    /// <param name="providerBindingId">Plugin私有设备绑定身份；由Plugin按自身选择器生成，不要求人员填写。</param>
    /// <param name="resourceKey">规范物理资源键；同一物理设备只对应一个键，用于互斥与一致性校验。</param>
    /// <param name="configurationSummary">私有配置摘要（确定性文本）；进入CompositionId，使设备参数变化产生新组合身份。</param>
    /// <exception cref="ArgumentException">任一值为空或仅含空白字符。</exception>
    public VisionDeviceSettingsParseResult(
        string providerBindingId,
        string resourceKey,
        string configurationSummary)
    {
        if (string.IsNullOrWhiteSpace(providerBindingId))
            throw new ArgumentException("Provider绑定身份不能为空。", nameof(providerBindingId));
        if (string.IsNullOrWhiteSpace(resourceKey))
            throw new ArgumentException("物理资源键不能为空。", nameof(resourceKey));
        if (string.IsNullOrWhiteSpace(configurationSummary))
            throw new ArgumentException("配置摘要不能为空。", nameof(configurationSummary));

        ProviderBindingId = providerBindingId.Trim();
        ResourceKey = resourceKey.Trim();
        ConfigurationSummary = configurationSummary.Trim();
    }

    /// <summary>Plugin私有设备绑定身份；由Plugin按自身选择器生成，不要求人员填写。</summary>
    public string ProviderBindingId { get; }

    /// <summary>规范物理资源键；同一物理设备只对应一个键，用于互斥与一致性校验。</summary>
    public string ResourceKey { get; }

    /// <summary>私有配置摘要（确定性文本）；进入CompositionId，使设备参数变化产生新组合身份。</summary>
    public string ConfigurationSummary { get; }
}
