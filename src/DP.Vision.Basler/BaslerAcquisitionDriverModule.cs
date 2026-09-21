using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// Basler采集Driver Module。只向候选Type Builder贡献AcquisitionType，不接触正式Catalog；
/// 自动发现只使用无参构造。
/// </summary>
public sealed class BaslerAcquisitionDriverModule : IVisionAcquisitionDriverModule
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.basler.driver";

    /// <summary>Basler面阵Type身份。</summary>
    public const string AreaScanTypeId = "dp.acquisition.basler.area";

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

        builder.Register(new VisionAcquisitionTypeRegistration(
            AreaScanTypeId,
            BaslerAcquisitionProviderPlugin.PluginIdentity,
            TypeVersion,
            EVisionAcquisitionKind.AreaScan,
            DeviceSettingsVersion,
            "Basler 面阵相机",
            new VisionAcquisitionTypeCapabilities(
                SupportsFreeRun: true,
                SupportsSoftwareTrigger: true,
                SupportsExternalTrigger: true,
                SupportsCompleteFrameCallback: true),
            () => new BaslerAcquisitionProvider()));
    }
}
