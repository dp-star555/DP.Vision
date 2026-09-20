using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
#if HALCON_SDK
using HalconDotNet;
#endif

namespace DP.Vision.Halcon;

/// <summary>直接基于HALCON SDK的中立相机采集实现，不经过旧视觉框架。CameraId为“接口名|设备名”。</summary>
public sealed class HalconCameraCapture : ICameraCapture
{
    private readonly int _grabTimeoutMilliseconds;
    /// <summary>创建每次采集独立打开/关闭设备的读取器，不共享可变设备句柄。</summary>
    /// <param name="grabTimeoutMilliseconds">设备采集超时；采集接口必须支持grab_timeout，不能无限等触发。</param>
    public HalconCameraCapture(int grabTimeoutMilliseconds = 5000)
    {
        if (grabTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(grabTimeoutMilliseconds));
        _grabTimeoutMilliseconds = grabTimeoutMilliseconds;
    }

    /// <summary>当前程序集是否包含SDK实现；不是设备在线或许可证通过的证明。</summary>
    public static bool IsSdkEnabled
    {
        get
        {
#if HALCON_SDK
            return true;
#else
            return false;
#endif
        }
    }

    /// <inheritdoc/>
    public Task<IImageSource> CaptureAsync(string cameraId, CameraCaptureOptions options, CancellationToken token = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        var parts = (cameraId ?? string.Empty).Split(new[] { '|' }, 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException("CameraId must be interface|device.", nameof(cameraId));
        token.ThrowIfCancellationRequested();
#if HALCON_SDK
        // SDK阻塞操作隔离到后台，取消在调用边界检查，真实等待受设备grab_timeout约束。
        return Task.Run<IImageSource>(() =>
        {
            token.ThrowIfCancellationRequested();
            using var camera = new HFramegrabber(parts[0], 1, 1, 0, 0, 0, 0,
                "default", -1, "default", -1, options.Triggered ? "true" : "false", "default", parts[1], 0, -1);
            camera.SetFramegrabberParam("grab_timeout", _grabTimeoutMilliseconds);
            if (options.Exposure > 0) camera.SetFramegrabberParam("ExposureTime", options.Exposure);
            if (options.Gain > 0) camera.SetFramegrabberParam("Gain", options.Gain);
            token.ThrowIfCancellationRequested();
            using var image = camera.GrabImageAsync(-1);
            token.ThrowIfCancellationRequested();
            return HalconImageSource.CopyFrom(image, token);
        }, token);
#else
        throw new PlatformNotSupportedException("Build DP.Vision.Halcon with HALCONROOT or HalconDotNetPath and deploy the HALCON runtime.");
#endif
    }
}
