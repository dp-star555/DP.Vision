using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DP.Vision.Acquisition;

/// <summary>Provider组合器；采用"候选贡献 → 完整验证 → 一次发布"原则，任一Module失败都不留下半个正式组合。</summary>
public sealed class VisionAcquisitionProviderComposer
{
    /// <summary>组合Provider与Source绑定。</summary>
    /// <param name="modules">本次候选组合的Provider Module集合。</param>
    /// <param name="sources">机器级公共Source绑定。</param>
    /// <returns>验证通过后一次发布的不可变组合。</returns>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    /// <exception cref="VisionSourceConfigurationException">Module身份或Provider身份重复、注册非法、Source绑定不完整或不一致。</exception>
    public VisionAcquisitionProviderComposition Compose(
        IEnumerable<IVisionAcquisitionProviderModule> modules,
        IEnumerable<VisionAcquisitionSourceBinding> sources)
    {
        if (modules is null)
            throw new ArgumentNullException(nameof(modules));
        if (sources is null)
            throw new ArgumentNullException(nameof(sources));

        var providers = new Dictionary<string, VisionAcquisitionProviderRegistration>(StringComparer.Ordinal);
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);
        var manifest = new List<string>();

        foreach (var module in modules)
        {
            if (module is null)
                throw new ArgumentException("Module列表不能包含空项。", nameof(modules));
            if (string.IsNullOrWhiteSpace(module.ExtensionId))
                throw new VisionSourceConfigurationException("Provider Module 身份不能为空。");
            if (!moduleIds.Add(module.ExtensionId))
                throw new VisionSourceConfigurationException(
                    $"Provider Module 身份重复：{module.ExtensionId}；同一进程不能组合两个同身份Module。");

            var candidate = new CandidateBuilder();
            // 贡献阶段失败直接抛出，调用方持有的正式组合保持不变。
            module.Contribute(candidate);

            foreach (var registration in candidate.Registrations)
            {
                Validate(registration);
                if (providers.ContainsKey(registration.ProviderId))
                    throw new VisionSourceConfigurationException(
                        $"Provider身份重复：{registration.ProviderId}；同一进程不能组合两个同身份Provider。");
                providers.Add(registration.ProviderId, registration);
                manifest.Add(registration.ProviderId + "@" + registration.Version);
            }
        }

        var bindings = sources.ToArray();
        ValidateBindings(bindings, providers);

