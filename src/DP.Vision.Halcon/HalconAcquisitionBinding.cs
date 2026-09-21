using System;

namespace DP.Vision.Halcon;

/// <summary>HALCON Provider私有设备绑定；公共配置不解释这些字段，只引用其绑定身份。</summary>
public sealed class HalconAcquisitionBinding
{
    /// <summary>外部回调长连接未在私有配置里指定抓取超时时的默认值（毫秒）。</summary>
    public const int DefaultGrabTimeoutMilliseconds = 5000;

    /// <summary>创建绑定。</summary>
    /// <param name="bindingId">Provider私有绑定身份，供机器配置的providerBindingId引用。</param>
    /// <param name="interfaceName">HALCON采集接口名，例如GigEVision2。</param>
    /// <param name="deviceName">HALCON设备名，例如相机IP或厂商设备串。</param>
    /// <param name="serialNumber">可选设备序列号；提供后用于报告规范资源键。</param>
    /// <param name="triggerSource">外部硬件触发使用的触发源，例如 GenICam 的 <c>Line1</c>；只有外部触发模式需要。</param>
    /// <param name="grabTimeoutMilliseconds">外部回调长连接的抓取等待上限；同时是停止等待的上界。</param>
    /// <exception cref="ArgumentException">绑定身份、接口名或设备名为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">抓取超时不为正。</exception>
    public HalconAcquisitionBinding(
        string bindingId,
        string interfaceName,
        string deviceName,
        string? serialNumber = null,
        string? triggerSource = null,
        int grabTimeoutMilliseconds = DefaultGrabTimeoutMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(bindingId))
            throw new ArgumentException("绑定身份不能为空。", nameof(bindingId));
        if (string.IsNullOrWhiteSpace(interfaceName))
            throw new ArgumentException("采集接口名不能为空。", nameof(interfaceName));
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("设备名不能为空。", nameof(deviceName));
        if (grabTimeoutMilliseconds < 1)
            throw new ArgumentOutOfRangeException(
                nameof(grabTimeoutMilliseconds), "抓取超时必须为正；无限等待会让停止无法收敛。");

        BindingId = bindingId.Trim();
        InterfaceName = interfaceName.Trim();
        DeviceName = deviceName.Trim();
        SerialNumber = serialNumber is null || string.IsNullOrWhiteSpace(serialNumber) ? null : serialNumber.Trim();
        TriggerSource = triggerSource is null || string.IsNullOrWhiteSpace(triggerSource) ? null : triggerSource.Trim();
        GrabTimeoutMilliseconds = grabTimeoutMilliseconds;
    }

    /// <summary>Provider私有绑定身份。</summary>
    public string BindingId { get; }

    /// <summary>HALCON采集接口名。</summary>
    public string InterfaceName { get; }

    /// <summary>HALCON设备名。</summary>
    public string DeviceName { get; }

    /// <summary>设备序列号；未配置时为空。</summary>
    public string? SerialNumber { get; }

    /// <summary>
    /// 外部硬件触发的触发源；未配置时为空，表示"保持设备当前触发设置"。
    /// <para>
    /// 它是**外部回调缓冲源**能进入外部触发模式的必要条件：HALCON 的通用采集层没有触发源参数，
    /// 触发源只能按具体接口的设备参数写入，因此必须由私有配置显式声明，不能猜物理接线。
    /// </para>
    /// </summary>
    public string? TriggerSource { get; }

    /// <summary>外部回调长连接的抓取等待上限（毫秒）；它同时决定停止等待的上界。</summary>
    public int GrabTimeoutMilliseconds { get; }

    /// <summary>HALCON设备键，格式为"接口名|设备名"；只在本Provider内部使用，不进入工作流文档。</summary>
    public string CameraId => InterfaceName + "|" + DeviceName;

    /// <summary>Provider报告的规范资源键；未配置序列号时为空，表示本Provider无法提供规范身份。</summary>
    public string? CanonicalKey => SerialNumber is null ? null : "camera:serial:" + SerialNumber;
}
