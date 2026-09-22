using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>HALCON采集Provider；按绑定创建持有唯一 <c>HFramegrabber</c> 的设备适配器，公共契约不暴露任何 HALCON 类型。</summary>
/// <remarks>
/// Provider 自身不保存设备绑定：绑定由 <see cref="HalconDeviceSettingsParser"/> 从机器配置的
/// <c>deviceSettings</c> 解析后随 <see cref="VisionAcquisitionProviderBinding.ProviderState"/> 传入，
/// 打开设备时取用。这样设备配置只有一处来源，不会出现"私有配置与机器配置各写一遍"的漂移。
/// </remarks>
public sealed class HalconAcquisitionProvider : IVisionAcquisitionProvider, IVisionDeviceDiscovery
{
    /// <summary>HALCON Provider稳定身份。</summary>
    public const string ProviderIdentity = "dp.vision.halcon";

    private readonly IReadOnlyList<string>? _discoveryInterfaceNames;

    private bool _disposed;

    /// <summary>创建Provider。</summary>
    /// <param name="discoveryInterfaceNames">
    /// 设备发现要查询的HALCON采集接口名；为空时使用工业相机常用接口
    /// （GigEVision2 / USB3Vision / GenICamTL）。HALCON 没有"列出已安装接口"的查询，因此这里必须由调用方给出候选。
    /// </param>
    public HalconAcquisitionProvider(IReadOnlyList<string>? discoveryInterfaceNames = null) =>
        _discoveryInterfaceNames = discoveryInterfaceNames;

    /// <inheritdoc/>
    public string ProviderId => ProviderIdentity;

    /// <inheritdoc/>
    /// <exception cref="VisionSourceConfigurationException">绑定缺少该Provider的私有绑定对象，或对象类型不属于本Provider。</exception>
    public ValueTask<IVisionAcquisitionDevice> OpenAsync(
        VisionAcquisitionProviderBinding binding,
        CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HalconAcquisitionProvider));
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));
        cancellationToken.ThrowIfCancellationRequested();

        // 绑定对象由本Provider的 deviceSettings 解析器创建；类型不符说明机器配置把别的Type的
        // deviceSettings 配到了这个Source上，必须明确失败而不是猜。
        if (binding.ProviderState is not HalconAcquisitionBinding halconBinding)
            throw new VisionSourceConfigurationException(
                $"HALCON Provider 收到绑定 {binding.ProviderBindingId}，但它没有携带 {nameof(HalconAcquisitionBinding)}；"
                + "请确认该Source的 acquisitionTypeId 指向 HALCON 类型，且 deviceSettings 由 HALCON 解析器解析。");
        return new ValueTask<IVisionAcquisitionDevice>(new HalconAcquisitionDevice(halconBinding));
    }

    /// <summary>
    /// 枚举当前可见的 HALCON 相机，供宿主生成候选绑定。
    /// <para>
    /// 发现不使用Provider私有配置：它的用途正是"还没有配置时先看见现场有哪些设备"，
    /// 因此与绑定列表是否存在无关。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>顺序确定的候选描述；现场确实没有设备时为空列表。</returns>
    /// <exception cref="VisionProviderUnavailableException">未装配 SDK，或全部采集接口都无法枚举。</exception>
    public ValueTask<IReadOnlyList<VisionDeviceDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HalconAcquisitionProvider));
        cancellationToken.ThrowIfCancellationRequested();

        return new ValueTask<IReadOnlyList<VisionDeviceDescriptor>>(
            HalconBoardInfo.ToDescriptors(HalconDeviceEnumeration.Enumerate(_discoveryInterfaceNames)));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return default;
    }
}
