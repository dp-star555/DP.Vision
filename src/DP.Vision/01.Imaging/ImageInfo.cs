using System;
using System.Collections.Generic;

namespace DP.Vision;

/// <summary>图像布局；显示分块不会改变原图尺寸。</summary>
public sealed class ImageInfo
{
    /// <summary>创建图像布局；超过单个托管数组容量时拒绝创建，调用方应改用分块图像源。</summary>
    /// <param name = "width">原图宽度，范围1–1048576，单位为像素。</param>
    /// <param name = "height">原图高度，范围1–1048576，单位为像素。</param>
    /// <param name = "layout">像素通道顺序和位深；宽乘高乘每像素字节数不得超过int最大值。</param>
    public ImageInfo(int width, int height, EPixelLayout layout)
    {
        if (
            width < 1
            || height < 1
            || width > 1048576
            || height > 1048576
            || !Enum.IsDefined(typeof(EPixelLayout), layout)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(width));
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
        Layout == EPixelLayout.Gray8 ? 1
        : Layout == EPixelLayout.Gray16 ? 2
        : Layout == EPixelLayout.Bgr24 || Layout == EPixelLayout.Rgb24 ? 3
        : 4;

    /// <summary>一行占用的字节数，不含额外行填充。</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>整张图像的字节数。</summary>
    public int ByteLength => Stride * Height;
}
