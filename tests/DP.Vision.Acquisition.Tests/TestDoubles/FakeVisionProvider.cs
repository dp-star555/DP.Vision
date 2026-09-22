using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>确定性内存Provider；记录打开过的绑定，可注入打开失败，也可充当设备发现Provider。</summary>
internal sealed class FakeVisionProvider : IVisionAcquisitionProvider, IVisionDeviceDiscovery
{
    private readonly Func<string, IVisionAcquisitionDevice> _deviceFactory;
    private readonly List<string> _opened = new List<string>();
    private readonly List<VisionAcquisitionProviderBinding> _openRequests =
        new List<VisionAcquisitionProviderBinding>();
    private readonly object _gate = new object();
    private int _disposeCount;
    private int _discoveryCount;

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

    /// <summary>
    /// 已收到的完整打开绑定（含插件私有状态），按调用顺序。
    /// 用于证明 deviceSettings 解析出的私有绑定真的被带到了 <c>OpenAsync</c>，
    /// 而不是在中途被丢掉、只剩一个身份字符串。
    /// </summary>
    public IReadOnlyList<VisionAcquisitionProviderBinding> OpenRequests
    {
        get { lock (_gate) return _openRequests.ToArray(); }
    }

    /// <summary>已执行的释放次数。</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>发现返回的候选设备；默认为空，代表现场没有设备。</summary>
    public IReadOnlyList<VisionDeviceDescriptor> Devices { get; set; } =
        Array.Empty<VisionDeviceDescriptor>();

    /// <summary>发现时抛出的异常；用于验证"缺SDK的Provider不拖垮整体发现"。</summary>
    public Exception? DiscoveryFailure { get; set; }

    /// <summary>已执行的发现次数。</summary>
    public int DiscoveryCount => Volatile.Read(ref _discoveryCount);

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
        VisionAcquisitionProviderBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _opened.Add(binding.ProviderBindingId);
            _openRequests.Add(binding);
        }

        return new ValueTask<IVisionAcquisitionDevice>(_deviceFactory(binding.ProviderBindingId));
    }

    /// <inheritdoc/>
    /// <exception cref="VisionProviderUnavailableException">注入了发现失败。</exception>
    public ValueTask<IReadOnlyList<VisionDeviceDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _discoveryCount);
        if (DiscoveryFailure is not null)
            throw DiscoveryFailure;
        return new ValueTask<IReadOnlyList<VisionDeviceDescriptor>>(Devices);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return default;
    }
}
