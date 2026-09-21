using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// Basler采集Provider插件Module。只向候选Builder提交Provider工厂和私有设备绑定，
/// 不接触当前正式Provider目录；贡献失败时调用方持有的正式组合保持不变。
/// </summary>
public sealed class BaslerAcquisitionProviderModule : IVisionAcquisitionProviderModule
{
    /// <summary>Module稳定身份。</summary>
    public const string ModuleIdentity = "dp.vision.basler.acquisition";

    /// <summary>Provider实现版本；进入组合清单，供运行制品记录本次实际使用的实现。</summary>
    public const string ProviderVersion = "1.0.0";

    private readonly IReadOnlyList<BaslerAcquisitionBinding> _bindings;

    /// <summary>创建Module。</summary>
    /// <param name="bindings">Provider私有设备绑定；为空表示本Module尚未配置任何设备。</param>
    /// <exception cref="ArgumentException">绑定列表含空项或绑定身份重复。</exception>
    public BaslerAcquisitionProviderModule(IEnumerable<BaslerAcquisitionBinding>? bindings = null)
    {
        var candidates = bindings is null ? Array.Empty<BaslerAcquisitionBinding>() : bindings.ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in candidates)
        {
            if (binding is null)
                throw new ArgumentException("绑定列表不能包含空项。", nameof(bindings));
            if (!identities.Add(binding.BindingId))
                throw new ArgumentException($"Basler Provider 绑定身份重复：{binding.BindingId}。", nameof(bindings));
        }

        _bindings = candidates;
    }

    /// <summary>本Module提供的Provider私有设备绑定；公共配置只引用其绑定身份。</summary>
    public IReadOnlyList<BaslerAcquisitionBinding> Bindings => _bindings;

    /// <inheritdoc/>
    public string ExtensionId => ModuleIdentity;

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">候选Builder为空。</exception>
    public void Contribute(IVisionAcquisitionProviderContributionBuilder builder)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));
        builder.Register(new VisionAcquisitionProviderRegistration(
            BaslerAcquisitionProvider.ProviderIdentity,
            ProviderVersion,
            () => new BaslerAcquisitionProvider(_bindings)));
    }
}
