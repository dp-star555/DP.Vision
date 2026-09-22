using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON采集Driver Module。只向候选Type Builder贡献AcquisitionType，不接触正式Catalog；
/// 自动发现只使用无参构造。
/// <para>
/// 面阵与线扫各贡献一个AcquisitionType，两者共享同一份私有配置契约、同一份能力声明和同一个设备适配器：
/// 线扫相机在HALCON采集接口下同样由SDK完成整图组装，<c>grab_image</c>返回的就是一张完整图像，
/// 因此适配器不需要（也不允许）在公共层引入Line/Chunk模型，只向上交付SDK完成的整张图。
/// 两个Type的区别只在 <see cref="EVisionAcquisitionKind"/>：它决定工作流节点按几何形态过滤Source。
/// </para>
/// <para>
/// 本Module同时报告Provider级健康：未装配HALCON SDK的构建里本程序集不含任何采集实现，
/// 必须让"缺SDK"在Type Catalog冻结时就变成逻辑源的不可用诊断，而不是等到采集时才失败。
/// </para>
/// </summary>
public sealed class HalconAcquisitionDriverModule : IVisionAcquisitionDriverModule, IVisionAcquisitionDriverModuleHealth
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.halcon.driver";

    /// <summary>本Module声明的插件身份；进入Type注册，用于把逻辑源归到某个Provider名下。</summary>
    public const string PluginIdentity = "dp.vision.halcon";

    /// <summary>HALCON面阵Type身份。</summary>
    public const string AreaScanTypeId = "dp.acquisition.halcon.area";

    /// <summary>HALCON线扫Type身份。</summary>
    public const string LineScanTypeId = "dp.acquisition.halcon.line";

    /// <summary>Type实现版本；进入Catalog清单与部署记录。</summary>
    public const string TypeVersion = "1.0.0";

    /// <summary>设备配置契约版本；机器配置引用它来解析deviceSettings。</summary>
    public const int DeviceSettingsVersion = 1;

    private readonly Func<bool> _isSdkDeployed;

    /// <summary>创建Driver Module；SDK部署状态取自本程序集的编译期开关。</summary>
    public HalconAcquisitionDriverModule()
        : this(static () => HalconStreamCameras.IsSdkEnabled)
    {
    }

    /// <summary>
    /// 创建Driver Module并注入SDK部署探测。目录扫描只使用无参构造；
    /// 该重载用于让"已安装但缺SDK"这条诊断路径在装有SDK的机器上也能被确定性验证。
    /// </summary>
    /// <param name="isSdkDeployed">报告本进程是否部署了HALCON SDK。</param>
    /// <exception cref="ArgumentNullException">探测为空。</exception>
    public HalconAcquisitionDriverModule(Func<bool> isSdkDeployed) =>
        _isSdkDeployed = isSdkDeployed ?? throw new ArgumentNullException(nameof(isSdkDeployed));

    /// <inheritdoc/>
    public string ExtensionId => ModuleIdentity;

    /// <inheritdoc/>
    public void Contribute(IVisionAcquisitionTypeContributionBuilder builder)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));

        // 外部触发与完整帧回调依赖流式实现，只在SDK已编译进本程序集时声明；
        // 缺SDK时的可用性由本Module的健康报告单独给出，不伪造Type能力。
        var supportsStreaming = HalconStreamCameras.IsSdkEnabled;
        builder.Register(new VisionAcquisitionTypeRegistration(
            AreaScanTypeId,
            PluginIdentity,
            TypeVersion,
            EVisionAcquisitionKind.AreaScan,
            DeviceSettingsVersion,
            "HALCON 面阵相机",
            new VisionAcquisitionTypeCapabilities(
                SupportsFreeRun: true,
                SupportsSoftwareTrigger: true,
                SupportsExternalTrigger: supportsStreaming,
                SupportsCompleteFrameCallback: supportsStreaming),
            () => new HalconAcquisitionProvider(),
            HalconDeviceSettingsParser.Parse));

        // 线扫：整图由SDK（采集接口/采集卡）组装完成后交给适配器，适配器只交付整张图，
        // 因此与面阵共用同一个适配器工厂和同一个deviceSettings解析器，只在Kind上区分。
        builder.Register(new VisionAcquisitionTypeRegistration(
            LineScanTypeId,
            PluginIdentity,
            TypeVersion,
            EVisionAcquisitionKind.LineScan,
            DeviceSettingsVersion,
            "HALCON 线扫相机",
            new VisionAcquisitionTypeCapabilities(
                SupportsFreeRun: true,
                SupportsSoftwareTrigger: true,
                SupportsExternalTrigger: supportsStreaming,
                SupportsCompleteFrameCallback: supportsStreaming),
            () => new HalconAcquisitionProvider(),
            HalconDeviceSettingsParser.Parse));
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
