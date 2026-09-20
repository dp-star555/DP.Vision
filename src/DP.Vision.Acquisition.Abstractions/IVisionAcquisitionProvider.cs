using System;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition;

/// <summary>采集Provider适配器；公共接口保持最小，发现和健康查询由可选能力提供。</summary>
public interface IVisionAcquisitionProvider : IAsyncDisposable
{
    /// <summary>Provider稳定身份，例如dp.vision.halcon。</summary>
    string ProviderId { get; }

    /// <summary>按Provider私有绑定身份打开设备。</summary>
    /// <param name="providerBindingId">Provider配置内的设备绑定身份。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>调用方拥有的已打开设备。</returns>
    ValueTask<IVisionAcquisitionDevice> OpenAsync(
        string providerBindingId,
        CancellationToken cancellationToken);
}

/// <summary>已打开的设备；由Acquisition Runtime按ResourceKey持有并复用。</summary>
public interface IVisionAcquisitionDevice : IAsyncDisposable
{
    /// <summary>设备报告的规范身份，用于校验机器配置声明的ResourceKey。</summary>
    VisionDeviceIdentity Identity { get; }

    /// <summary>采集一帧；返回的中立图像必须在设备或SDK对象释放后仍然有效。</summary>
    /// <param name="request">采集请求。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>所有权交给调用方的中立帧。</returns>
    ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken);
}
