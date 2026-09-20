using System;

namespace DP.Vision.Halcon;

/// <summary>HALCON Provider私有设备绑定；公共配置不解释这些字段，只引用其绑定身份。</summary>
public sealed class HalconAcquisitionBinding
{
    /// <summary>创建绑定。</summary>
    /// <param name="bindingId">Provider私有绑定身份，供机器配置的providerBindingId引用。</param>
    /// <param name="interfaceName">HALCON采集接口名，例如GigEVision2。</param>
    /// <param name="deviceName">HALCON设备名，例如相机IP或厂商设备串。</param>
    /// <param name="serialNumber">可选设备序列号；提供后用于报告规范资源键。</param>
    /// <exception cref="ArgumentException">绑定身份、接口名或设备名为空。</exception>
    public HalconAcquisitionBinding(
        string bindingId,
        string interfaceName,
        string deviceName,
        string? serialNumber = null)
    {
        if (string.IsNullOrWhiteSpace(bindingId))
            throw new ArgumentException("绑定身份不能为空。", nameof(bindingId));
        if (string.IsNullOrWhiteSpace(interfaceName))
            throw new ArgumentException("采集接口名不能为空。", nameof(interfaceName));
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("设备名不能为空。", nameof(deviceName));
        BindingId = bindingId.Trim();
        InterfaceName = interfaceName.Trim();
        DeviceName = deviceName.Trim();
        SerialNumber = serialNumber is null || string.IsNullOrWhiteSpace(serialNumber) ? null : serialNumber.Trim();
    }

    /// <summary>Provider私有绑定身份。</summary>
    public string BindingId { get; }

    /// <summary>HALCON采集接口名。</summary>
    public string InterfaceName { get; }

    /// <summary>HALCON设备名。</summary>
    public string DeviceName { get; }

    /// <summary>设备序列号；未配置时为空。</summary>
    public string? SerialNumber { get; }

    /// <summary>HALCON设备键，格式为"接口名|设备名"；只在本Provider内部使用，不进入工作流文档。</summary>
    public string CameraId => InterfaceName + "|" + DeviceName;

    /// <summary>Provider报告的规范资源键；未配置序列号时为空，表示本Provider无法提供规范身份。</summary>
    public string? CanonicalKey => SerialNumber is null ? null : "camera:serial:" + SerialNumber;
}
