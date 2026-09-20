using System;

namespace DP.Vision.Algorithms;

/// <summary>指定坐标系内的有限二维坐标；单位与轴方向由标定调用者声明，不隐式当作图像像素。</summary>
public readonly struct Coordinate2D
{
    /// <summary>创建有限坐标。</summary>
    /// <param name="x">第一轴坐标。</param>
    /// <param name="y">第二轴坐标。</param>
    public Coordinate2D(double x, double y)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
            throw new ArgumentOutOfRangeException(nameof(x), "坐标必须有限。");
        X = x;
        Y = y;
    }

    /// <summary>第一轴坐标。</summary>
    public double X { get; }
    /// <summary>第二轴坐标。</summary>
    public double Y { get; }
}
