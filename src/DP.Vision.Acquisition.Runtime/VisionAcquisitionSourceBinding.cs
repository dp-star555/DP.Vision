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
    /// <param name="acquisitionMode">该Source采用主动单次采集还是外部回调缓冲。</param>
    /// <param name="inboxPolicy">外部回调缓冲的有界策略；主动单次采集必须为空。</param>
    /// <param name="isRequired">该源是否必需；必需源在Runtime启动阶段必须成功打开，否则Runtime不得就绪。</param>
    /// <param name="providerState">
    /// Plugin私有绑定对象（由该Type的deviceSettings解析器产生）；公共层原样保存并转交给Provider的
    /// <c>OpenAsync</c>，不解释其类型与内容。为空表示该Provider不需要额外状态。
    /// </param>
    /// <exception cref="ArgumentException">任一身份为空、资源键含空白字符，或模式与缓冲策略不匹配。</exception>
    public VisionAcquisitionSourceBinding(
        string sourceId,
        string providerId,
        string providerBindingId,
        string resourceKey,
        EVisionSourceSharingPolicy sharingPolicy = EVisionSourceSharingPolicy.ExclusiveOperation,
        EVisionAcquisitionMode acquisitionMode = EVisionAcquisitionMode.OnDemand,
        VisionFrameInboxPolicy? inboxPolicy = null,
        bool isRequired = true,
        object? providerState = null)
    {
        SourceId = Require(sourceId, "逻辑源标识", nameof(sourceId));
        ProviderId = Require(providerId, "Provider身份", nameof(providerId));
        ProviderBindingId = Require(providerBindingId, "Provider绑定身份", nameof(providerBindingId));
        ResourceKey = Require(resourceKey, "物理资源键", nameof(resourceKey));
        if (ResourceKey.IndexOf(' ') >= 0)
            throw new ArgumentException("物理资源键不能包含空白字符。", nameof(resourceKey));

        // 模式与缓冲策略必须成对出现：缺少策略会让运行期无处可退，多给策略说明配置写错了模式。
        if (acquisitionMode == EVisionAcquisitionMode.BufferedExternal)
        {
            if (inboxPolicy is null)
                throw new ArgumentException(
                    "外部回调缓冲Source必须声明待领取队列的容量、字节预算与最大帧龄；缺失时无法建立有界队列。",
                    nameof(inboxPolicy));
        }
        else if (inboxPolicy is not null)
        {
            throw new ArgumentException(
                "主动单次采集Source不接受待领取队列策略；请确认是否漏配了 BufferedExternal 模式。",
                nameof(inboxPolicy));
        }

        SharingPolicy = sharingPolicy;
        AcquisitionMode = acquisitionMode;
        InboxPolicy = inboxPolicy;
        IsRequired = isRequired;
        ProviderState = providerState;
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

    /// <summary>该Source采用主动单次采集还是外部回调缓冲；未显式配置时为OnDemand。</summary>
    public EVisionAcquisitionMode AcquisitionMode { get; }

    /// <summary>外部回调缓冲的有界策略；主动单次采集时为空。</summary>
    public VisionFrameInboxPolicy? InboxPolicy { get; }

    /// <summary>该源是否必需；必需源在Runtime启动阶段必须成功打开，否则Runtime不得就绪。</summary>
    public bool IsRequired { get; }

    /// <summary>
    /// Plugin私有绑定对象；公共层不解释其类型与内容，只在打开设备时原样转交给Provider。
    /// 它由 <c>deviceSettings</c> 解析器产生，因此设备字段不需要在第二条私有配置里重复声明。
    /// </summary>
    public object? ProviderState { get; }

    private static string Require(string value, string label, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(label + "不能为空。", parameterName);
        return value.Trim();
    }
}
