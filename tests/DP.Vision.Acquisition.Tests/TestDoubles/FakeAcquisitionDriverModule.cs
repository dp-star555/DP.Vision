using System;
using System.Collections.Generic;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>确定性内存Driver Module；可注入贡献中途失败，用于验证候选Catalog组合的原子性。</summary>
internal sealed class FakeAcquisitionDriverModule : IVisionAcquisitionDriverModule
{
    private readonly List<VisionAcquisitionTypeRegistration> _registrations =
        new List<VisionAcquisitionTypeRegistration>();

    /// <summary>创建Module。</summary>
    /// <param name="extensionId">Module稳定身份。</param>
    /// <param name="registrations">本Module贡献的AcquisitionType注册。</param>
    public FakeAcquisitionDriverModule(
        string extensionId,
        params VisionAcquisitionTypeRegistration[] registrations)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new ArgumentException("Module身份不能为空。", nameof(extensionId));
        ExtensionId = extensionId;
        if (registrations is not null)
            _registrations.AddRange(registrations);
    }

    /// <inheritdoc/>
    public string ExtensionId { get; }

    /// <summary>贡献时抛出的异常；用于验证"任一Module失败即丢弃候选"。</summary>
    public Exception? ContributeFailure { get; set; }

    /// <summary>已执行的贡献次数。</summary>
    public int ContributeCount { get; private set; }

    /// <inheritdoc/>
    public void Contribute(IVisionAcquisitionTypeContributionBuilder builder)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));
        ContributeCount++;
        foreach (var registration in _registrations)
            builder.Register(registration);
        if (ContributeFailure is not null)
            throw ContributeFailure;
    }
}
