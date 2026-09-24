using System;

namespace DP.Vision;

/// <summary>连续的半开矩形范围；允许零宽或零高，以表示点、线的外接范围。</summary>
public readonly struct RectD : IEquatable<RectD>
{
    /// <summary>创建有限的原图范围；坐标、尺寸和右下端点均不得超出支持范围。</summary>
    /// <param name = "x">左边界原图坐标。</param>
    /// <param name = "y">上边界原图坐标。</param>
    /// <param name = "width">非负宽度，单位为原图像素。</param>
    /// <param name = "height">非负高度，单位为原图像素。</param>
    public RectD(double x, double y, double width, double height)
    {
        if (!PointD.Valid(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (!PointD.Valid(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (!PointD.Valid(width) || width < 0 || !PointD.Valid(x + width))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (!PointD.Valid(height) || height < 0 || !PointD.Valid(y + height))
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>左边界原图坐标。</summary>
    public double X { get; }

    /// <summary>上边界原图坐标。</summary>
    public double Y { get; }

    /// <summary>原图范围宽度。</summary>
    public double Width { get; }

    /// <summary>原图范围高度。</summary>
    public double Height { get; }

    /// <summary>右侧排他边界。</summary>
    public double Right => X + Width;

    /// <summary>下侧排他边界。</summary>
    public double Bottom => Y + Height;

    /// <summary>判断范围是否相交，边界接触也算相交</summary>
    /// <param name = "other">要比较的另一原图范围。</param>
    /// <param name = "margin">相交检查的扩展余量，单位为原图像素；屏幕线宽应先换算。</param>
    /// <returns>两范围是否相交或接触。</returns>
    public bool Intersects(RectD other, double margin = 0)
    {
        return X <= other.Right + margin
            && Right >= other.X - margin
            && Y <= other.Bottom + margin
            && Bottom >= other.Y - margin;
    }

    /// <summary>按左上包含、右下排除的半开规则判断点是否在范围内。</summary>
    /// <param name = "p">原图坐标点。</param>
    /// <returns>点是否在半开范围内。</returns>
    public bool Contains(PointD p)
    {
        return p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    }

    /// <summary>位置与尺寸都相等时相等。</summary>
    /// <param name="other">另一个值。</param>
    /// <returns>是否相等。</returns>
    public bool Equals(RectD other)
    {
        return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is RectD other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = X.GetHashCode();
            hash = (hash * 397) ^ Y.GetHashCode();
            hash = (hash * 397) ^ Width.GetHashCode();
            hash = (hash * 397) ^ Height.GetHashCode();
            return hash;
        }
    }

    /// <summary>值相等比较。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否相等。</returns>
    public static bool operator ==(RectD left, RectD right)
    {
        return left.Equals(right);
    }

    /// <summary>值不等比较。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否不等。</returns>
    public static bool operator !=(RectD left, RectD right)
    {
        return !left.Equals(right);
    }
}
