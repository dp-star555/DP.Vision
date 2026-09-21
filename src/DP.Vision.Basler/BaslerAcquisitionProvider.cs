using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>Basler pylon 采集Provider；把 pylon 设备包装为公共Provider契约。</summary>
public sealed class BaslerAcquisitionProvider : IVisionAcquisitionProvider
{
    /// <summary>Basler Provider稳定身份。</summary>
    public const string ProviderIdentity = "dp.vision.basler";

    private readonly Dictionary<string, BaslerAcquisitionBinding> _bindings =
        new Dictionary<string, BaslerAcquisitionBinding>(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>创建Provider。</summary>
    /// <param name="bindings">Provider私有设备绑定；为空表示尚未配置任何设备。</param>
    /// <exception cref="ArgumentException">绑定列表含空项或绑定身份重复。</exception>
    public BaslerAcquisitionProvider(IEnumerable<BaslerAcquisitionBinding>? bindings = null)
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
    /// <exception cref="VisionSourceConfigurationException">绑定身份为空或未在私有配置中发布。</exception>
    public ValueTask<IVisionAcquisitionDevice> OpenAsync(
        string providerBindingId,
        CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BaslerAcquisitionProvider));
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(providerBindingId))
            throw new VisionSourceConfigurationException("Basler Provider 绑定身份不能为空。");
        if (!_bindings.TryGetValue(providerBindingId, out var binding))
        {
            throw new VisionSourceConfigurationException(
                $"Basler Provider 私有配置中没有绑定 {providerBindingId}；请先发布该设备的私有配置。");
        }

        return new ValueTask<IVisionAcquisitionDevice>(new BaslerAcquisitionDevice(binding));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return default;
    }
}
