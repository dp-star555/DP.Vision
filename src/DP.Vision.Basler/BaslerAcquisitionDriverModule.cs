using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// Basler采集Driver Module。只向候选Type Builder贡献AcquisitionType，不接触正式Catalog；
/// 自动发现只使用无参构造。
/// <para>
/// 与HALCON不同，本Provider的"缺SDK"是运行时问题：pylon托管程序集随NuGet包还原，
/// 但原生运行时可能没有部署。因此健康报告探测的是原生运行时可解析性，而不是编译期开关。
/// </para>
/// </summary>
public sealed class BaslerAcquisitionDriverModule : IVisionAcquisitionDriverModule, IVisionAcquisitionDriverModuleHealth
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.basler.driver";

    /// <summary>本Module声明的插件身份；进入Type注册，用于把逻辑源归到某个Provider名下。</summary>
    public const string PluginIdentity = "dp.vision.basler";

    /// <summary>Basler面阵Type身份。</summary>
    public const string AreaScanTypeId = "dp.acquisition.basler.area";

    /// <summary>Type实现版本；进入Catalog清单与部署记录。</summary>
    public const string TypeVersion = "1.0.0";

    /// <summary>设备配置契约版本；机器配置引用它来解析deviceSettings。</summary>
    public const int DeviceSettingsVersion = 1;

    private readonly Func<bool> _isRuntimeDeployed;

    /// <summary>创建Driver Module；运行时部署状态取自进程内的原生库解析探测。</summary>
    public BaslerAcquisitionDriverModule()
        : this(static () => BaslerPylonRuntime.IsDeployed)
    {
    }

    /// <summary>
    /// 创建Driver Module并注入运行时部署探测。目录扫描只使用无参构造；
    /// 该重载用于让"已安装但缺运行时"这条诊断路径在装有pylon的机器上也能被确定性验证。
    /// </summary>
    /// <param name="isRuntimeDeployed">报告本进程是否部署了 pylon 运行时。</param>
    /// <exception cref="ArgumentNullException">探测为空。</exception>
    public BaslerAcquisitionDriverModule(Func<bool> isRuntimeDeployed) =>
        _isRuntimeDeployed = isRuntimeDeployed ?? throw new ArgumentNullException(nameof(isRuntimeDeployed));

    /// <inheritdoc/>
    public string ExtensionId => ModuleIdentity;

    /// <inheritdoc/>
    public void Contribute(IVisionAcquisitionTypeContributionBuilder builder)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));

        builder.Register(new VisionAcquisitionTypeRegistration(
            AreaScanTypeId,
            PluginIdentity,
            TypeVersion,
            EVisionAcquisitionKind.AreaScan,
            DeviceSettingsVersion,
            "Basler 面阵相机",
            new VisionAcquisitionTypeCapabilities(
                SupportsFreeRun: true,
                SupportsSoftwareTrigger: true,
                SupportsExternalTrigger: true,
                SupportsCompleteFrameCallback: true),
            () => new BaslerAcquisitionProvider(),
            BaslerDeviceSettingsParser.Parse));
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
