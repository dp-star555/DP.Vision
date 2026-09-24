using System;

namespace DP.Vision;

/// <summary>图像布局；显示分块不会改变原图尺寸。宽、高与像素布局都相同的两个实例相等。</summary>
public sealed class ImageInfo : IEquatable<ImageInfo>
{
    /// <summary>创建图像布局；超过单个托管数组容量时拒绝创建，调用方应改用分块图像源。</summary>
    /// <param name = "width">原图宽度，范围1–1048576，单位为像素。</param>
    /// <param name = "height">原图高度，范围1–1048576，单位为像素。</param>
    /// <param name = "layout">像素通道顺序和位深；宽乘高乘每像素字节数不得超过int最大值。</param>
    public ImageInfo(int width, int height, EPixelLayout layout)
    {
        if (width < 1 || width > 1048576)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height < 1 || height > 1048576)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (!Enum.IsDefined(typeof(EPixelLayout), layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }

        Width = width;
        Height = height;
        Layout = layout;
        if ((long)width * height * BytesPerPixel > int.MaxValue)
        {
            throw new ArgumentException($"源数据图像超过缓冲区的最大容量:{int.MaxValue}。");
        }
    }

    /// <summary>原图宽度，单位为像素。</summary>
    public int Width { get; }

    /// <summary>原图高度，单位为像素。</summary>
    public int Height { get; }

    /// <summary>明确的像素通道顺序及位深。</summary>
    public EPixelLayout Layout { get; }

    /// <summary>每个像素占用的字节数。</summary>
    public int BytesPerPixel =>
        Layout switch
        {
            EPixelLayout.Gray8 => 1,
            EPixelLayout.Gray16 => 2,
            EPixelLayout.Bgr24 or EPixelLayout.Rgb24 => 3,
            EPixelLayout.Bgra32 or EPixelLayout.Rgba32 => 4,
            _ => throw new NotSupportedException($"未定义每像素字节数的布局：{Layout}。"),
        };

    /// <summary>一行占用的字节数，不含额外行填充。</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>整张图像的字节数。</summary>
    public int ByteLength => Stride * Height;

    /// <summary>宽、高与像素布局都相同时相等。</summary>
    /// <param name="other">另一个布局。</param>
    /// <returns>是否描述同一种原图布局。</returns>
    public bool Equals(ImageInfo? other)
    {
        return other is not null && Width == other.Width && Height == other.Height && Layout == other.Layout;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as ImageInfo);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            return (Width * 397 ^ Height) * 397 ^ (int)Layout;
        }
    }

    /// <summary>值相等比较；两侧都为null时相等。</summary>
    /// <param name="left">左侧布局。</param>
    /// <param name="right">右侧布局。</param>
    /// <returns>是否相等。</returns>
    public static bool operator ==(ImageInfo? left, ImageInfo? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    /// <summary>值不等比较。</summary>
    /// <param name="left">左侧布局。</param>
    /// <param name="right">右侧布局。</param>
    /// <returns>是否不等。</returns>
    public static bool operator !=(ImageInfo? left, ImageInfo? right)
    {
        return !(left == right);
    }
}
