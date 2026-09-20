#if HALCON_SDK
using System;
using System.Runtime.InteropServices;
using System.Threading;
using HalconDotNet;

namespace DP.Vision.Halcon;

/// <summary>HALCON像素到统一IImageSource的显式复制边界；不接管输入SDK对象。</summary>
public static class HalconImageSource
{
    /// <summary>复制完整矩阵的byte灰度/RGB或uint2灰度；RGB交错为BGR，不借用SDK指针。不导入HALCON domain，Region需单独保存。</summary>
    /// <param name="image">借用的单张HALCON图像；调用者继续拥有。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="maximumBytes">输出像素字节上限，默认512MiB；复制过程还需要临时和目标缓冲区。</param>
    /// <returns>由调用者释放的独立中立图像。</returns>
    public static IImageSource CopyFrom(HObject image, CancellationToken token = default, int maximumBytes = 512 * 1024 * 1024)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        token.ThrowIfCancellationRequested();
        HOperatorSet.CountObj(image, out var objectCount);
        using (objectCount) if (objectCount.I != 1) throw new ArgumentException("Exactly one image is required.", nameof(image));
        HOperatorSet.CountChannels(image, out var channels);
        using (channels)
        {
            if (channels.I == 1)
            {
                HOperatorSet.GetImagePointer1(image, out var pointer, out var type, out var width, out var height);
                using (pointer) using (type) using (width) using (height)
                {
                    var layout = type.S == "byte" ? EPixelLayout.Gray8 : type.S == "uint2" ? EPixelLayout.Gray16
                        : throw new NotSupportedException("Only byte and uint2 single-channel pixels are supported.");
                    var info = new ImageInfo(width.I, height.I, layout);
                    EnsureBudget(info, maximumBytes);
                    var bytes = new byte[info.ByteLength];
                    Marshal.Copy(pointer.IP, bytes, 0, bytes.Length);
                    token.ThrowIfCancellationRequested();
                    return VisionImage.CopyFrom(info, bytes);
                }
            }
            if (channels.I != 3) throw new NotSupportedException("Only one or three image channels are supported.");
            HOperatorSet.GetImagePointer3(image, out var red, out var green, out var blue, out var pixelType, out var w, out var h);
            using (red) using (green) using (blue) using (pixelType) using (w) using (h)
            {
                if (pixelType.S != "byte") throw new NotSupportedException("RGB input must have byte channels.");
                var info = new ImageInfo(w.I, h.I, EPixelLayout.Bgr24);
                EnsureBudget(info, maximumBytes);
                int count = checked(w.I * h.I);
                var r = new byte[count]; var g = new byte[count]; var b = new byte[count];
                Marshal.Copy(red.IP, r, 0, count); Marshal.Copy(green.IP, g, 0, count); Marshal.Copy(blue.IP, b, 0, count);
                var bytes = new byte[info.ByteLength];
                for (int y = 0; y < h.I; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < w.I; x++)
                    {
                        int i = y * w.I + x;
                        bytes[i * 3] = b[i]; bytes[i * 3 + 1] = g[i]; bytes[i * 3 + 2] = r[i];
                    }
                }
                return VisionImage.CopyFrom(info, bytes);
            }
        }
    }

    private static void EnsureBudget(ImageInfo info, int maximumBytes)
    {
        if (info.ByteLength > maximumBytes) throw new InvalidOperationException("HALCON image exceeds the configured pixel copy budget.");
    }
}
#endif
