using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>记录被创建的Provider实例，便于断言"只调用了绑定的那个Provider"。</summary>
internal sealed class RecordingProviderFactory
{
    private readonly List<FakeVisionProvider> _created = new List<FakeVisionProvider>();

    /// <summary>已创建的Provider实例，按创建顺序。</summary>
    public IReadOnlyList<FakeVisionProvider> Created => _created;

    /// <summary>最近创建的Provider实例。</summary>
    public FakeVisionProvider Last => _created[_created.Count - 1];

    /// <summary>创建带记录能力的Provider注册。</summary>
    /// <param name="providerId">Provider稳定身份。</param>
    /// <param name="version">Provider版本。</param>
    /// <param name="capture">可选采集行为。</param>
    /// <param name="canonicalKey">可选设备规范身份，用于验证与配置资源键的一致性校验。</param>
    /// <returns>Provider注册。</returns>
    public VisionAcquisitionProviderRegistration Registration(
        string providerId,
        string version = "1.0.0",
        Func<VisionCaptureRequest, CancellationToken, ValueTask<VisionProviderFrame>>? capture = null,
        string? canonicalKey = null)
    {
        return new VisionAcquisitionProviderRegistration(providerId, version, () =>
        {
            var provider = canonicalKey is null
                ? FakeVisionProvider.WithDevices(providerId, capture)
                : new FakeVisionProvider(
                    providerId,
                    binding => new FakeVisionDevice(
                        new VisionDeviceIdentity(providerId, binding, canonicalKey),
                        capture));
            _created.Add(provider);
            return provider;
        });
    }
}
