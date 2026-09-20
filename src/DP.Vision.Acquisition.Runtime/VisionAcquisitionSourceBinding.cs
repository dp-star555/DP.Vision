using System;

namespace DP.Vision.Acquisition;

/// <summary>机器级公共Source绑定；把逻辑SourceId固定到一个Provider和设备绑定，工作流文档不解释这些字段。</summary>
public sealed record VisionAcquisitionSourceBinding
{
    /// <summary>创建Source绑定。</summary>
    /// <param name="sourceId">逻辑视觉源标识。</param>
    /// <param name="providerId">唯一允许服务该Source的Provider身份。</param>
    /// <param name="providerBindingId">该Provider私有配置内的设备绑定身份。</param>
    /// <param name="resourceKey">物理资源键，用于识别同一硬件并协调互斥。</param>
    /// <param name="sharingPolicy">同一资源键上的并发协调策略。</param>
    /// <exception cref="ArgumentException">任一身份为空或资源键含空白字符。</exception>
    public VisionAcquisitionSourceBinding(
        string sourceId,
        string providerId,
        string providerBindingId,
        string resourceKey,
        EVisionSourceSharingPolicy sharingPolicy = EVisionSourceSharingPolicy.ExclusiveOperation)
    {
        SourceId = Require(sourceId, "逻辑源标识", nameof(sourceId));
        ProviderId = Require(providerId, "Provider身份", nameof(providerId));
        ProviderBindingId = Require(providerBindingId, "Provider绑定身份", nameof(providerBindingId));
        ResourceKey = Require(resourceKey, "物理资源键", nameof(resourceKey));
        if (ResourceKey.IndexOf(' ') >= 0)
            throw new ArgumentException("物理资源键不能包含空白字符。", nameof(resourceKey));
        SharingPolicy = sharingPolicy;
    }

    /// <summary>逻辑视觉源标识。</summary>
    public string SourceId { get; }

    /// <summary>服务该Source的Provider稳定身份。</summary>
    public string ProviderId { get; }

    /// <summary>Provider私有配置内的设备绑定身份。</summary>
    public string ProviderBindingId { get; }

    /// <summary>物理资源键；多个Source可有意映射同一键，但必须共享Provider与绑定。</summary>
    public string ResourceKey { get; }

    /// <summary>并发协调策略；未显式配置时为ExclusiveOperation，冲突时确定性失败。</summary>
    public EVisionSourceSharingPolicy SharingPolicy { get; }

    private static string Require(string value, string label, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(label + "不能为空。", parameterName);
        return value.Trim();
    }
}
