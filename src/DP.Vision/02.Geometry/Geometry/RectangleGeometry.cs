using System;
using System.Collections.Generic;

namespace DP.Vision;

/// <summary>旋转矩形几何，使用原图坐标；旋转角为顺时针弧度。</summary>
public sealed class RectangleGeometry : Geometry
{
    private readonly double _cos;
    private readonly double _sin;

    /// <summary>以中心、局部尺寸和角度创建非空矩形。</summary>
    /// <param name = "center">矩形中心的原图坐标。</param>
    /// <param name = "width">旋转前局部宽度，必须大于0，单位为原图像素。</param>
    /// <param name = "height">旋转前局部高度，必须大于0，单位为原图像素。</param>
    /// <param name = "angle">顺时针旋转角，单位为弧度；0为轴对齐。</param>
    public RectangleGeometry(PointD center, double width, double height, double angle = 0)
    {
        if (!PointD.Valid(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (!PointD.Valid(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (!PointD.Valid(angle))
        {
            throw new ArgumentOutOfRangeException(nameof(angle));
        }

        Center = center;
        Width = width;
        Height = height;
        Angle = angle;
        _cos = Math.Cos(angle);
        _sin = Math.Sin(angle);
        Corners = Array.AsReadOnly(
            new[]
            {
                Transform(-width / 2, -height / 2),
                Transform(width / 2, -height / 2),
                Transform(width / 2, height / 2),
                Transform(-width / 2, height / 2),
            }
        );
        Bounds = GeometryMath.Bounds(Corners);
    }

    private PointD Transform(double x, double y)
    {
        return new PointD(
            Center.X + x * _cos - y * _sin,
            Center.Y + x * _sin + y * _cos
        );
    }

    /// <summary>矩形中心的原图坐标。</summary>
    public PointD Center { get; }

    /// <summary>旋转前局部宽度，单位为像素。</summary>
    public double Width { get; }

    /// <summary>旋转前局部高度，单位为像素。</summary>
    public double Height { get; }

    /// <summary>顺时针旋转角，单位为弧度。</summary>
    public double Angle { get; }

    /// <summary>按边界顺序排列的四个原图顶点。</summary>
    public IReadOnlyList<PointD> Corners { get; }

    /// <inheritdoc/>
    public override RectD Bounds { get; }

    /// <inheritdoc/>
    public override bool Contains(PointD p, double tolerance = 0)
    {
        GeometryMath.ValidateTolerance(tolerance);
        double x = p.X - Center.X,
            y = p.Y - Center.Y;
        double u = x * _cos + y * _sin,
            v = -x * _sin + y * _cos;
        return u >= -Width / 2 - tolerance
            && u < Width / 2 + tolerance
            && v >= -Height / 2 - tolerance
            && v < Height / 2 + tolerance;
    }

    /// <inheritdoc/>
    public override long ElementCount => 4;

    /// <inheritdoc/>
    public override Geometry Translate(double dx, double dy)
    {
        return new RectangleGeometry(new PointD(Center.X + dx, Center.Y + dy), Width, Height, Angle);
    }
}
