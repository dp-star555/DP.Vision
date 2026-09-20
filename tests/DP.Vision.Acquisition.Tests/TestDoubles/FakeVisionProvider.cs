using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>确定性内存Provider；记录打开过的绑定身份，可注入打开失败。</summary>
internal sealed class FakeVisionProvider : IVisionAcquisitionProvider
{
    private readonly Func<string, IVisionAcquisitionDevice> _deviceFactory;
    private readonly List<string> _opened = new List<string>();
    private readonly object _gate = new object();
    private int _disposeCount;

    /// <summary>创建Provider。</summary>
    /// <param name="providerId">Provider稳定身份。</param>
    /// <param name="deviceFactory">按绑定身份创建设备。</param>
    public FakeVisionProvider(string providerId, Func<string, IVisionAcquisitionDevice> deviceFactory)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider身份不能为空。", nameof(providerId));
        ProviderId = providerId;
        _deviceFactory = deviceFactory ?? throw new ArgumentNullException(nameof(deviceFactory));
    }

    /// <inheritdoc/>
    public string ProviderId { get; }

    /// <summary>已请求打开的绑定身份，按调用顺序。</summary>
    public IReadOnlyList<string> OpenedBindings
    {
        get { lock (_gate) return _opened.ToArray(); }
    }

    /// <summary>已执行的释放次数。</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>创建每次打开都返回确定性设备的Provider。</summary>
    /// <param name="providerId">Provider稳定身份。</param>
    /// <param name="capture">可选采集行为。</param>
    /// <returns>内存Provider。</returns>
    public static FakeVisionProvider WithDevices(
        string providerId,
        Func<VisionCaptureRequest, CancellationToken, ValueTask<VisionProviderFrame>>? capture = null)
    {
        return new FakeVisionProvider(
            providerId,
            binding => new FakeVisionDevice(new VisionDeviceIdentity(providerId, binding), capture));
    }

    /// <inheritdoc/>
    public ValueTask<IVisionAcquisitionDevice> OpenAsync(
        string providerBindingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _opened.Add(providerBindingId);
        return new ValueTask<IVisionAcquisitionDevice>(_deviceFactory(providerBindingId));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return default;
    }
}