        var orderedManifest = manifest.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return new VisionAcquisitionProviderComposition(
            ComputeCompositionId(orderedManifest, bindings),
            providers.Values,
            bindings,
            orderedManifest);
    }

    private static void Validate(VisionAcquisitionProviderRegistration registration)
    {
        if (registration is null)
            throw new VisionSourceConfigurationException("Provider注册不能为空。");
        if (string.IsNullOrWhiteSpace(registration.ProviderId))
            throw new VisionSourceConfigurationException("Provider身份不能为空。");
        if (string.IsNullOrWhiteSpace(registration.Version))
            throw new VisionSourceConfigurationException($"Provider {registration.ProviderId} 缺少版本。");
        if (registration.Factory is null)
            throw new VisionSourceConfigurationException($"Provider {registration.ProviderId} 缺少工厂。");
    }

    private static void ValidateBindings(
        IReadOnlyList<VisionAcquisitionSourceBinding> bindings,
        IDictionary<string, VisionAcquisitionProviderRegistration> providers)
    {
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var byResourceKey = new Dictionary<string, VisionAcquisitionSourceBinding>(StringComparer.Ordinal);
        var bufferedByResourceKey = new Dictionary<string, VisionAcquisitionSourceBinding>(StringComparer.Ordinal);

        foreach (var binding in bindings)
        {
            if (binding is null)
                throw new VisionSourceConfigurationException("Source绑定列表不能包含空项。");
            if (!sourceIds.Add(binding.SourceId))
                throw new VisionSourceConfigurationException($"逻辑源身份重复：{binding.SourceId}。");
            if (!providers.ContainsKey(binding.ProviderId))
                throw new VisionSourceConfigurationException(
                    $"逻辑源 {binding.SourceId} 绑定的Provider {binding.ProviderId} 不在本次组合中；拒绝发布。");

            ValidateAcquisitionMode(binding, bufferedByResourceKey);

            if (!byResourceKey.TryGetValue(binding.ResourceKey, out var existing))
            {
                byResourceKey.Add(binding.ResourceKey, binding);
                continue;
            }

            // 同一物理设备必须落在同一锁域，否则会形成两个互不相知的互斥状态。
            if (!string.Equals(existing.ProviderId, binding.ProviderId, StringComparison.Ordinal)
                || !string.Equals(existing.ProviderBindingId, binding.ProviderBindingId, StringComparison.Ordinal))
                throw new VisionSourceConfigurationException(
                    $"资源键 {binding.ResourceKey} 被 {existing.SourceId} 与 {binding.SourceId} 同时映射，但Provider或设备绑定不一致；拒绝发布。");

            // 同一资源键混用"立即失败"与"排队等待"会让冲突行为不可预测，因此要求策略一致。
            if (existing.SharingPolicy != binding.SharingPolicy)
                throw new VisionSourceConfigurationException(
                    $"资源键 {binding.ResourceKey} 上的共享策略不一致（{existing.SharingPolicy} 与 {binding.SharingPolicy}）；拒绝发布。");

            // 同一资源键混用两种采集模式会让"帧从哪来"不可判定，因此要求模式一致。
            if (existing.AcquisitionMode != binding.AcquisitionMode)
                throw new VisionSourceConfigurationException(
                    $"资源键 {binding.ResourceKey} 上的采集模式不一致（{existing.AcquisitionMode} 与 {binding.AcquisitionMode}）；"
                    + "同一物理相机不能同时被当成主动采集源和外部回调缓冲源。拒绝发布。");
        }
    }

    /// <summary>
    /// 外部回调缓冲在组合期可判定的约束。
    /// <para>
    /// 另有四项只能在打开设备或读取Provider私有配置之后判定，不在本方法职责内，也不应在此伪造结论：
    /// Provider绑定存在、设备声明流式能力、触发配置为External、设备规范身份与资源键一致。
    /// 它们由运行期布防阶段负责，失败必须带ProviderId诊断。
    /// </para>
    /// </summary>
    private static void ValidateAcquisitionMode(
        VisionAcquisitionSourceBinding binding,
        IDictionary<string, VisionAcquisitionSourceBinding> bufferedByResourceKey)
    {
        if (binding.AcquisitionMode != EVisionAcquisitionMode.BufferedExternal)
            return;

        // V1：一台物理相机只有一个接收队列，因此一个资源键只能有一个被动Source。
        if (bufferedByResourceKey.TryGetValue(binding.ResourceKey, out var existing))            throw new VisionSourceConfigurationException(
                $"资源键 {binding.ResourceKey} 上同时发布了两个外部回调缓冲Source（{existing.SourceId} 与 {binding.SourceId}）；"
                + "V1只支持一台物理相机对应一个逻辑源，不支持用多个SourceId形成多个独立队列。拒绝发布。");

        // 被动Source允许帧先于节点到达，必须由根运行独占，否则无法界定"哪根运行的帧"。
        if (binding.SharingPolicy != EVisionSourceSharingPolicy.ExclusiveRun)
            throw new VisionSourceConfigurationException(
                $"外部回调缓冲Source {binding.SourceId} 的共享策略是 {binding.SharingPolicy}，必须是 ExclusiveRun；"
                + "否则旧运行的帧可能被下一根运行领取。拒绝发布。");

        bufferedByResourceKey.Add(binding.ResourceKey, binding);
    }

    private static string ComputeCompositionId(
        IReadOnlyList<string> manifest,
        IReadOnlyList<VisionAcquisitionSourceBinding> bindings)
    {
        var builder = new StringBuilder();
        foreach (var item in manifest)
            builder.Append(item).Append('\n');
        foreach (var binding in bindings.OrderBy(item => item.SourceId, StringComparer.Ordinal))
        {
            builder.Append(binding.SourceId).Append('|')
                .Append(binding.ProviderId).Append('|')
                .Append(binding.ProviderBindingId).Append('|')
                .Append(binding.ResourceKey).Append('|')
                .Append((int)binding.SharingPolicy).Append('|')
                .Append((int)binding.AcquisitionMode).Append('|')
                // 队列策略直接决定"能收多少、留多久"，必须进组合身份；否则改容量不会产生新身份。
                .Append(binding.InboxPolicy is null
                    ? "-"
                    : string.Join(",",
                        binding.InboxPolicy.Capacity.ToString(CultureInfo.InvariantCulture),
                        binding.InboxPolicy.ByteBudget.ToString(CultureInfo.InvariantCulture),
                        binding.InboxPolicy.MaximumFrameAge.Ticks.ToString(CultureInfo.InvariantCulture)))
                .Append('\n');
        }

        using (var algorithm = SHA256.Create())
        {
            var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var text = new StringBuilder();
            for (int index = 0; index < 8; index++)
                text.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    private sealed class CandidateBuilder : IVisionAcquisitionProviderContributionBuilder
    {
        private readonly List<VisionAcquisitionProviderRegistration> _registrations =
            new List<VisionAcquisitionProviderRegistration>();

        public IReadOnlyList<VisionAcquisitionProviderRegistration> Registrations => _registrations;

        public void Register(VisionAcquisitionProviderRegistration registration)
        {
            if (registration is null)
                throw new VisionSourceConfigurationException("Provider注册不能为空。");
            _registrations.Add(registration);
        }
    }
}
