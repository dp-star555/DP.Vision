using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// Basler采集Provider插件入口。插件目录只通过本类型加载Basler，
/// 因此宿主不需要在编译期引用任何Basler类型。
///
/// 与HALCON不同，本Provider的"缺SDK"是运行时问题：pylon托管程序集随NuGet包还原，
/// 但原生运行时可能没有部署。因此健康报告探测的是原生运行时可解析性，而不是编译期开关。
/// </summary>
public sealed class BaslerAcquisitionProviderPlugin : IVisionAcquisitionProviderPlugin, IVisionAcquisitionProviderHealth
{
    /// <summary>插件稳定身份；必须与 <c>plugin.json</c> 声明的 pluginId 一致。</summary>
    public const string PluginIdentity = "dp.vision.basler";

    /// <summary>插件实现版本；与Manifest版本一起进入部署记录。</summary>
    public const string PluginVersion = "1.0.0";

    private readonly Func<bool> _isRuntimeDeployed;

    /// <summary>创建插件入口；运行时部署状态取自进程内的原生库解析探测。</summary>
    public BaslerAcquisitionProviderPlugin()
        : this(static () => BaslerPylonRuntime.IsDeployed)
    {
    }

    /// <summary>
    /// 创建插件入口并注入运行时部署探测。插件目录加载只使用无参构造；
    /// 该重载用于让"已安装但缺运行时"这条诊断路径在装有pylon的机器上也能被确定性验证。
    /// </summary>
    /// <param name="isRuntimeDeployed">报告本进程是否部署了 pylon 运行时。</param>
    /// <exception cref="ArgumentNullException">探测为空。</exception>
    public BaslerAcquisitionProviderPlugin(Func<bool> isRuntimeDeployed) =>
        _isRuntimeDeployed = isRuntimeDeployed ?? throw new ArgumentNullException(nameof(isRuntimeDeployed));

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
                "Basler Provider 不再接受插件私有配置：设备字段（serialNumber/userDefinedName/"
                + "triggerSource/pixelFormat）已统一由机器配置中每个逻辑源的 deviceSettings 提供；"
                + "请把该配置迁移到对应相机的 deviceSettings，并清空 Provider 私有配置。");
        return new BaslerAcquisitionProviderModule();
    }

    /// <summary>
    /// 报告Basler Provider当前是否可用。缺运行时必须让宿主在首节点执行前就看到Provider级诊断，
    /// 而不是等到采集时抛原生异常。
    /// </summary>
    /// <param name="diagnostic">不可用原因；可用时为空。</param>
    /// <returns>可用时返回 <see langword="true"/>。</returns>
    public bool TryGetHealth(out string? diagnostic)
    {
        if (_isRuntimeDeployed())
        {
            diagnostic = null;
            return true;
        }

        diagnostic = BaslerPylonRuntime.DescribeMissingRuntime();
        return false;
    }
}
