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

            foreach (var registration in candidate.Registrations)
            {
                var descriptor = ValidateAndBuild(registration);
                if (!typeIds.Add(descriptor.AcquisitionTypeId))
                    throw new VisionSourceConfigurationException(
                        $"AcquisitionTypeId 重复：{descriptor.AcquisitionTypeId}；同一进程不能冻结两个同身份Type。");
                descriptors.Add(descriptor);
                manifest.Add(FormatManifestLine(descriptor));
            }
        }

        var ordered = descriptors
            .OrderBy(item => item.AcquisitionTypeId, StringComparer.Ordinal)
            .ToArray();
        var orderedManifest = manifest.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return new VisionAcquisitionTypeCatalog(
            ComputeCatalogId(orderedManifest),
            ordered,
            orderedManifest);
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
