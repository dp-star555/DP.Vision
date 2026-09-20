using System;

namespace DP.Vision.Acquisition;

/// <summary>采集结果；由Acquisition Runtime包装Provider帧并补齐来源事实后交给调用方。</summary>
public sealed class VisionCapturedImage : IDisposable
{
    /// <summary>创建采集结果。</summary>
    /// <param name="frame">已带CaptureId身份的帧；所有权随本对象转移。</param>
    /// <param name="metadata">来源事实。</param>
    /// <exception cref="ArgumentNullException">帧或来源事实为空。</exception>
    public VisionCapturedImage(ImageFrame frame, VisionCaptureMetadata metadata)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    /// <summary>采集得到的帧；FrameId即CaptureId。</summary>
    public ImageFrame Frame { get; }

    /// <summary>来源事实。</summary>
    public VisionCaptureMetadata Metadata { get; }

    /// <summary>释放本结果拥有的帧租约。</summary>
    public void Dispose() => Frame.Dispose();
}
