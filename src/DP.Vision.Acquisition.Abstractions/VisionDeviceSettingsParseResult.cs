using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// Plugin解析自身deviceSettings后生成的验证结果。公共层不解释设备字段，
/// 只使用其绑定身份、规范资源键与配置摘要；字段含义与校验由对应Plugin拥有。
/// <para>
/// 除上述三行文本外，结果还携带 <see cref="ProviderState"/>：插件解析出的私有绑定对象。
/// 公共层不解释它，只把它原样带到 <c>IVisionAcquisitionProvider.OpenAsync</c>，
/// 这样 <c>deviceSettings</c> 就能成为设备配置的唯一来源。
/// </para>
/// </summary>
public sealed record VisionDeviceSettingsParseResult
{
    /// <summary>创建解析结果。</summary>
    /// <param name="providerBindingId">Plugin私有设备绑定身份；由Plugin按自身选择器生成，不要求人员填写。</param>
    /// <param name="resourceKey">规范物理资源键；同一物理设备只对应一个键，用于互斥与一致性校验。</param>
    /// <param name="configurationSummary">私有配置摘要（确定性文本）；进入CompositionId，使设备参数变化产生新组合身份。</param>
    /// <param name="providerState">
    /// Plugin私有绑定对象；公共层原样转交给该Provider的 <c>OpenAsync</c>，不解释其类型与内容。
    /// 这是"deviceSettings是设备配置唯一来源"的承载物：设备字段只在解析器里解析一次，
    /// 之后随绑定一路带到打开设备，不再需要第二条私有配置通道重复声明同一台设备。
    /// </param>
    /// <exception cref="ArgumentException">任一文本值为空或仅含空白字符。</exception>
    public VisionDeviceSettingsParseResult(
        string providerBindingId,
        string resourceKey,
        string configurationSummary,
        object? providerState = null)
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
        ProviderState = providerState;
    }

    /// <summary>Plugin私有设备绑定身份；由Plugin按自身选择器生成，不要求人员填写。</summary>
    public string ProviderBindingId { get; }

    /// <summary>规范物理资源键；同一物理设备只对应一个键，用于互斥与一致性校验。</summary>
    public string ResourceKey { get; }

    /// <summary>私有配置摘要（确定性文本）；进入CompositionId，使设备参数变化产生新组合身份。</summary>
    public string ConfigurationSummary { get; }

    /// <summary>Plugin私有绑定对象；公共层不解释其类型与内容，只原样转交给该Provider。</summary>
    public object? ProviderState { get; }
}
