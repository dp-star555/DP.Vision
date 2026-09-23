using System;

namespace DP.Vision;

/// <summary>圆或旋转椭圆几何。</summary>
public sealed class EllipseGeometry : Geometry
{
    /// <summary>用正半径创建椭圆；两个半径相等时为圆。</summary>
    /// <param name = "center">中心的原图坐标。</param>
    /// <param name = "radiusX">旋转前的水平半径，必须大于0，单位为原图像素。</param>
    /// <param name = "radiusY">旋转前的垂直半径，必须大于0，单位为原图像素。</param>
    /// <param name = "angle">顺时针旋转角，单位为弧度。</param>
    public EllipseGeometry(PointD center, double radiusX, double radiusY, double angle = 0)
    {
        if (
            !PointD.Valid(radiusX)
            || !PointD.Valid(radiusY)
            || radiusX <= 0
            || radiusY <= 0
            || !PointD.Valid(angle)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX));
        }

        Center = center;
        RadiusX = radiusX;
        RadiusY = radiusY;
        Angle = angle;
        double c = Math.Cos(angle),
            s = Math.Sin(angle),
            x = Math.Sqrt(radiusX * radiusX * c * c + radiusY * radiusY * s * s),
            y = Math.Sqrt(radiusX * radiusX * s * s + radiusY * radiusY * c * c);
        Bounds = new RectD(center.X - x, center.Y - y, 2 * x, 2 * y);
    }

    /// <summary>椭圆中心的原图坐标。</summary>
    public PointD Center { get; }

    /// <summary>局部水平半径，单位为原图像素。</summary>
    public double RadiusX { get; }

    /// <summary>局部垂直半径，单位为原图像素。</summary>
    public double RadiusY { get; }

    /// <summary>顺时针旋转角，单位为弧度。</summary>
    public double Angle { get; }

    /// <inheritdoc/>
    public override RectD Bounds { get; }

    /// <inheritdoc/>
    public override bool Contains(PointD p, double tolerance = 0)
    {
        GeometryMath.ValidateTolerance(tolerance);
        double x = p.X - Center.X,
            y = p.Y - Center.Y,
            u = (x * Math.Cos(Angle) + y * Math.Sin(Angle)) / (RadiusX + tolerance),
            v = (-x * Math.Sin(Angle) + y * Math.Cos(Angle)) / (RadiusY + tolerance);
        return u * u + v * v <= 1;
    }
}
