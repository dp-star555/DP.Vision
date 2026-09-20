using System;

namespace DP.Vision.Acquisition;

/// <summary>Provider插件Module；只提交候选工厂和元数据，不接触当前正式Provider目录。</summary>
public interface IVisionAcquisitionProviderModule
{
    /// <summary>Module稳定身份，与ProviderId一起用于唯一性校验。</summary>
    string ExtensionId { get; }

    /// <summary>向候选贡献Builder提交本Module的Provider注册。</summary>
    /// <param name="builder">本次组合的私有候选Builder。</param>
    void Contribute(IVisionAcquisitionProviderContributionBuilder builder);
}

/// <summary>候选贡献入口；只在一次候选组合期间有效。</summary>
public interface IVisionAcquisitionProviderContributionBuilder
{
    /// <summary>注册一个Provider候选。</summary>
    /// <param name="registration">Provider注册信息。</param>
    void Register(VisionAcquisitionProviderRegistration registration);
}

/// <summary>Provider候选注册；工厂在组合发布后按需创建Provider实例。</summary>
/// <param name="ProviderId">Provider稳定身份。</param>
/// <param name="Version">Provider实现版本。</param>
/// <param name="Factory">Provider工厂；每次调用返回一个独立实例。</param>
public sealed record VisionAcquisitionProviderRegistration(
    string ProviderId,
    string Version,
    Func<IVisionAcquisitionProvider> Factory);
