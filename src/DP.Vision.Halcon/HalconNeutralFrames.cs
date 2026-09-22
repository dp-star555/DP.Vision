using System;
using System.Diagnostics;
using System.Threading;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON 设备帧到中立图像的**唯一**像素落地实现：主动单次采集与外部回调长连接都走这里。
/// <para>
/// 之所以只留一条路径：行/通道排布与预算校验一旦各写一份，就会在两条采集路径上慢慢漂移，
/// 而漂移出来的差异只会在现场以"偶发图像错位"的形式暴露。
/// </para>
/// <para>
/// 诊断文本保持与既有 <c>HalconImageSource.CopyFrom</c> 完全一致（英文原句），
/// 避免把公共可见的错误文本在本次改动里一并改掉。
/// </para>
/// <para>
/// 上面刻意用代码字体而不是 cref 引用：HalconImageSource 整体位于 HALCON_SDK 条件编译内，
/// 未装 SDK 的机器上该类型不存在，cref 无法解析会退化成构建警告。
/// </para>
/// </summary>
internal static class HalconNeutralFrames
{
    /// <summary>默认像素复制预算：512 MiB；复制过程还需要临时和目标缓冲区。</summary>
    public const int DefaultMaximumBytes = 512 * 1024 * 1024;

    /// <summary>把设备帧复制为独立的中立图像。</summary>
    /// <param name="frame">借用的设备帧；调用方继续拥有，本方法不释放它。</param>
    /// <param name="token">取消令牌；在通道之间与逐行交错之间检查。</param>
    /// <param name="maximumBytes">输出像素字节上限。</param>
    /// <returns>由调用者释放的独立中立图像。</returns>
    /// <exception cref="ArgumentNullException">设备帧为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">预算不为正。</exception>
    /// <exception cref="NotSupportedException">通道数或像素类型不受支持。</exception>
    /// <exception cref="InvalidOperationException">图像超出像素复制预算。</exception>
    public static IImageSource Copy(
        IHalconGrabFrame frame,
        CancellationToken token = default,
        int maximumBytes = DefaultMaximumBytes) =>
        CopyObserved(frame, token, maximumBytes).Image;

    /// <summary>
    /// 把设备帧复制为独立的中立图像，并同时报告这次落地的耗时与字节数。
    /// <para>
    /// 计时从通道搬运开始：布局解析与预算校验是元数据成本，把它算进"像素搬运"会让监视数字失去意义；
    /// 取消检查也不计时，因为它是策略而不是搬运。
    /// </para>
    /// </summary>
    /// <param name="frame">借用的设备帧；调用方继续拥有，本方法不释放它。</param>
    /// <param name="token">取消令牌；在通道之间与逐行交错之间检查。</param>
    /// <param name="maximumBytes">输出像素字节上限。</param>
    /// <returns>独立中立图像与本次像素落地观测。</returns>
    /// <exception cref="ArgumentNullException">设备帧为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">预算不为正。</exception>
    /// <exception cref="NotSupportedException">通道数或像素类型不受支持。</exception>
    /// <exception cref="InvalidOperationException">图像超出像素复制预算。</exception>
    public static (IImageSource Image, VisionPixelTransferObservation Observation) CopyObserved(
        IHalconGrabFrame frame,
        CancellationToken token = default,
        int maximumBytes = DefaultMaximumBytes)
    {
        if (frame is null)
            throw new ArgumentNullException(nameof(frame));
        if (maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        token.ThrowIfCancellationRequested();

        var layout = ResolveLayout(frame.ChannelCount, frame.PixelTypeName);
        var info = new ImageInfo(frame.Width, frame.Height, layout);
        if (info.ByteLength > maximumBytes)
            throw new InvalidOperationException("HALCON image exceeds the configured pixel copy budget.");

        var bytes = new byte[info.ByteLength];
        var stopwatch = Stopwatch.StartNew();
        if (layout == EPixelLayout.Bgr24)
        {
            var count = checked(frame.Width * frame.Height);
            var red = new byte[count];
            var green = new byte[count];
            var blue = new byte[count];
            frame.CopyChannelInto(0, red);
            token.ThrowIfCancellationRequested();
            frame.CopyChannelInto(1, green);
            token.ThrowIfCancellationRequested();
            frame.CopyChannelInto(2, blue);

            // HALCON 三个通道是分离平面，中立布局要求 BGR 交错。
            for (var y = 0; y < frame.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < frame.Width; x++)
                {
                    var index = (y * frame.Width) + x;
                    bytes[(index * 3) + 0] = blue[index];
                    bytes[(index * 3) + 1] = green[index];
                    bytes[(index * 3) + 2] = red[index];
                }
            }
        }
        else
        {
            // 灰度按整块搬运：uint2 的字节序由 HALCON 决定，这里不做重排。
            frame.CopyChannelInto(0, bytes);
            token.ThrowIfCancellationRequested();
        }

        stopwatch.Stop();
        return (
            VisionImage.CopyFrom(info, bytes),
            new VisionPixelTransferObservation(stopwatch.Elapsed, info.ByteLength));
    }

    /// <summary>由通道数与像素类型名解析中立布局；不支持时明确拒绝而不是按默认布局猜。</summary>
    /// <param name="channelCount">通道数。</param>
    /// <param name="pixelTypeName">HALCON 像素类型名。</param>
    /// <returns>中立布局。</returns>
    /// <exception cref="NotSupportedException">通道数或像素类型不受支持。</exception>
    private static EPixelLayout ResolveLayout(int channelCount, string pixelTypeName)
    {
        if (channelCount == 1)
        {
            if (pixelTypeName == "byte")
                return EPixelLayout.Gray8;
            if (pixelTypeName == "uint2")
                return EPixelLayout.Gray16;
            throw new NotSupportedException("Only byte and uint2 single-channel pixels are supported.");
        }

        if (channelCount != 3)
            throw new NotSupportedException("Only one or three image channels are supported.");

        if (pixelTypeName != "byte")
            throw new NotSupportedException("RGB input must have byte channels.");
        return EPixelLayout.Bgr24;
    }
}
