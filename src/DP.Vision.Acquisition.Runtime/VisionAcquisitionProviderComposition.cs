using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Acquisition;

/// <summary>一次完整Provider组合的不可变快照；发布后不再接受任何变更，已开始的运行持有自己的快照。</summary>
public sealed class VisionAcquisitionProviderComposition
{
    private readonly Dictionary<string, VisionAcquisitionProviderRegistration> _providers;
    private readonly Dictionary<string, VisionAcquisitionSourceBinding> _sources;

    internal VisionAcquisitionProviderComposition(
        string compositionId,
        IEnumerable<VisionAcquisitionProviderRegistration> providers,
        IEnumerable<VisionAcquisitionSourceBinding> sources,
        IEnumerable<string> providerManifest)
    {
        CompositionId = compositionId;
        _providers = providers.ToDictionary(item => item.ProviderId, StringComparer.Ordinal);
        _sources = sources.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        ProviderManifest = providerManifest.ToArray();
    }

    /// <summary>组合的内容身份；Provider与Source清单变化会产生新的组合身份。</summary>
    public string CompositionId { get; }

    /// <summary>Provider与Source版本清单，按ProviderId排序；可导出用于运行制品。</summary>
    public IReadOnlyList<string> ProviderManifest { get; }

    /// <summary>全部已发布的Source绑定，按SourceId排序。</summary>
    public IReadOnlyList<VisionAcquisitionSourceBinding> Sources =>
        _sources.Values.OrderBy(binding => binding.SourceId, StringComparer.Ordinal).ToArray();

    /// <summary>按逻辑源查找绑定。</summary>
    /// <param name="sourceId">逻辑源标识。</param>
    /// <param name="binding">找到的绑定。</param>
    /// <returns>已发布时返回true。</returns>
    public bool TryGetSource(string sourceId, out VisionAcquisitionSourceBinding? binding)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            binding = null;
            return false;
        }

        return _sources.TryGetValue(sourceId, out binding);
    }

    /// <summary>按Provider身份查找注册。</summary>
    /// <param name="providerId">Provider稳定身份。</param>
    /// <param name="registration">找到的注册。</param>
    /// <returns>已发布时返回true。</returns>
    public bool TryGetProvider(string providerId, out VisionAcquisitionProviderRegistration? registration)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            registration = null;
            return false;
        }

        return _providers.TryGetValue(providerId, out registration);
    }
}
