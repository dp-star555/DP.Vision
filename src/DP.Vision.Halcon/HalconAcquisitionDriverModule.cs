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
/// </summary>
public sealed class HalconAcquisitionDriverModule : IVisionAcquisitionDriverModule
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.halcon.driver";

    /// <summary>HALCON面阵Type身份。</summary>
    public const string AreaScanTypeId = "dp.acquisition.halcon.area";

    /// <summary>HALCON线扫Type身份。</summary>
    public const string LineScanTypeId = "dp.acquisition.halcon.line";

    /// <summary>Type实现版本；进入Catalog清单与部署记录。</summary>
    public const string TypeVersion = "1.0.0";

    /// <summary>设备配置契约版本；机器配置引用它来解析deviceSettings。</summary>
    public const int DeviceSettingsVersion = 1;

    /// <inheritdoc/>
    public string ExtensionId => ModuleIdentity;

    /// <inheritdoc/>
    public void Contribute(IVisionAcquisitionTypeContributionBuilder builder)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));

        // 外部触发与完整帧回调依赖流式实现，只在SDK已编译进本程序集时声明；
        // 缺SDK时的可用性由Provider健康报告单独给出，不伪造Type能力。
        var supportsStreaming = HalconStreamCameras.IsSdkEnabled;
        builder.Register(new VisionAcquisitionTypeRegistration(
            AreaScanTypeId,
            HalconAcquisitionProviderPlugin.PluginIdentity,
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
            HalconAcquisitionProviderPlugin.PluginIdentity,
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
}
