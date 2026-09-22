using System;

namespace DP.Vision.Basler;

/// <summary>
/// Basler pylon Provider私有设备绑定。公共配置不解释这些字段，只引用其绑定身份；
/// 选择器必须唯一指定，避免"碰巧连上另一台相机"这种不可复现的路由。
/// <para>
/// 它是不可变值对象，因此是 <see langword="record"/>：同一份 deviceSettings 解析两次应得到相等的绑定，
/// 而绑定又会被 <c>VisionDeviceSettingsParseResult</c> 作为 <c>ProviderState</c> 参与该记录的相等性比较。
/// </para>
/// </summary>
public sealed record BaslerAcquisitionBinding
{
    /// <summary>创建绑定。</summary>
    /// <param name="bindingId">Provider私有绑定身份，供机器配置的providerBindingId引用。</param>
    /// <param name="serialNumber">设备序列号；与 <paramref name="userDefinedName"/> 二选一。</param>
    /// <param name="userDefinedName">设备用户自定义名（pylon 的 UserDefinedName）；与 <paramref name="serialNumber"/> 二选一。</param>
    /// <param name="triggerSource">外部硬件触发使用的触发源，例如 Line1；只有外部触发模式需要。</param>
    /// <exception cref="ArgumentException">绑定身份为空，或两个选择器没有恰好指定一个。</exception>
    public BaslerAcquisitionBinding(
        string bindingId,
        string? serialNumber = null,
        string? userDefinedName = null,
        string? triggerSource = null)
    {
        if (string.IsNullOrWhiteSpace(bindingId))
            throw new ArgumentException("绑定身份不能为空。", nameof(bindingId));

        var hasSerial = !string.IsNullOrWhiteSpace(serialNumber);
        var hasName = !string.IsNullOrWhiteSpace(userDefinedName);
        if (hasSerial == hasName)
        {
            throw new ArgumentException(
                "绑定必须且只能指定 serialNumber 或 userDefinedName 之一："
                + "同时给出无法判断以哪个为准，都不给出则会匹配到任意一台设备。",
                hasSerial ? nameof(userDefinedName) : nameof(serialNumber));
        }

        BindingId = bindingId.Trim();
        SerialNumber = hasSerial ? serialNumber!.Trim() : null;
        UserDefinedName = hasName ? userDefinedName!.Trim() : null;
        TriggerSource = string.IsNullOrWhiteSpace(triggerSource) ? null : triggerSource!.Trim();
    }

    /// <summary>Provider私有绑定身份。</summary>
    public string BindingId { get; }

    /// <summary>设备序列号；未按序列号选择时为空。</summary>
    public string? SerialNumber { get; }

    /// <summary>设备用户自定义名；未按名称选择时为空。</summary>
    public string? UserDefinedName { get; }

    /// <summary>外部硬件触发的触发源；未配置时为空。</summary>
    public string? TriggerSource { get; }

    /// <summary>pylon 相机信息里用于定位设备的键，取值为 <c>Basler.Pylon.CameraInfoKey</c> 中的常量名。</summary>
    public string SelectorKey => SerialNumber is not null ? "SerialNumber" : "UserDefinedName";

    /// <summary>选择器的取值。</summary>
    public string SelectorValue => SerialNumber ?? UserDefinedName!;

    /// <summary>Provider报告的规范资源键；按名称选择时为空，表示本Provider无法提供规范身份。</summary>
    public string? CanonicalKey => SerialNumber is null ? null : "camera:serial:" + SerialNumber;
}
