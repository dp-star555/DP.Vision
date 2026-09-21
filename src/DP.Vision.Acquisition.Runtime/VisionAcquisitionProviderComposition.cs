using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Acquisition;

/// <summary>一次完整Provider组合的不可变快照；发布后不再接受任何变更，已开始的运行持有自己的快照。</summary>
public sealed class VisionAcquisitionProviderComposition : IVisionAcquisitionSourceCatalog
{
    private readonly Dictionary<string, VisionAcquisitionProviderRegistration> _providers;
    private readonly Dictionary<string, VisionAcquisitionSourceBinding> _sources;
    private readonly Dictionary<string, VisionAcquisitionSourceInfo> _sourceInfos;
    private readonly Dictionary<string, string> _configurationSummaries;

    internal VisionAcquisitionProviderComposition(
        string compositionId,
        IEnumerable<VisionAcquisitionProviderRegistration> providers,
        IEnumerable<VisionAcquisitionSourceBinding> sources,
        IEnumerable<string> providerManifest,
        IReadOnlyDictionary<string, VisionAcquisitionSourceInfo>? sourceInfos,
        IReadOnlyDictionary<string, string>? configurationSummaries)
    {
        CompositionId = compositionId;
        _providers = providers.ToDictionary(item => item.ProviderId, StringComparer.Ordinal);
        _sources = sources.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        _sourceInfos = sourceInfos is null
            ? _sources.ToDictionary(
                item => item.Key,
                item => ToSourceInfo(item.Value),
                StringComparer.Ordinal)
            : sourceInfos.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);
        _configurationSummaries = configurationSummaries is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : configurationSummaries.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);
        ProviderManifest = providerManifest.ToArray();
    }

    /// <summary>组合的内容身份；Provider与Source清单及私有配置摘要变化会产生新的组合身份。</summary>
    public string CompositionId { get; }

    /// <summary>Provider与Source版本清单，按ProviderId排序；可导出用于运行制品。</summary>
    public IReadOnlyList<string> ProviderManifest { get; }

    /// <summary>
    /// 已注册Provider的只读清单，按ProviderId排序；身份、版本与显示名可被界面与诊断直接读取。
    /// <para>
    /// 这是 <see cref="ProviderManifest"/> 的结构化形式：消费方不应再自行切分清单行。
    /// 清单按身份去重且顺序确定，与已发布Source无关——尚未配置任何Source的Provider同样在列。
    /// </para>
    /// </summary>
    public IReadOnlyList<VisionAcquisitionProviderRegistration> Providers =>
        _providers.Values.OrderBy(registration => registration.ProviderId, StringComparer.Ordinal).ToArray();

    /// <summary>全部已发布的Source绑定，按SourceId排序。</summary>
    public IReadOnlyList<VisionAcquisitionSourceBinding> Sources =>
        _sources.Values.OrderBy(binding => binding.SourceId, StringComparer.Ordinal).ToArray();

    /// <inheritdoc/>
    public IReadOnlyList<VisionAcquisitionSourceInfo> SourceCatalog =>
        _sourceInfos.Values.OrderBy(info => info.SourceId, StringComparer.Ordinal).ToArray();

    /// <inheritdoc/>
    public bool TryGetSourceEntry(string sourceId, out VisionAcquisitionSourceInfo? source)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            source = null;
            return false;
        }

        return _sourceInfos.TryGetValue(sourceId.Trim(), out source);
    }

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

    /// <summary>按逻辑源读取私有配置摘要；未发布或不可用源返回空。</summary>
    /// <param name="sourceId">逻辑源标识。</param>
    /// <returns>Plugin生成的确定性配置摘要；不存在时为空。</returns>
    public string? GetConfigurationSummary(string sourceId) =>
        string.IsNullOrWhiteSpace(sourceId) || !_configurationSummaries.TryGetValue(sourceId.Trim(), out var summary)
            ? null
            : summary;

    private static VisionAcquisitionSourceInfo ToSourceInfo(VisionAcquisitionSourceBinding binding) =>
        new VisionAcquisitionSourceInfo(
            binding.SourceId,
            binding.ProviderId,
            AcquisitionTypeId: null,
            binding.ResourceKey,
            Kind: null,
            binding.SharingPolicy,
            binding.AcquisitionMode,
            IsAvailable: true,
            Diagnostic: null);
}
