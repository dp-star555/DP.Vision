using System;
using System.Threading;

namespace DP.Vision;

/// <summary>客户创建不可变图像的统一入口；隐藏内部像素缓冲区及临时租约。</summary>
public static class VisionImage
{
    /// <summary>复制紧密排列的原图像素；调用方随后修改输入数组不会改变图像。</summary>
    /// <param name="info">原图尺寸及像素布局。</param>
    /// <param name="pixels">调用方拥有的数组，长度必须等于info.ByteLength。</param>
    /// <returns>由调用方释放的图像源；检测、显示可以各自在内部保留独立租约。</returns>
    public static IImageSource CopyFrom(ImageInfo info, byte[] pixels)
    {
        return MemoryImageSource.CopyFrom(info, pixels);
    }

    /// <summary>只支持8位灰度的功能在入口调用：彩色或16位图像明确报错，不隐式转换。</summary>
    /// <param name="image">输入图像。</param>
    /// <param name="feature">功能名称，用于提示，例如“模板匹配”。</param>
    /// <exception cref="NotSupportedException">图像不是8位灰度。</exception>
    public static void RequireGray8(IImageSource image, string feature)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (image.Info.Layout != EPixelLayout.Gray8)
            throw new NotSupportedException($"{feature}只支持8位灰度图像，当前图像为{Describe(image.Info.Layout)}，请先转换为8位灰度。");
    }

    /// <summary>转换为8位灰度：8位彩色按亮度公式（R 0.299、G 0.587、B 0.114，与OpenCV一致，Alpha不参与）转换，8位灰度原样复制。</summary>
    /// <param name="image">输入图像。</param>
    /// <param name="token">取消。</param>
    /// <returns>由调用方释放的8位灰度图像。</returns>
    /// <exception cref="NotSupportedException">16位灰度需要指定增益，不能直接转换。</exception>
    public static IImageSource ToGray8(IImageSource image, CancellationToken token = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        var info = image.Info;
        if (info.Layout == EPixelLayout.Gray16) throw new NotSupportedException("16位灰度图像转换为8位需要指定增益，请使用图像预处理的16位转8位。");
        var raw = new byte[info.ByteLength]; image.CopyTo(0, raw, 0, raw.Length);
        if (info.Layout == EPixelLayout.Gray8) return CopyFrom(info, raw);
        int channels = info.BytesPerPixel;
        bool bgr = info.Layout is EPixelLayout.Bgr24 or EPixelLayout.Bgra32;
        var gray = new byte[info.Width * info.Height];
        for (int i = 0; i < gray.Length; i++)
        {
            if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            int o = i * channels, r = raw[bgr ? o + 2 : o], g = raw[o + 1], b = raw[bgr ? o : o + 2];
            gray[i] = (byte)((r * 4899 + g * 9617 + b * 1868 + 8192) >> 14);
        }
        return CopyFrom(new ImageInfo(info.Width, info.Height, EPixelLayout.Gray8), gray);
    }

    private static string Describe(EPixelLayout layout) => layout switch
    {
        EPixelLayout.Gray16 => "16位灰度",
        EPixelLayout.Bgr24 => "BGR彩色",
        EPixelLayout.Rgb24 => "RGB彩色",
        EPixelLayout.Bgra32 => "BGRA彩色",
        EPixelLayout.Rgba32 => "RGBA彩色",
        _ => layout.ToString()
    };
}
