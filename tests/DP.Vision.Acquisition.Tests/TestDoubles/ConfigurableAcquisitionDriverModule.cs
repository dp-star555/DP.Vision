using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 插件目录Driver Module加载测试用的公开Module。本类型是测试程序集里唯一公开实现
/// <see cref="IVisionAcquisitionDriverModule"/> 的类型——扫描器按"程序集里所有公开实现者"发现，
/// 因此再增加一个公开实现者会让同一个程序集产生多个Module，破坏加载测试的断言。
/// 同时贡献一个AreaScan Type和一个LineScan测试Type，证明Catalog能按Kind列出而不读任何机器配置。
/// </summary>
/// <remarks>
/// 它也是测试里唯一的插件身份来源：<see cref="PluginIdentity"/> 与 <see cref="ProviderIdentity"/>
/// 曾由已删除的 <c>ConfigurableAcquisitionProviderPlugin</c> 提供，现在归到Driver Module。
/// 实现 <see cref="IVisionAcquisitionDriverModuleHealth"/> 以便确定性地覆盖"插件已安装但不可用"，
/// 而不必依赖本机是否真的装了厂商运行时。
/// </remarks>
public sealed class ConfigurableAcquisitionDriverModule : IVisionAcquisitionDriverModule, IVisionAcquisitionDriverModuleHealth
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.test.driver";

    /// <summary>插件稳定身份；进入Type注册，用于把逻辑源归到某个Provider名下。</summary>
    public const string PluginIdentity = "dp.vision.test";

    /// <summary>本Module贡献的Provider身份。</summary>
    public const string ProviderIdentity = "dp.vision.test.provider";

    /// <summary>本Module贡献的面阵Type身份。</summary>
    public const string AreaScanTypeId = "dp.acquisition.test.area";

    /// <summary>本Module贡献的线扫测试Type身份（V2先允许测试Type）。</summary>
    public const string LineScanTestTypeId = "dp.acquisition.test.line";

    /// <summary>Type实现版本。</summary>
    public const string TypeVersion = "1.0.0";

    /// <summary>设备配置契约版本。</summary>
    public const int DeviceSettingsVersion = 1;

    private readonly bool _isAvailable;
    private readonly string? _diagnostic;

    /// <summary>创建Module；默认报告可用，与"未实现健康报告"的Module行为一致。</summary>
    public ConfigurableAcquisitionDriverModule()
        : this(isAvailable: true, diagnostic: null)
    {
    }

    /// <summary>创建Module并固定健康结论，用于确定性覆盖"插件不可用"这条路径。</summary>
    /// <param name="isAvailable">是否报告可用。</param>
    /// <param name="diagnostic">不可用原因；为空时使用默认原因。</param>
    public ConfigurableAcquisitionDriverModule(bool isAvailable, string? diagnostic)
    {
        _isAvailable = isAvailable;
        _diagnostic = diagnostic;
    }

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
            "测试面阵相机",
            new VisionAcquisitionTypeCapabilities(
                SupportsFreeRun: true,
                SupportsSoftwareTrigger: true,
                SupportsExternalTrigger: true,
                SupportsCompleteFrameCallback: true),
            () => FakeVisionProvider.WithDevices(ProviderIdentity),
            TestDeviceSettingsParser.Parse));

        builder.Register(new VisionAcquisitionTypeRegistration(
            LineScanTestTypeId,
            PluginIdentity,
            TypeVersion,
            EVisionAcquisitionKind.LineScan,
            DeviceSettingsVersion,
            "测试线扫（允许测试Type）",
            new VisionAcquisitionTypeCapabilities(
                SupportsFreeRun: true,
                SupportsExternalTrigger: true,
                SupportsCompleteFrameCallback: true),
            () => FakeVisionProvider.WithDevices(ProviderIdentity),
            TestDeviceSettingsParser.Parse));
    }

    /// <inheritdoc/>
    public bool TryGetHealth(out string? diagnostic)
    {
        diagnostic = _isAvailable
            ? null
            : _diagnostic ?? $"插件 {PluginIdentity} 在测试中被标记为不可用。";
        return _isAvailable;
    }
}
