using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>有序亚像素轮廓，可表示XLD式轮廓、线、点或多边形；闭合不等于填充。</summary>
public sealed class ContourGeometry : Geometry
{
    /// <summary>复制原始点序列；填充轮廓必须闭合且至少有三个顶点。</summary>
    /// <param name = "points">按轮廓顺序排列的原图坐标点，最多2000000个；构造时复制。</param>
    /// <param name = "closed">是否连接最后一点与第一点。</param>
    /// <param name = "filled">是否按奇偶规则作为填充面积使用；false只表示轮廓，不隐式变成Region。</param>
    public ContourGeometry(IEnumerable<PointD> points, bool closed = false, bool filled = false)
    {
        var copy = points?.ToArray() ?? throw new ArgumentNullException(nameof(points));
        if (copy.Length > 2000000 || filled && (!closed || copy.Length < 3))
        {
            throw new ArgumentException("Invalid contour.");
        }

        Points = Array.AsReadOnly(copy);
        Closed = closed;
        Filled = filled;
        Bounds = copy.Length == 0 ? new RectD(0, 0, 0, 0) : GeometryMath.Bounds(Points);
    }

    /// <summary>不可变的原始点序列，不是显示简化点。</summary>
    public IReadOnlyList<PointD> Points { get; }

    /// <summary>是否连接首尾顶点。</summary>
    public bool Closed { get; }

    /// <summary>是否按奇偶填充规则判断面积成员关系，而非只判断轮廓线。</summary>
    public bool Filled { get; }

    /// <inheritdoc/>
    public override RectD Bounds { get; }

    /// <inheritdoc/>
    public override bool Contains(PointD p, double tolerance = 0)
    {
        GeometryMath.ValidateTolerance(tolerance);
        bool inside = false;
        for (int i = 0; i < Points.Count; i++)
        {
            var a = Points[i];
            var b = Points[(i + 1) % Points.Count];
            if (
                (i < Points.Count - 1 || Closed || Points.Count == 1)
                && GeometryMath.DistanceSquared(p, a, b) <= tolerance * tolerance
            )
            {
                return true;
            }

            if (Filled && ((a.Y > p.Y) != (b.Y > p.Y)) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return Filled && inside;
    }

    /// <summary>闭合轮廓是否在末尾重复了首点。两种写法都合法，编辑顶点时需要保持原写法。</summary>
    public bool RepeatsFirstPoint =>
        Closed
        && Points.Count > 1
        && Points[0].X == Points[Points.Count - 1].X
        && Points[0].Y == Points[Points.Count - 1].Y;

    /// <inheritdoc/>
    public override long ElementCount => Points.Count;

    /// <inheritdoc/>
    public override Geometry Translate(double dx, double dy)
    {
        return new ContourGeometry(Points.Select(p => new PointD(p.X + dx, p.Y + dy)), Closed, Filled);
    }
}
