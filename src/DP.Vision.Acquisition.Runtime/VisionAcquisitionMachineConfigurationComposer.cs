using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Acquisition;

/// <summary>
/// 机器相机配置组合器：把已冻结的AcquisitionType Catalog与版本化CameraDefinition投影为不可变Composition。
/// 一份机器配置生成唯一Composition和SourceCatalog；Plugin验证deviceSettings后生成内部Binding与规范ResourceKey，
/// 私有配置摘要进入CompositionId。
/// </summary>
public sealed class VisionAcquisitionMachineConfigurationComposer
{
    /// <summary>组合机器相机配置。</summary>
    /// <param name="catalog">已冻结的AcquisitionType Catalog；机器配置不负责加载DLL。</param>
    /// <param name="cameras">版本化机器相机定义。</param>
    /// <returns>一次发布的不可变组合；未安装Type的Source被保真保留并标记为不可用，不阻断组合。</returns>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    /// <exception cref="VisionSourceConfigurationException">设置版本不一致、策略与缓冲策略不匹配、Plugin拒绝解析deviceSettings或绑定冲突。</exception>
    public VisionAcquisitionProviderComposition Compose(
        VisionAcquisitionTypeCatalog catalog,
        IEnumerable<VisionAcquisitionCameraDefinition> cameras)
    {
        if (catalog is null)
            throw new ArgumentNullException(nameof(catalog));
        if (cameras is null)
            throw new ArgumentNullException(nameof(cameras));

        var bindings = new List<VisionAcquisitionSourceBinding>();
        var registrations = new List<VisionAcquisitionProviderRegistration>();
        var registrationIds = new HashSet<string>(StringComparer.Ordinal);
        var sourceInfos = new Dictionary<string, VisionAcquisitionSourceInfo>(StringComparer.Ordinal);
        var configurationSummaries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var camera in cameras)
        {
            if (camera is null)
                throw new VisionSourceConfigurationException("相机定义列表不能包含空项。");

            var sourceId = camera.SourceId;
            var typeId = camera.AcquisitionTypeId;
            if (!catalog.TryGetType(typeId, out var descriptor) || descriptor is null)
            {
                // 未安装Type时机器配置保真：Source保留并标记不可用，由后续诊断解释原因（V2验收§21.3）。
                sourceInfos.Add(sourceId, new VisionAcquisitionSourceInfo(
                    sourceId,
                    typeId,
                    typeId,
                    string.Empty,
                    Kind: null,
                    DeriveSharingPolicy(camera),
                    DeriveAcquisitionMode(camera),
                    IsAvailable: false,
                    $"AcquisitionType {typeId} 未安装：当前Catalog不包含该类型，请检查插件部署。"));
                continue;
            }

            if (camera.SettingsVersion != descriptor.DeviceSettingsVersion)
                throw new VisionSourceConfigurationException(
                    $"逻辑源 {sourceId} 的 settingsVersion {camera.SettingsVersion} "
                    + $"与 {typeId} 声明的配置版本 {descriptor.DeviceSettingsVersion} 不一致；请更新机器配置或重新部署Plugin。");

            var binding = BuildBinding(camera, descriptor);
            bindings.Add(binding.Binding);
            configurationSummaries.Add(sourceId, binding.ConfigurationSummary);
            sourceInfos.Add(sourceId, new VisionAcquisitionSourceInfo(
                sourceId,
                typeId,
                typeId,
                binding.Binding.ResourceKey,
                descriptor.Kind,
                binding.Binding.SharingPolicy,
                binding.Binding.AcquisitionMode,
                IsAvailable: true,
                Diagnostic: null));

            // 同一Type的多个Source共享同一Provider注册；Provider身份在V2机器组合中等价于AcquisitionTypeId。
            if (registrationIds.Add(typeId))
                registrations.Add(new VisionAcquisitionProviderRegistration(
                    typeId,
                    descriptor.Version,
                    descriptor.Factory)
                {
                    // Type的显示名是操作员可读的Provider名；组合把它带进只读注册清单，界面不必再反查Catalog。
                    DisplayName = descriptor.DisplayName,
                });
        }

        return new VisionAcquisitionProviderComposer().Compose(
            registrations,
            bindings,
            sourceInfos,
            configurationSummaries);
    }

    private static ValidatedBinding BuildBinding(
        VisionAcquisitionCameraDefinition camera,
        VisionAcquisitionTypeDescriptor descriptor)
    {
        var transferStart = camera.Connection.TransferStart;
        if (transferStart == EVisionAcquisitionTransferStart.OnConnect
            && !descriptor.Capabilities.SupportsCompleteFrameCallback)
        {
            throw new VisionSourceConfigurationException(
                $"逻辑源 {camera.SourceId} 使用 OnConnect 取流，但 {descriptor.AcquisitionTypeId} 未声明完整帧回调能力；"
                + "外部触发Source必须使用OnConnect流，V2不允许只触发不布防。");
        }

        if (camera.DeviceSettingsJson is null)
            throw new VisionSourceConfigurationException(
                $"逻辑源 {camera.SourceId} 缺少 deviceSettings；无法生成设备绑定。");

        var parseResult = descriptor.DeviceSettingsParser(camera.DeviceSettingsJson);
        if (parseResult is null)
            throw new VisionSourceConfigurationException(
                $"逻辑源 {camera.SourceId} 的 {descriptor.AcquisitionTypeId} 未返回设备绑定解析结果。");

        var inbox = transferStart == EVisionAcquisitionTransferStart.OnConnect ? camera.Inbox : null;
        var binding = new VisionAcquisitionSourceBinding(
            camera.SourceId,
            descriptor.AcquisitionTypeId,
            parseResult.ProviderBindingId,
            parseResult.ResourceKey,
            DeriveSharingPolicy(camera),
            DeriveAcquisitionMode(camera),
            inbox,
            camera.IsRequired,
            // 插件私有绑定随公共绑定一起发布，打开设备时原样交回同一个插件；
            // 这是"deviceSettings 是设备配置唯一来源"的接线点。
            parseResult.ProviderState);
        return new ValidatedBinding(binding, parseResult.ConfigurationSummary);
    }

    private static EVisionSourceSharingPolicy DeriveSharingPolicy(VisionAcquisitionCameraDefinition camera) =>
        camera.Connection.TransferStart == EVisionAcquisitionTransferStart.OnConnect
            ? EVisionSourceSharingPolicy.ExclusiveRun
            : EVisionSourceSharingPolicy.ExclusiveOperation;

    private static EVisionAcquisitionMode DeriveAcquisitionMode(VisionAcquisitionCameraDefinition camera) =>
        camera.Connection.TransferStart == EVisionAcquisitionTransferStart.OnConnect
            ? EVisionAcquisitionMode.BufferedExternal
            : EVisionAcquisitionMode.OnDemand;

    /// <summary>内部绑定快照：公共Source绑定与其配置摘要成对出现。</summary>
    private sealed record ValidatedBinding(
        VisionAcquisitionSourceBinding Binding,
        string ConfigurationSummary);
}
