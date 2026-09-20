using System;

namespace DP.Vision.Acquisition;

/// <summary>Provider设备返回的中立帧；所有权随返回值交给Acquisition Runtime，不得暴露厂商对象或裸指针。</summary>
public sealed class VisionProviderFrame : IDisposable
{
    /// <summary>创建中立帧。</summary>
    /// <param name="image">Provider拥有的中立图像；所有权随本对象转移给调用方。</param>
    /// <param name="capturedAtUtc">设备报告或Provider观测的采集时刻。</param>
    /// <param name="deviceSequence">设备可选提供的帧序号；不支持时为空。</param>
    /// <exception cref="ArgumentNullException">图像为空。</exception>
    public VisionProviderFrame(IImageSource image, DateTimeOffset capturedAtUtc, long? deviceSequence = null)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        CapturedAtUtc = capturedAtUtc;
        DeviceSequence = deviceSequence;
    }

    /// <summary>中立图像租约；设备或SDK对象释放后仍必须可读。</summary>
    public IImageSource Image { get; }

    /// <summary>采集时刻。</summary>
    public DateTimeOffset CapturedAtUtc { get; }

    /// <summary>设备帧序号；不支持时为空。</summary>
    public long? DeviceSequence { get; }

    /// <summary>释放本帧拥有的图像租约。</summary>
    public void Dispose() => Image.Dispose();
}
