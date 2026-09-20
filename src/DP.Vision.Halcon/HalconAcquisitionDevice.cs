using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using DP.Vision.Algorithms;

namespace DP.Vision.Halcon;

/// <summary>HALCON采集设备Adapter；阶段A保持每次采集独立打开/关闭设备并复制为中立图像，不承诺长连接。</summary>
public sealed class HalconAcquisitionDevice : IVisionAcquisitionDevice
{
    private readonly HalconAcquisitionBinding _binding;
    private bool _disposed;

    /// <summary>创建设备Adapter。</summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <exception cref="ArgumentNullException">绑定为空。</exception>
    public HalconAcquisitionDevice(HalconAcquisitionBinding binding)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Identity = new VisionDeviceIdentity(
            HalconAcquisitionProvider.ProviderIdentity,
            binding.BindingId,
            binding.CanonicalKey,
            binding.SerialNumber);
    }

    /// <inheritdoc/>
    public VisionDeviceIdentity Identity { get; }

    /// <inheritdoc/>
    public async ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (_disposed)
            throw new ObjectDisposedException(nameof(HalconAcquisitionDevice));
        cancellationToken.ThrowIfCancellationRequested();

        // 现有实现每次采集独立打开/关闭设备；grab_timeout 由请求超时驱动，避免无限等待触发。
        var timeout = (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(request.Timeout.TotalMilliseconds)));
        var capture = new HalconCameraCapture(timeout);
        var options = new CameraCaptureOptions(
            request.ExposureMicroseconds ?? 0,
            request.GainDecibels ?? 0,
            MapTrigger(request.TriggerMode));
        IImageSource image;
        try
        {
            image = await capture.CaptureAsync(_binding.CameraId, options, cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new VisionProviderUnavailableException(
                HalconAcquisitionProvider.ProviderIdentity,
                "HALCON SDK 未部署；请安装 HALCON 运行时并以 HALCONROOT 或 HalconDotNetPath 构建本程序集。",
                exception);
        }

        return new VisionProviderFrame(image, DateTimeOffset.UtcNow, null);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return default;
    }

    private static bool MapTrigger(EVisionTriggerMode mode)
    {
        switch (mode)
        {
            case EVisionTriggerMode.External:
                return true;
            case EVisionTriggerMode.KeepCurrent:
            case EVisionTriggerMode.FreeRun:
                return false;
            default:
                // 无法把软件触发表达为当前 HALCON 打开参数时明确拒绝，而不是静默按自由运行采集。
                throw new VisionParameterNotSupportedException(
                    $"HALCON Adapter 不支持触发模式 {mode}；请在Provider私有配置中设置触发源。");
        }
    }
}
