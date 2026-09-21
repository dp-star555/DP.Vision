using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// 已冻结的AcquisitionType描述。Catalog发布后不可变，已开始的运行持有自己的Catalog快照；
/// 机器配置只引用AcquisitionTypeId，不包含CLR程序集完整类型名。
/// </summary>
public sealed class VisionAcquisitionTypeDescriptor
{
    internal VisionAcquisitionTypeDescriptor(
        string acquisitionTypeId,
        string pluginId,
        string version,
        EVisionAcquisitionKind kind,
        int deviceSettingsVersion,
        string displayName,
        VisionAcquisitionTypeCapabilities capabilities,
        Func<IVisionAcquisitionProvider> factory)
    {
        AcquisitionTypeId = acquisitionTypeId;
        PluginId = pluginId;
        Version = version;
        Kind = kind;
        DeviceSettingsVersion = deviceSettingsVersion;
        DisplayName = displayName;
        Capabilities = capabilities;
        Factory = factory;
    }

    /// <summary>Type稳定身份，例如dp.acquisition.basler.area。</summary>
    public string AcquisitionTypeId { get; }

    /// <summary>声明该Type的插件身份，例如dp.vision.basler。</summary>
    public string PluginId { get; }

    /// <summary>Type实现版本；与插件版本一起进入部署记录。</summary>
    public string Version { get; }

    /// <summary>采集几何形态：面阵或线扫。</summary>
    public EVisionAcquisitionKind Kind { get; }

    /// <summary>设备配置契约版本；机器配置引用它来解析deviceSettings。</summary>
    public int DeviceSettingsVersion { get; }

    /// <summary>面向操作员的Type显示名。</summary>
    public string DisplayName { get; }

    /// <summary>设备就绪后支持的取图与触发路径。</summary>
    public VisionAcquisitionTypeCapabilities Capabilities { get; }

    /// <summary>创建设备Adapter（Provider实例）的工厂；Catalog冻结后按需调用。</summary>
    public Func<IVisionAcquisitionProvider> Factory { get; }
}
