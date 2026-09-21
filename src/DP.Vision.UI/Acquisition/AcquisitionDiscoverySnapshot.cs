using System.Collections.Generic;
using DP.Vision.Acquisition;

namespace DP.Vision.UI.Acquisition;

/// <summary>
/// 一次设备发现的结果：候选设备加各Provider的失败诊断。
/// <para>
/// 失败与候选放在同一个快照里是刻意的：缺一个SDK不能让另一个SDK的相机也看不见，
/// 但"某个Provider根本没查成"必须和"现场确实没有设备"在界面上截然不同。
/// </para>
/// </summary>
public sealed class AcquisitionDiscoverySnapshot
{
    /// <summary>创建发现快照。</summary>
    /// <param name="devices">合并后的候选设备，顺序确定。</param>
    /// <param name="providerFailures">发现失败的Provider诊断，按ProviderId排序。</param>
    internal AcquisitionDiscoverySnapshot(
        IReadOnlyList<AcquisitionDiscoveredDevice> devices,
        IReadOnlyList<AcquisitionDiscoveryFailure> providerFailures)
    {
        Devices = devices;
        ProviderFailures = providerFailures;
    }

    /// <summary>合并后的候选设备；现场确实没有设备时为空列表。</summary>
    public IReadOnlyList<AcquisitionDiscoveredDevice> Devices { get; }

    /// <summary>发现失败的Provider诊断；全部成功时为空列表。</summary>
    public IReadOnlyList<AcquisitionDiscoveryFailure> ProviderFailures { get; }
}

/// <summary>一个Provider的设备发现失败诊断；带Provider身份，否则无法判断该去装哪个插件。</summary>
public sealed class AcquisitionDiscoveryFailure
{
    /// <summary>创建失败诊断。</summary>
    /// <param name="providerId">失败的Provider稳定身份。</param>
    /// <param name="message">失败原因，直接来自异常文本。</param>
    internal AcquisitionDiscoveryFailure(string providerId, string message)
    {
        ProviderId = providerId;
        Message = message;
    }

    /// <summary>失败的Provider稳定身份。</summary>
    public string ProviderId { get; }

    /// <summary>失败原因。</summary>
    public string Message { get; }
}

/// <summary>
/// 界面上的一个候选设备；搬运 <see cref="VisionDeviceDescriptor"/> 并给出与当前机器配置的比对结论。
/// <para>
/// 候选不代表设备已打开或已占用：发现只回答"现场有哪些设备可被引用"。
/// </para>
/// </summary>
public sealed class AcquisitionDiscoveredDevice
{
    /// <summary>由候选描述与比对结论构造。</summary>
    /// <param name="descriptor">Provider报告的候选设备描述。</param>
    /// <param name="isConfigured">该候选的规范资源键是否已被当前机器配置引用。</param>
    internal AcquisitionDiscoveredDevice(VisionDeviceDescriptor descriptor, bool isConfigured)
    {
        ProviderId = descriptor.ProviderId;
        ProviderBindingId = descriptor.ProviderBindingId;
        CanonicalKey = descriptor.CanonicalKey;
        DisplayName = descriptor.DisplayName;
        VendorName = descriptor.VendorName;
        ModelName = descriptor.ModelName;
        SerialNumber = descriptor.SerialNumber;
        IsConfigured = isConfigured;
    }

    /// <summary>提供该候选的Provider稳定身份。</summary>
    public string ProviderId { get; }

    /// <summary>Provider配置内的设备绑定身份；写入deviceSettings时用它。</summary>
    public string ProviderBindingId { get; }

    /// <summary>规范资源键；Provider无法提供时为空。</summary>
    public string? CanonicalKey { get; }

    /// <summary>面向操作员的显示名称。</summary>
    public string DisplayName { get; }

    /// <summary>厂商名称；不可得时为空。</summary>
    public string? VendorName { get; }

    /// <summary>型号名称；不可得时为空。</summary>
    public string? ModelName { get; }

    /// <summary>设备序列号；不可得时为空。</summary>
    public string? SerialNumber { get; }

    /// <summary>
    /// 是否已被当前机器配置引用：以规范资源键与已发布Source的ResourceKey逐一比对得出。
    /// <para>规范资源键为空时无法比对，按未配置处理——宁可让操作员多看一眼，也不要宣称"已经在用"。</para>
    /// </summary>
    public bool IsConfigured { get; }

    /// <summary>是否尚未被当前机器配置引用；界面据此把候选标成"可添加"。</summary>
    public bool IsUnconfigured => !IsConfigured;
}