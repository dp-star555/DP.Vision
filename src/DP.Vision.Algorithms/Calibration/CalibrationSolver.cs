using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>纯数值标定实现，不依赖厂商SDK、UI或工作流；中心化和尺度归一降低大坐标偏移的影响。</summary>
public static class CalibrationSolver
{
    /// <summary>由至少三个非共线观测求解最小二乘仿射；不做离群点删除或精度合格判定。</summary>
    /// <param name="samples">源/目标观测，调用期间不得并发修改。</param>
    /// <param name="rotationSamples">可选目标坐标旋转轨迹；空集合表示未请求，非空至少三点。</param>
    /// <param name="token">协作取消令牌。</param>
    /// <returns>仿射系数、目标坐标RMS及可选旋转中心。</returns>
    public static AffineCalibration SolveAffine(IReadOnlyList<CalibrationSample> samples,
        IReadOnlyList<Coordinate2D>? rotationSamples = null, CancellationToken token = default)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        token.ThrowIfCancellationRequested();
        var copy = samples.ToArray();
        if (copy.Length < 3 || copy.Any(p => p == null))
            throw new ArgumentException("仿射标定需要至少三个非空观测。", nameof(samples));
        var source = copy.Select(p => p.Source).ToArray();
        var x = FitPlane(source, copy.Select(p => p.Target.X).ToArray(), token);
        var y = FitPlane(source, copy.Select(p => p.Target.Y).ToArray(), token);
        double squaredError = 0;
        foreach (var p in copy)
        {
            token.ThrowIfCancellationRequested();
            double dx = x[0] * p.Source.X + x[1] * p.Source.Y + x[2] - p.Target.X;
            double dy = y[0] * p.Source.X + y[1] * p.Source.Y + y[2] - p.Target.Y;
            squaredError += (dx * dx + dy * dy) / copy.Length;
        }
        Coordinate2D? center = rotationSamples != null && rotationSamples.Count > 0
            ? FitRotationCenter(rotationSamples, token) : (Coordinate2D?)null;
        return new AffineCalibration(x[0], x[1], x[2], y[0], y[1], y[2], Math.Sqrt(squaredError), center);
    }

    /// <summary>拟合圆轨迹的代数最小二乘中心；短弧或噪声仍需调用方依据实测精度验收。</summary>
    /// <param name="samples">目标坐标中的至少三个非共线旋转轨迹点。</param>
    /// <param name="token">协作取消令牌。</param>
    /// <returns>同一目标坐标系中的圆心。</returns>
    public static Coordinate2D FitRotationCenter(IReadOnlyList<Coordinate2D> samples, CancellationToken token = default)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        token.ThrowIfCancellationRequested();
        var copy = samples.ToArray();
        if (copy.Length < 3) throw new ArgumentException("旋转中心拟合至少需要三个点。", nameof(samples));
        double mx = copy.Average(p => p.X), my = copy.Average(p => p.Y);
        double scale = copy.Max(p => Math.Max(Math.Abs(p.X - mx), Math.Abs(p.Y - my)));
        if (scale == 0 || double.IsInfinity(scale)) throw new InvalidOperationException("旋转轨迹退化或数值范围过大。");
        var local = copy.Select(p => new Coordinate2D((p.X - mx) / scale, (p.Y - my) / scale)).ToArray();
        var rhs = local.Select(p => p.X * p.X + p.Y * p.Y).ToArray();
        var coefficients = FitPlane(local, rhs, token);
        return new Coordinate2D(mx + coefficients[0] * scale / 2, my + coefficients[1] * scale / 2);
    }

    private static double[] FitPlane(Coordinate2D[] points, double[] values, CancellationToken token)
    {
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y), mv = values.Average();
        double sx = points.Max(p => Math.Abs(p.X - mx)), sy = points.Max(p => Math.Abs(p.Y - my));
        if (sx == 0 || sy == 0 || double.IsInfinity(sx) || double.IsInfinity(sy))
            throw new InvalidOperationException("标定点退化或数值范围过大。");
        double xx = 0, xy = 0, yy = 0, xv = 0, yv = 0;
        for (int i = 0; i < points.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            double x = (points[i].X - mx) / sx, y = (points[i].Y - my) / sy, v = values[i] - mv;
            xx += x * x; xy += x * y; yy += y * y; xv += x * v; yv += y * v;
        }
        double determinant = xx * yy - xy * xy;
        if (double.IsNaN(determinant) || determinant <= 1e-12 * xx * yy)
            throw new InvalidOperationException("标定点共线或接近共线，无法稳定求解。");
        double a = (xv * yy - yv * xy) / determinant / sx;
        double b = (yv * xx - xv * xy) / determinant / sy;
        return new[] { a, b, mv - a * mx - b * my };
    }
}
