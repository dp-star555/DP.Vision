using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DP.Vision.Acquisition;

/// <summary>
/// AcquisitionType组合器；采用"候选贡献 → 完整验证 → 一次Freeze"原则。
/// 任一Module的重复TypeId、空工厂、未知配置版本或不一致能力都会在Catalog冻结前失败，
/// 调用方持有的正式Catalog保持不变（参照WorkFlow AR-02"先校验后发布"）。
/// </summary>
public sealed class VisionAcquisitionTypeCatalogComposer
{
    /// <summary>组合并一次冻结AcquisitionType Catalog。</summary>
    /// <param name="modules">本次候选组合的Driver Module集合。</param>
    /// <returns>完整验证后一次发布的不可变Catalog。</returns>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    /// <exception cref="ArgumentException">Module列表包含空项。</exception>
    /// <exception cref="VisionSourceConfigurationException">Module身份重复、AcquisitionTypeId重复、注册非法或能力不一致。</exception>
    public VisionAcquisitionTypeCatalog Compose(IEnumerable<IVisionAcquisitionDriverModule> modules)
    {
        if (modules is null)
            throw new ArgumentNullException(nameof(modules));

        var descriptors = new List<VisionAcquisitionTypeDescriptor>();
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);
        var typeIds = new HashSet<string>(StringComparer.Ordinal);
        var manifest = new List<string>();
        var availability = new Dictionary<string, PluginHealth>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            if (module is null)
                throw new ArgumentException("Module列表不能包含空项。", nameof(modules));
            if (string.IsNullOrWhiteSpace(module.ExtensionId))
                throw new VisionSourceConfigurationException("Driver Module 身份不能为空。");
            if (!moduleIds.Add(module.ExtensionId))
                throw new VisionSourceConfigurationException(
                    $"Driver Module 身份重复：{module.ExtensionId}；同一进程不能组合两个同身份Module。");

            var candidate = new CandidateBuilder();
            // 贡献阶段失败直接抛出，调用方持有的正式Catalog保持不变。
            module.Contribute(candidate);

            // 健康报告每个Module只问一次：它可能去解析厂商原生库，不能按Type重复调用。
            // 未实现健康报告的Module视为可用——这是可选接口，不是"默认不可用"。
            string? moduleDiagnostic = null;
            var moduleAvailable = module is not IVisionAcquisitionDriverModuleHealth health
                || health.TryGetHealth(out moduleDiagnostic);
            var moduleDiagnosticText = moduleAvailable ? null : moduleDiagnostic;

