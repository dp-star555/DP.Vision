using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>原图像素边缘坐标。像素(column,row)的中心为(column+0.5,row+0.5)，不是显示缩放后的坐标。</summary>
public readonly struct PointD
{
    /// <summary>创建有限坐标；不允许NaN、无穷大或绝对值超过10000000。</summary>
    /// <param name = "x">横向原图坐标，向右增大，单位为像素。</param>
    /// <param name = "y">纵向原图坐标，向下增大，单位为像素。</param>
    public PointD(double x, double y)
    {
        if (!Valid(x) || !Valid(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x));
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
}
