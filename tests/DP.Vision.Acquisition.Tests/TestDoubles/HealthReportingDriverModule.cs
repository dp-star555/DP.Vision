using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 可配置插件身份与健康结论的Driver Module，用于确定性地覆盖"插件已安装但底层运行时不可用"。
/// </summary>
/// <remarks>
/// <see cref="ConfigurableAcquisitionDriverModule"/> 的身份与TypeId是固定常量（别的测试依赖它们），
/// 而"同一PluginId被多个Module声明"和"一个插件不可用不影响另一个插件"这两条需要可变的身份，
/// 因此单独提供本替身。它始终实现健康报告接口——"不实现健康报告"那条路径由
/// <see cref="FakeAcquisitionDriverModule"/> 覆盖。
/// </remarks>
internal sealed class HealthReportingDriverModule : IVisionAcquisitionDriverModule, IVisionAcquisitionDriverModuleHealth
{
    private readonly string? _diagnostic;

    /// <summary>创建Module。</summary>
    /// <param name="extensionId">Module稳定身份。</param>
    /// <param name="pluginId">本Module声明的插件身份。</param>
    /// <param name="typeId">本Module贡献的Type身份。</param>
    /// <param name="isAvailable">健康结论。</param>
    /// <param name="diagnostic">不可用原因；为空时使用默认原因。</param>
    public HealthReportingDriverModule(
        string extensionId,
        string pluginId,
        string typeId,
        bool isAvailable,
        string? diagnostic = null)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new ArgumentException("Module身份不能为空。", nameof(extensionId));
        ExtensionId = extensionId.Trim();
        PluginId = pluginId;
        TypeId = typeId;
        IsAvailable = isAvailable;
        _diagnostic = diagnostic;
    }

    /// <summary>Module稳定身份。</summary>
    public string ExtensionId { get; }

    /// <summary>本Module声明的插件身份。</summary>
    public string PluginId { get; }

    /// <summary>本Module贡献的Type身份。</summary>
    public string TypeId { get; }

    /// <summary>本Module报告的健康结论。</summary>
    public bool IsAvailable { get; }

    /// <inheritdoc/>
    public void Contribute(IVisionAcquisitionTypeContributionBuilder builder)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));

        builder.Register(new VisionAcquisitionTypeRegistration(
            TypeId,
            PluginId,
            "1.0.0",
            EVisionAcquisitionKind.AreaScan,
            1,
            "健康报告替身相机",
            new VisionAcquisitionTypeCapabilities(SupportsFreeRun: true, SupportsSoftwareTrigger: true),
            () => FakeVisionProvider.WithDevices(PluginId + ".provider"),
            TestDeviceSettingsParser.Parse));
    }

    /// <inheritdoc/>
    public bool TryGetHealth(out string? diagnostic)
    {
        diagnostic = IsAvailable ? null : _diagnostic ?? $"插件 {PluginId} 在测试中被标记为不可用。";
        return IsAvailable;
    }
}
