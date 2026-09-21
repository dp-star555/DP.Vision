using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 插件目录Driver Module加载测试用的公开Module。本类型是测试程序集里唯一公开实现
/// <see cref="IVisionAcquisitionDriverModule"/> 的类型——扫描器按"程序集里所有公开实现者"发现，
/// 因此再增加一个公开实现者会让同一个程序集产生多个Module，破坏加载测试的断言。
/// 同时贡献一个AreaScan Type和一个LineScan测试Type，证明Catalog能按Kind列出而不读任何机器配置。
/// </summary>
public sealed class ConfigurableAcquisitionDriverModule : IVisionAcquisitionDriverModule
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.test.driver";

    /// <summary>本Module贡献的面阵Type身份。</summary>
    public const string AreaScanTypeId = "dp.acquisition.test.area";

    /// <summary>本Module贡献的线扫测试Type身份（V2先允许测试Type）。</summary>
    public const string LineScanTestTypeId = "dp.acquisition.test.line";

    /// <summary>Type实现版本。</summary>
    public const string TypeVersion = "1.0.0";

    /// <summary>设备配置契约版本。</summary>
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
            ConfigurableAcquisitionProviderPlugin.PluginIdentity,
            TypeVersion,
            EVisionAcquisitionKind.AreaScan,
            DeviceSettingsVersion,
            "测试面阵相机",
            new VisionAcquisitionTypeCapabilities(
                SupportsFreeRun: true,
                SupportsSoftwareTrigger: true,
                SupportsExternalTrigger: true,
                SupportsCompleteFrameCallback: true),
            () => FakeVisionProvider.WithDevices(ConfigurableAcquisitionProviderPlugin.ProviderIdentity)));

        builder.Register(new VisionAcquisitionTypeRegistration(
            LineScanTestTypeId,
            ConfigurableAcquisitionProviderPlugin.PluginIdentity,
            TypeVersion,
            EVisionAcquisitionKind.LineScan,
            DeviceSettingsVersion,
            "测试线扫（允许测试Type）",
            new VisionAcquisitionTypeCapabilities(
                SupportsFreeRun: true,
                SupportsExternalTrigger: true,
                SupportsCompleteFrameCallback: true),
            () => FakeVisionProvider.WithDevices(ConfigurableAcquisitionProviderPlugin.ProviderIdentity)));
    }
}
