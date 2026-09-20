using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>旋转矩形几何，使用原图坐标；旋转角为顺时针弧度。</summary>
public sealed class RectangleGeometry : Geometry
{
    /// <summary>以中心、局部尺寸和角度创建非空矩形。</summary>
    /// <param name = "center">矩形中心的原图坐标。</param>
    /// <param name = "width">旋转前局部宽度，必须大于0，单位为原图像素。</param>
    /// <param name = "height">旋转前局部高度，必须大于0，单位为原图像素。</param>
    /// <param name = "angle">顺时针旋转角，单位为弧度；0为轴对齐。</param>
    public RectangleGeometry(PointD center, double width, double height, double angle = 0)
    {
        if (
            !PointD.Valid(width)
            || !PointD.Valid(height)
            || width <= 0
            || height <= 0
            || !PointD.Valid(angle)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Center = center;
        Width = width;
        Height = height;
        Angle = angle;
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
            Center.X + x * Math.Cos(Angle) - y * Math.Sin(Angle),
            Center.Y + x * Math.Sin(Angle) + y * Math.Cos(Angle)
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
        double u = x * Math.Cos(Angle) + y * Math.Sin(Angle),
            v = -x * Math.Sin(Angle) + y * Math.Cos(Angle);
        return u >= -Width / 2 - tolerance
            && u < Width / 2 + tolerance
            && v >= -Height / 2 - tolerance
            && v < Height / 2 + tolerance;
    }
}
