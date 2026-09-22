using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON采集Provider插件Module。只向候选Builder提交Provider工厂，
/// 不接触当前正式Provider目录；贡献失败时调用方持有的正式组合保持不变。
/// </summary>
/// <remarks>
/// Module 不携带任何设备绑定：设备配置由机器配置的 <c>deviceSettings</c> 提供，
/// 经 <see cref="HalconDeviceSettingsParser"/> 解析后随公共绑定发布。
/// 这样同一台相机只有一处声明，不会出现"插件私有配置与机器配置各写一遍"的漂移。
/// </remarks>
public sealed class HalconAcquisitionProviderModule : IVisionAcquisitionProviderModule
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.halcon.acquisition";

    /// <summary>Provider实现版本；进入组合清单，供运行制品记录本次实际使用的实现。</summary>
    public const string ProviderVersion = "1.0.0";

    /// <inheritdoc/>
    public string ExtensionId => ModuleIdentity;

    /// <inheritdoc/>
    public void Contribute(IVisionAcquisitionProviderContributionBuilder builder)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));
        builder.Register(new VisionAcquisitionProviderRegistration(
            HalconAcquisitionProvider.ProviderIdentity,
            ProviderVersion,
            static () => new HalconAcquisitionProvider()));
    }
}
