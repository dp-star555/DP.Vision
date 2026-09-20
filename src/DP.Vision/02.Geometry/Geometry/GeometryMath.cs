using System;
using System.Collections.Generic;
using System.Linq;

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
        double x = points.Min(p => p.X),
            y = points.Min(p => p.Y);
        return new RectD(x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y);
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
