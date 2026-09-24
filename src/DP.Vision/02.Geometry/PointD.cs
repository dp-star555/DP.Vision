using System;

namespace DP.Vision;

/// <summary>原图像素边缘坐标。像素(column,row)的中心为(column+0.5,row+0.5)，不是显示缩放后的坐标。</summary>
public readonly struct PointD : IEquatable<PointD>
{
    /// <summary>创建有限坐标；不允许NaN、无穷大或绝对值超过10000000。</summary>
    /// <param name = "x">横向原图坐标，向右增大，单位为像素。</param>
    /// <param name = "y">纵向原图坐标，向下增大，单位为像素。</param>
    public PointD(double x, double y)
    {
        if (!Valid(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (!Valid(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        X = x;
        Y = y;
    }

    internal static bool Valid(double n)
    {
        return !double.IsNaN(n) && !double.IsInfinity(n) && Math.Abs(n) <= 10000000;
    }

    /// <summary>横向原图坐标。</summary>
    public double X { get; }

    /// <summary>纵向原图坐标。</summary>
    public double Y { get; }

    /// <summary>两个坐标分量都相等时相等。</summary>
    /// <param name="other">另一个值。</param>
    /// <returns>是否相等。</returns>
    public bool Equals(PointD other)
    {
        return X == other.X && Y == other.Y;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is PointD other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = X.GetHashCode();
            hash = (hash * 397) ^ Y.GetHashCode();
            return hash;
        }
    }

    /// <summary>值相等比较。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否相等。</returns>
    public static bool operator ==(PointD left, PointD right)
    {
        return left.Equals(right);
    }

    /// <summary>值不等比较。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否不等。</returns>
    public static bool operator !=(PointD left, PointD right)
    {
        return !left.Equals(right);
    }
}
