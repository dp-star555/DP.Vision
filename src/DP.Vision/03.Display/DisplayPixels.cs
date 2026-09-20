using System;

namespace DP.Vision;

/// <summary>
/// 供画布使用的显示像素副本，不修改检测算法使用的原始像素。
/// Gray8 保持灰度布局；RGB/RGBA 显式交换通道；Gray16 根据显示窗口映射为 Gray8。
/// </summary>
public sealed class DisplayPixels
{
    /// <summary>
    /// 接管本次转换产生的显示缓冲区。
    /// </summary>
    /// <param name = "width">图块宽度，单位为像素。</param>
    /// <param name = "height">图块高度，单位为像素。</param>
    /// <param name = "layout">转换后的像素布局。</param>
    /// <param name = "bytes">本对象拥有的显示字节数组，不与原始图像共享存储。</param>
    private DisplayPixels(int width, int height, EPixelLayout layout, byte[] bytes)
    {
        Width = width;
        Height = height;
        Layout = layout;
        Bytes = bytes;
    }

    /// <summary>
    /// 显示图块宽度，单位为像素。
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// 显示图块高度，单位为像素。
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// 显示布局：Gray8、Bgr24 或非预乘 Alpha 的 Bgra32。
    /// </summary>
    public EPixelLayout Layout { get; }

    /// <summary>
    /// 本对象拥有的显示像素数组。此数组不是算法原始像素的别名。
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>
    /// 每行紧密排列的字节数，不包含额外行填充。
    /// </summary>
    public int Stride =>
        Width
        * (
            Layout == EPixelLayout.Gray8 ? 1
            : Layout == EPixelLayout.Bgr24 ? 3
            : 4
        );

    /// <summary>
    /// 复制原始像素并转换为显示布局，不修改源图的像素值或布局元数据。
    /// </summary>
    /// <param name = "source">只在调用期间借用的原始图像租约；本方法不释放它。</param>
    /// <param name = "options">显示设置。Gray16 使用 Gray16Low/Gray16High 进行线性窗口映射，超出窗口的值截断到 0/255。</param>
    /// <returns>拥有独立像素数组的显示图块；其生命周期不依赖源图。</returns>
    /// <exception cref = "ArgumentNullException">源图或显示设置为空。</exception>
    public static DisplayPixels From(IImageSource source, CanvasOptions options)
    {
        if (source == null || options == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var info = source.Info;
        var raw = new byte[info.ByteLength];
        source.CopyTo(0, raw, 0, raw.Length);
        if (info.Layout == EPixelLayout.Gray16)
        {
            var mapped = new byte[info.Width * info.Height];
            for (int i = 0; i < mapped.Length; i++)
            {
                // 原始 Gray16 是小端无符号数；只转换显示副本，不覆盖原始16位数据。
                int value = raw[2 * i] | raw[2 * i + 1] << 8;
                long displayValue =
                    ((long)value - options.Gray16Low) * 255 / (options.Gray16High - options.Gray16Low);
                mapped[i] = (byte)Math.Max(0, Math.Min(255, displayValue));
            }

            return new DisplayPixels(info.Width, info.Height, EPixelLayout.Gray8, mapped);
        }

        if (info.Layout == EPixelLayout.Rgb24 || info.Layout == EPixelLayout.Rgba32)
        {
            // GDI/WPF显示适配使用BGR顺序；只交换红、蓝通道，Alpha保持不变。
            for (int i = 0; i < raw.Length; i += info.BytesPerPixel)
            {
                byte red = raw[i];
                raw[i] = raw[i + 2];
                raw[i + 2] = red;
            }
        }

        EPixelLayout displayLayout = info.Layout;
        if (displayLayout == EPixelLayout.Rgb24)
        {
            displayLayout = EPixelLayout.Bgr24;
        }
        else if (displayLayout == EPixelLayout.Rgba32)
        {
            displayLayout = EPixelLayout.Bgra32;
        }

        return new DisplayPixels(info.Width, info.Height, displayLayout, raw);
    }
}