            var modulePluginIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registration in candidate.Registrations)
            {
                var descriptor = ValidateAndBuild(registration);
                if (!typeIds.Add(descriptor.AcquisitionTypeId))
                    throw new VisionSourceConfigurationException(
                        $"AcquisitionTypeId 重复：{descriptor.AcquisitionTypeId}；同一进程不能冻结两个同身份Type。");
                descriptors.Add(descriptor);
                manifest.Add(FormatManifestLine(descriptor));
                modulePluginIds.Add(descriptor.PluginId);
            }

            // 一个Module贡献多个Type时，它的健康结论只累计一次；否则同一个原因会按Type重复堆积。
            foreach (var pluginId in modulePluginIds.OrderBy(id => id, StringComparer.Ordinal))
                Accumulate(availability, pluginId, moduleAvailable, moduleDiagnosticText);
        }

        var ordered = descriptors
            .OrderBy(item => item.AcquisitionTypeId, StringComparer.Ordinal)
            .ToArray();
        var orderedManifest = manifest.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var orderedAvailability = availability
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new VisionAcquisitionPluginAvailability(
                item.Key,
                item.Value.IsAvailable,
                item.Value.IsAvailable ? null : DescribeUnavailable(item.Key, item.Value)))
            .ToArray();
        return new VisionAcquisitionTypeCatalog(
            ComputeCatalogId(orderedManifest),
            ordered,
            orderedManifest,
            orderedAvailability);
    }

    /// <summary>
    /// 合并同一PluginId的多个Module健康结论：全部可用才算可用，诊断按序数排序后拼接，
    /// 使冻结结果与Module的传入顺序无关。
    /// </summary>
    private static void Accumulate(
        IDictionary<string, PluginHealth> availability,
        string pluginId,
        bool available,
        string? diagnostic)
    {
        if (!availability.TryGetValue(pluginId, out var health))
        {
            health = new PluginHealth();
            availability.Add(pluginId, health);
        }

        health.IsAvailable &= available;
        // netstandard2.0 的 BCL 没有 NotNullWhen，IsNullOrWhiteSpace 不参与可空流分析，
        // 因此这里显式判空再取值。
        if (diagnostic is not null && diagnostic.Trim().Length > 0)
            health.Diagnostics.Add(diagnostic.Trim());
    }

    /// <summary>拼出不可用诊断；Module报告不可用却没给原因时也要留下可读说明，不能变成空字符串。</summary>
    private static string DescribeUnavailable(string pluginId, PluginHealth health)
    {
        if (health.Diagnostics.Count == 0)
            return $"插件 {pluginId} 报告自身不可用，但没有给出原因。";

        return string.Join(
            "；",
            health.Diagnostics
                .Distinct(StringComparer.Ordinal)
                .OrderBy(text => text, StringComparer.Ordinal));
    }

    /// <summary>单个PluginId的可用性累计状态。</summary>
    private sealed class PluginHealth
    {
        /// <summary>该PluginId声明的全部Module是否都可用。</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>各Module给出的不可用原因。</summary>
        public List<string> Diagnostics { get; } = new List<string>();
    }

    private static VisionAcquisitionTypeDescriptor ValidateAndBuild(
        VisionAcquisitionTypeRegistration registration)
    {
        if (registration is null)
            throw new VisionSourceConfigurationException("AcquisitionType注册不能为空。");
        if (string.IsNullOrWhiteSpace(registration.AcquisitionTypeId))
            throw new VisionSourceConfigurationException("AcquisitionTypeId 不能为空。");
        if (string.IsNullOrWhiteSpace(registration.PluginId))
            throw new VisionSourceConfigurationException($"Type {registration.AcquisitionTypeId} 缺少 PluginId。");
        if (string.IsNullOrWhiteSpace(registration.Version))
            throw new VisionSourceConfigurationException($"Type {registration.AcquisitionTypeId} 缺少版本。");
        if (!Enum.IsDefined(typeof(EVisionAcquisitionKind), registration.Kind))
            throw new VisionSourceConfigurationException(
                $"Type {registration.AcquisitionTypeId} 声明了未知的采集形态 {registration.Kind}。");
        if (registration.DeviceSettingsVersion < 1)
            throw new VisionSourceConfigurationException(
                $"Type {registration.AcquisitionTypeId} 的配置版本 {registration.DeviceSettingsVersion} 无效；必须是正整数。");
        if (registration.Capabilities is null)
            throw new VisionSourceConfigurationException($"Type {registration.AcquisitionTypeId} 缺少能力声明。");
        if (!registration.Capabilities.HasAnyCapturePath)
            throw new VisionSourceConfigurationException(
                $"Type {registration.AcquisitionTypeId} 的能力声明不包含任何取图路径。");
        if (registration.Capabilities.SupportsExternalTrigger
            && !registration.Capabilities.SupportsCompleteFrameCallback)
            throw new VisionSourceConfigurationException(
                $"Type {registration.AcquisitionTypeId} 声明支持外部触发但没有完整帧回调；"
                + "外部触发Source必须使用OnConnect流，V2不允许只触发不布防。");
        if (registration.Factory is null)
            throw new VisionSourceConfigurationException(
                $"Type {registration.AcquisitionTypeId} 缺少设备Adapter工厂。");
        if (registration.DeviceSettingsParser is null)
            throw new VisionSourceConfigurationException(
                $"Type {registration.AcquisitionTypeId} 缺少设备配置解析器；机器配置无法解析其deviceSettings。");

        var displayName = string.IsNullOrWhiteSpace(registration.DisplayName)
            ? registration.AcquisitionTypeId
            : registration.DisplayName.Trim();
        return new VisionAcquisitionTypeDescriptor(
            registration.AcquisitionTypeId.Trim(),
            registration.PluginId.Trim(),
            registration.Version.Trim(),
            registration.Kind,
            registration.DeviceSettingsVersion,
            displayName,
            registration.Capabilities,
            registration.Factory,
            registration.DeviceSettingsParser);
    }

    private static string FormatManifestLine(VisionAcquisitionTypeDescriptor descriptor)
    {
        var flags = (descriptor.Capabilities.SupportsFreeRun ? 1 : 0)
            | (descriptor.Capabilities.SupportsSoftwareTrigger ? 2 : 0)
            | (descriptor.Capabilities.SupportsExternalTrigger ? 4 : 0)
            | (descriptor.Capabilities.SupportsCompleteFrameCallback ? 8 : 0);
        return descriptor.AcquisitionTypeId + "@" + descriptor.Version
            + "|" + (int)descriptor.Kind
            + "|" + descriptor.DeviceSettingsVersion.ToString(CultureInfo.InvariantCulture)
            + "|" + flags.ToString(CultureInfo.InvariantCulture);
    }

    private static string ComputeCatalogId(IReadOnlyList<string> manifest)
    {
        var builder = new StringBuilder();
        foreach (var item in manifest)
            builder.Append(item).Append('\n');

        using (var algorithm = SHA256.Create())
        {
            var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var text = new StringBuilder();
            for (int index = 0; index < 8; index++)
                text.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    private sealed class CandidateBuilder : IVisionAcquisitionTypeContributionBuilder
    {
        private readonly List<VisionAcquisitionTypeRegistration> _registrations =
            new List<VisionAcquisitionTypeRegistration>();

        public IReadOnlyList<VisionAcquisitionTypeRegistration> Registrations => _registrations;

        public void Register(VisionAcquisitionTypeRegistration registration)
        {
            if (registration is null)
                throw new VisionSourceConfigurationException("AcquisitionType注册不能为空。");
            _registrations.Add(registration);
        }
    }
}
