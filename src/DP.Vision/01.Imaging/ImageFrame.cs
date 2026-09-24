using System;

namespace DP.Vision;

/// <summary>具有原图身份的只读图像租约；同一身份只能表示同一份像素内容。</summary>
public sealed class ImageFrame : IDisposable
{
    /// <summary>保留独立图像租约，调用者仍拥有输入句柄。</summary>
    /// <param name="frameId">原图内容身份；重新采集或修改像素必须换身份。</param>
    /// <param name="image">借用的只读图像。</param>
    public ImageFrame(string frameId, IImageSource image)
    {
        if (!Identity.IsValid(frameId))
            throw new ArgumentException("Invalid frame identity.", nameof(frameId));
        FrameId = frameId;
        Image = (image ?? throw new ArgumentNullException(nameof(image))).Retain();
    }

    /// <summary>原图身份。</summary>
    public string FrameId { get; }

    /// <summary>借用图像；消费者不得释放此句柄，需要独立生命周期时调用Retain。</summary>
    public IImageSource Image { get; }

    /// <summary>创建独立帧租约。</summary>
    /// <returns>调用者释放的新句柄。</returns>
    public ImageFrame Retain() => new ImageFrame(FrameId, Image);

    /// <summary>释放本帧拥有的图像租约。</summary>
    public void Dispose() => Image.Dispose();
}
