using System;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Algorithms;

/// <summary>中立单帧相机参数，零曝光/增益表示保持设备当前设置。</summary>
public sealed class CameraCaptureOptions
{
    /// <summary>创建有限非负相机参数。</summary>
    /// <param name="exposure">曝光，单位由设备Adapter说明。</param>
    /// <param name="gain">增益，单位由设备Adapter说明。</param>
    /// <param name="triggered">使用外部触发。</param>
    public CameraCaptureOptions(double exposure = 0, double gain = 0, bool triggered = false)
    {
        if (
            double.IsNaN(exposure)
            || double.IsInfinity(exposure)
            || exposure < 0
            || double.IsNaN(gain)
            || double.IsInfinity(gain)
            || gain < 0
        )
            throw new ArgumentException("Invalid camera settings.");
        Exposure = exposure;
        Gain = gain;
        Triggered = triggered;
    }

    /// <summary>曝光。</summary>
    public double Exposure { get; }

    /// <summary>增益。</summary>
    public double Gain { get; }

    /// <summary>是否外部触发。</summary>
    public bool Triggered { get; }
}

/// <summary>宿主选择的真实相机实现；调用者拥有返回的中立图像，不能返回活动SDK句柄。</summary>
public interface ICameraCapture
{
    /// <summary>采集一帧，只选择已装配设备实现，不自动换后端。</summary>
    /// <param name="cameraId">宿主设备键。</param>
    /// <param name="options">采集参数。</param>
    /// <param name="token">协作取消。</param>
    /// <returns>调用者必须释放的统一图像源。</returns>
    Task<IImageSource> CaptureAsync(
        string cameraId,
        CameraCaptureOptions options,
        CancellationToken token = default
    );
}
