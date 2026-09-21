using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>HALCON采集Provider；按绑定创建持有唯一 <c>HFramegrabber</c> 的设备适配器，公共契约不暴露任何 HALCON 类型。</summary>
public sealed class HalconAcquisitionProvider : IVisionAcquisitionProvider
{
    /// <summary>HALCON Provider稳定身份。</summary>
    public const string ProviderIdentity = "dp.vision.halcon";

    private readonly Dictionary<string, HalconAcquisitionBinding> _bindings =
        new Dictionary<string, HalconAcquisitionBinding>(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>创建Provider。</summary>
    /// <param name="bindings">Provider私有设备绑定；为空表示尚未配置任何设备。</param>
    /// <exception cref="ArgumentException">绑定列表含空项或绑定身份重复。</exception>
    public HalconAcquisitionProvider(IEnumerable<HalconAcquisitionBinding>? bindings = null)
    {
        if (bindings is null)
            return;
        foreach (var binding in bindings)
        {
            if (binding is null)
                throw new ArgumentException("绑定列表不能包含空项。", nameof(bindings));
            if (_bindings.ContainsKey(binding.BindingId))
                throw new ArgumentException($"Provider绑定身份重复：{binding.BindingId}。", nameof(bindings));
            _bindings.Add(binding.BindingId, binding);
        }
    }

    /// <inheritdoc/>
    public string ProviderId => ProviderIdentity;

    /// <inheritdoc/>
    public ValueTask<IVisionAcquisitionDevice> OpenAsync(
        string providerBindingId,
        CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HalconAcquisitionProvider));
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(providerBindingId))
            throw new VisionSourceConfigurationException("HALCON Provider 绑定身份不能为空。");
        if (!_bindings.TryGetValue(providerBindingId, out var binding))
            throw new VisionSourceConfigurationException(
                $"HALCON Provider 私有配置中没有绑定 {providerBindingId}；请先发布该设备的私有配置。");
        return new ValueTask<IVisionAcquisitionDevice>(new HalconAcquisitionDevice(binding));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return default;
    }
}
