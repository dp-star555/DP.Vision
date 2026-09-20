using System;

namespace DP.Vision.Algorithms;

/// <summary>拥有像素的算法使用的离散半开范围；与RectD不同，不能表达小数几何。</summary>
public readonly struct PixelBounds
{
    /// <summary>创建非空范围，并检查边缘非负及数值溢出。</summary>
    /// <param name = "x">非负左侧像素边缘坐标。</param>
    /// <param name = "y">非负顶部像素边缘坐标。</param>
    /// <param name = "width">正数像素宽度。</param>
    /// <param name = "height">正数像素高度。</param>
    public PixelBounds(int x, int y, int width, int height)
    {
        if (
            x < 0
            || y < 0
            || width < 1
            || height < 1
            || (long)x + width > int.MaxValue
            || (long)y + height > int.MaxValue
        )
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>左侧像素边缘。</summary>
    public int X { get; }

    /// <summary>顶部像素边缘。</summary>
    public int Y { get; }

    /// <summary>像素宽度。</summary>
    public int Width { get; }

    /// <summary>像素高度。</summary>
    public int Height { get; }

    /// <summary>检查是否完全包含于图像，并拒绝默认空范围。</summary>
    /// <param name = "image">用于检查包含关系的图像，只读取尺寸信息。</param>
    public bool Fits(IImageSource image)
    {
        return image != null
            && Width > 0
            && Height > 0
            && (long)X + Width <= image.Info.Width
            && (long)Y + Height <= image.Info.Height;
    }

    /// <summary>无损转换为连续的像素边缘几何。</summary>
    public RectD ToRect()
    {
        return new RectD(X, Y, Width, Height);
    }
}
