using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON采集Provider插件入口。插件目录只通过本类型加载HALCON，
/// 因此宿主不需要在编译期引用任何HALCON类型，也不能反向引用工作流复用其Loader。
/// </summary>
public sealed class HalconAcquisitionProviderPlugin : IVisionAcquisitionProviderPlugin, IVisionAcquisitionProviderHealth
{
    /// <summary>插件稳定身份；必须与 <c>plugin.json</c> 声明的 pluginId 一致。</summary>
    public const string PluginIdentity = "dp.vision.halcon";

    /// <summary>插件实现版本；与Manifest版本一起进入部署记录。</summary>
    public const string PluginVersion = "1.0.0";

    private readonly Func<bool> _isSdkDeployed;

    /// <summary>创建插件入口；SDK部署状态取自本程序集的编译期开关。</summary>
    public HalconAcquisitionProviderPlugin()
        : this(static () => HalconStreamCameras.IsSdkEnabled)
    {
    }

    /// <summary>
    /// 创建插件入口并注入SDK部署探测。插件目录加载只使用无参构造；
    /// 该重载用于让"已安装但缺SDK"这条诊断路径在装有SDK的机器上也能被确定性验证。
    /// </summary>
    /// <param name="isSdkDeployed">报告本进程是否部署了HALCON SDK。</param>
    /// <exception cref="ArgumentNullException">探测为空。</exception>
    public HalconAcquisitionProviderPlugin(Func<bool> isSdkDeployed) =>
        _isSdkDeployed = isSdkDeployed ?? throw new ArgumentNullException(nameof(isSdkDeployed));

    /// <inheritdoc/>
    public string PluginId => PluginIdentity;

    /// <inheritdoc/>
    /// <exception cref="VisionSourceConfigurationException">私有配置非空：设备配置已统一由机器配置的 deviceSettings 提供。</exception>
    public IVisionAcquisitionProviderModule CreateModule(string? configuration)
    {
        // 设备绑定曾经从这里解析，现在只从机器配置的 deviceSettings 来。
        // 静默忽略遗留私有配置会把"配置没生效"藏起来，所以这里明确拒绝并指路。
        if (!string.IsNullOrWhiteSpace(configuration))
            throw new VisionSourceConfigurationException(
                "HALCON Provider 不再接受插件私有配置：设备字段（interfaceName/deviceName/serialNumber/"
                + "triggerSource/grabTimeoutMilliseconds）已统一由机器配置中每个逻辑源的 deviceSettings 提供；"
                + "请把该配置迁移到对应相机的 deviceSettings，并清空 Provider 私有配置。");
        return new HalconAcquisitionProviderModule();
    }

    /// <summary>
    /// 报告HALCON Provider当前是否可用。本程序集在未装配SDK的构建下不包含任何采集实现，
    /// 此时必须让宿主在首节点执行前就看到Provider级诊断，而不是等到采集时才失败。
    /// </summary>
    /// <param name="diagnostic">不可用原因；可用时为空。</param>
    /// <returns>可用时返回 <see langword="true"/>。</returns>
    public bool TryGetHealth(out string? diagnostic)
    {
        if (_isSdkDeployed())
        {
            diagnostic = null;
            return true;
        }

        diagnostic = $"Provider {HalconAcquisitionProvider.ProviderIdentity} 已安装，但 HALCON SDK 未部署"
            + "（未找到 halcondotnet.dll）；请安装 HALCON 运行时后重新构建 DP.Vision.Halcon，"
            + "或把依赖该Provider的逻辑源标记为不可用。";
        return false;
    }
}
