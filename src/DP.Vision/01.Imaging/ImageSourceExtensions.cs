using System;

namespace DP.Vision;

/// <summary>建立在<see cref="IImageSource.CopyTo"/>之上的像素读取辅助，不要求图像源实现额外成员。</summary>
public static class ImageSourceExtensions
{
    /// <summary>
    /// 把原图中的半开像素矩形 [x, x+width) × [y, y+height) 按行紧密复制到调用方数组，
    /// 每行 width × BytesPerPixel 字节，保留原像素布局；只读取该矩形，不复制整幅图像。
    /// </summary>
    /// <param name="source">借用的图像源。</param>
    /// <param name="x">左侧像素列，非负。</param>
    /// <param name="y">顶部像素行，非负。</param>
    /// <param name="width">像素宽度，至少为1，且矩形不得越出原图。</param>
    /// <param name="height">像素高度，至少为1，且矩形不得越出原图。</param>
    /// <param name="destination">调用方拥有的目标数组。</param>
    /// <param name="destinationOffset">目标数组中的起始字节偏移。</param>
    public static void CopyRegion(
        this IImageSource source,
        int x,
        int y,
        int width,
        int height,
        byte[] destination,
        int destinationOffset = 0
    )
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        var info = source.Info;
        if (x < 0 || width < 1 || (long)x + width > info.Width)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "区域的列范围越出原图。");
        }

        if (y < 0 || height < 1 || (long)y + height > info.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "区域的行范围越出原图。");
        }

        int rowBytes = width * info.BytesPerPixel;
        if (destinationOffset < 0 || destinationOffset + (long)rowBytes * height > destination.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationOffset), "目标数组容纳不下该区域。");
        }

        int sourceOffset = y * info.Stride + x * info.BytesPerPixel;
        for (int row = 0; row < height; row++)
        {
            source.CopyTo(sourceOffset, destination, destinationOffset, rowBytes);
            sourceOffset += info.Stride;
            destinationOffset += rowBytes;
        }
    }
}
