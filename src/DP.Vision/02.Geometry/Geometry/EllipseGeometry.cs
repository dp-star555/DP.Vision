using System;

namespace DP.Vision;

/// <summary>圆或旋转椭圆几何。</summary>
public sealed class EllipseGeometry : Geometry
{
    private readonly double _cos;
    private readonly double _sin;

    /// <summary>用正半径创建椭圆；两个半径相等时为圆。</summary>
    /// <param name = "center">中心的原图坐标。</param>
    /// <param name = "radiusX">旋转前的水平半径，必须大于0，单位为原图像素。</param>
    /// <param name = "radiusY">旋转前的垂直半径，必须大于0，单位为原图像素。</param>
    /// <param name = "angle">顺时针旋转角，单位为弧度。</param>
    public EllipseGeometry(PointD center, double radiusX, double radiusY, double angle = 0)
    {
        if (!PointD.Valid(radiusX) || radiusX <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX));
        }

        if (!PointD.Valid(radiusY) || radiusY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusY));
        }

        if (!PointD.Valid(angle))
        {
            throw new ArgumentOutOfRangeException(nameof(angle));
        }

        Center = center;
        RadiusX = radiusX;
        RadiusY = radiusY;
        Angle = angle;
        _cos = Math.Cos(angle);
        _sin = Math.Sin(angle);
        double c = _cos,
            s = _sin,
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
            u = (x * _cos + y * _sin) / (RadiusX + tolerance),
            v = (-x * _sin + y * _cos) / (RadiusY + tolerance);
        return u * u + v * v <= 1;
    }

    /// <inheritdoc/>
    public override long ElementCount => 4;

    /// <inheritdoc/>
    public override Geometry Translate(double dx, double dy)
    {
        return new EllipseGeometry(new PointD(Center.X + dx, Center.Y + dy), RadiusX, RadiusY, Angle);
    }
}
