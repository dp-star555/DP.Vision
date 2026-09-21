#if HALCON_SDK
using System;
using System.Threading;
using HalconDotNet;

namespace DP.Vision.Halcon;

/// <summary>HALCON像素到统一IImageSource的显式复制边界；不接管输入SDK对象。</summary>
/// <remarks>
/// 本类型只是公共入口：真正的像素落地与布局/预算判定在 <see cref="HalconNeutralFrames"/>，
/// 与外部回调长连接共用同一份实现，避免两条采集路径各自漂移。
/// </remarks>
public static class HalconImageSource
{
    /// <summary>复制完整矩阵的byte灰度/RGB或uint2灰度；RGB交错为BGR，不借用SDK指针。不导入HALCON domain，Region需单独保存。</summary>
    /// <param name="image">借用的单张HALCON图像；调用者继续拥有。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="maximumBytes">输出像素字节上限，默认512MiB；复制过程还需要临时和目标缓冲区。</param>
    /// <returns>由调用者释放的独立中立图像。</returns>
    public static IImageSource CopyFrom(HObject image, CancellationToken token = default, int maximumBytes = HalconNeutralFrames.DefaultMaximumBytes)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        token.ThrowIfCancellationRequested();
        using var frame = HObjectGrabFrame.Borrow(image);
        return HalconNeutralFrames.Copy(frame, token, maximumBytes);
    }
}
#endif
