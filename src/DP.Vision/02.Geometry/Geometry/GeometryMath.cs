using System;
using System.Collections.Generic;

namespace DP.Vision;

internal static class GeometryMath
{
    internal static void ValidateTolerance(double tolerance)
    {
        if (!PointD.Valid(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }
    }

    internal static RectD Bounds(IReadOnlyList<PointD> points)
    {
        double left = double.PositiveInfinity,
            top = double.PositiveInfinity,
            right = double.NegativeInfinity,
            bottom = double.NegativeInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            left = Math.Min(left, p.X);
            top = Math.Min(top, p.Y);
            right = Math.Max(right, p.X);
            bottom = Math.Max(bottom, p.Y);
        }

        return new RectD(left, top, right - left, bottom - top);
    }

    internal static double DistanceSquared(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X,
            dy = b.Y - a.Y,
            len = dx * dx + dy * dy,
            t = len == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len));
        double x = p.X - a.X - t * dx,
            y = p.Y - a.Y - t * dy;
        return x * x + y * y;
    }
}
