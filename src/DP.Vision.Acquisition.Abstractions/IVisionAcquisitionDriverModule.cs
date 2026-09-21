using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// 采集Driver插件Module。只向候选Builder贡献AcquisitionType注册，不接触正式Catalog；
/// 贡献失败时调用方持有的冻结Catalog保持不变。一个Module可以贡献多个Type
/// （例如同一厂商面阵与线扫各一个AcquisitionTypeId）。
/// </summary>
public interface IVisionAcquisitionDriverModule
{
    /// <summary>Module稳定身份，与PluginId一起用于唯一性校验。</summary>
    string ExtensionId { get; }

    /// <summary>向候选贡献Builder提交本Module的AcquisitionType注册。</summary>
    /// <param name="builder">本次Catalog组合的私有候选Builder。</param>
    void Contribute(IVisionAcquisitionTypeContributionBuilder builder);
}

/// <summary>候选贡献入口；只在一次候选Catalog组合期间有效。</summary>
public interface IVisionAcquisitionTypeContributionBuilder
{
    /// <summary>注册一个AcquisitionType候选。</summary>
    /// <param name="registration">AcquisitionType注册信息。</param>
    void Register(VisionAcquisitionTypeRegistration registration);
}

/// <summary>
/// AcquisitionType能力集；描述该Type在设备就绪后支持的取图与触发路径。
/// 触发/参数能力与完整帧回调在Catalog冻结前做一致性校验。
/// </summary>
/// <param name="SupportsFreeRun">支持自由运行连续取帧。</param>
/// <param name="SupportsSoftwareTrigger">支持软件触发取帧。</param>
/// <param name="SupportsExternalTrigger">支持外部硬件触发。</param>
/// <param name="SupportsCompleteFrameCallback">支持完整帧回调；外部触发Source必须使用OnConnect流，因此必须声明本能力。</param>
public sealed record VisionAcquisitionTypeCapabilities(
    bool SupportsFreeRun = false,
    bool SupportsSoftwareTrigger = false,
    bool SupportsExternalTrigger = false,
    bool SupportsCompleteFrameCallback = false)
{
    /// <summary>是否至少存在一条取图路径；完全为空的能力声明会在Catalog冻结前被拒绝。</summary>
    public bool HasAnyCapturePath =>
        SupportsFreeRun || SupportsSoftwareTrigger || SupportsExternalTrigger || SupportsCompleteFrameCallback;
}

/// <summary>
/// AcquisitionType候选注册；工厂在Catalog冻结后按需创建设备Adapter实例。
/// 机器配置不通过CLR完整类型名引用Type，只引用AcquisitionTypeId。
/// </summary>
/// <param name="AcquisitionTypeId">Type稳定身份，例如dp.acquisition.basler.area。</param>
/// <param name="PluginId">声明该Type的插件身份。</param>
/// <param name="Version">Type实现版本。</param>
/// <param name="Kind">采集几何形态：面阵或线扫。</param>
/// <param name="DeviceSettingsVersion">设备配置契约版本；机器配置引用它来解析deviceSettings。</param>
/// <param name="DisplayName">面向操作员的Type显示名。</param>
/// <param name="Capabilities">设备就绪后支持的取图与触发路径。</param>
/// <param name="Factory">创建设备Adapter（Provider实例）的工厂。</param>
public sealed record VisionAcquisitionTypeRegistration(
    string AcquisitionTypeId,
    string PluginId,
    string Version,
    EVisionAcquisitionKind Kind,
    int DeviceSettingsVersion,
    string DisplayName,
    VisionAcquisitionTypeCapabilities Capabilities,
    Func<IVisionAcquisitionProvider> Factory);
