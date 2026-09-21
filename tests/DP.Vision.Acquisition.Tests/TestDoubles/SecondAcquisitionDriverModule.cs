using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 第二个公开Driver Module：让加载测试能观察到"同一程序集发现多个Module、按ExtensionId稳定排序"，
/// 也证明扫描器按接口发现而不是按Manifest声明。贡献独立的TypeId，避免与
/// <see cref="ConfigurableAcquisitionDriverModule"/> 在Catalog组合时冲突。
/// </summary>
public sealed class SecondAcquisitionDriverModule : IVisionAcquisitionDriverModule
{
    /// <summary>Module稳定身份；按序数排在本测试集第一个Module之后。</summary>
    public const string ModuleIdentity = "dp.vision.test.driver.z";

    /// <summary>本Module贡献的面阵Type身份。</summary>
    public const string AreaScanTypeId = "dp.acquisition.test.second.area";

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
            "1.0.0",
            EVisionAcquisitionKind.AreaScan,
            1,
            "第二个测试面阵",
            new VisionAcquisitionTypeCapabilities(SupportsSoftwareTrigger: true),
            () => FakeVisionProvider.WithDevices(ConfigurableAcquisitionProviderPlugin.ProviderIdentity)));
    }
}
