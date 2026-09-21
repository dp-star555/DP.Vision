using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON采集Driver Module。只向候选Type Builder贡献AcquisitionType，不接触正式Catalog；
/// 自动发现只使用无参构造。
/// </summary>
public sealed class HalconAcquisitionDriverModule : IVisionAcquisitionDriverModule
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.halcon.driver";

    /// <summary>HALCON面阵Type身份。</summary>
    public const string AreaScanTypeId = "dp.acquisition.halcon.area";

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
        var supportsStreaming = HalconCameraCapture.IsSdkEnabled;
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
    }
}
