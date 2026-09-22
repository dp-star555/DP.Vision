using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// 打开设备所需的中立绑定：公共层只认身份，插件私有状态原样转交。
/// </summary>
/// <remarks>
/// <para>
/// 引入本类型是为了让 <c>deviceSettings</c> 成为设备配置的**唯一来源**。
/// 在此之前，Provider 的私有绑定只能从"插件私有配置"这条独立通道进入，
/// 而机器配置里的 <c>deviceSettings</c> 解析完只留下身份、资源键与摘要三行文本，
/// 真正描述设备的字段被丢弃——于是同一台相机必须在两处各写一遍，
/// 只填 <c>deviceSettings</c> 的部署会在打开设备时报"私有配置中没有绑定"。
/// </para>
/// <para>
/// <see cref="ProviderState"/> 由声明该 AcquisitionType 的插件在
/// <c>DeviceSettingsParser</c> 里创建，由公共层**原样**转交给同一个插件的 Provider。
/// 公共层不读取、不比较、不序列化它，因此公共契约仍然不依赖任何厂商类型；
/// Provider 收到后应校验类型是否为自己的绑定类型，不匹配就明确失败而不是猜。
/// </para>
/// </remarks>
public sealed class VisionAcquisitionProviderBinding
{
    /// <summary>创建打开绑定。</summary>
    /// <param name="providerBindingId">Provider 私有设备绑定身份；用于诊断与冲突判定。</param>
    /// <param name="providerState">插件私有绑定对象；公共层原样转交。为空表示插件不需要额外状态。</param>
    /// <exception cref="ArgumentException">绑定身份为空或仅含空白字符。</exception>
    public VisionAcquisitionProviderBinding(string providerBindingId, object? providerState = null)
    {
        if (string.IsNullOrWhiteSpace(providerBindingId))
            throw new ArgumentException("Provider绑定身份不能为空。", nameof(providerBindingId));

        ProviderBindingId = providerBindingId.Trim();
        ProviderState = providerState;
    }

    /// <summary>Provider 私有设备绑定身份。</summary>
    public string ProviderBindingId { get; }

    /// <summary>插件私有绑定对象；公共层不解释其类型与内容。</summary>
    public object? ProviderState { get; }
}
