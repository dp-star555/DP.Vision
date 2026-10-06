using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>鲁棒圆拟合的圆心、半径、内点索引和残差，不拥有图像。</summary>
public sealed class RobustCircleResult
{
    internal RobustCircleResult(string frameId, PointD center, double radius, int[] indices, double rms)
    { FrameId = frameId; Center = center; Radius = radius; InlierIndices = Array.AsReadOnly((int[])indices.Clone()); RmsError = rms; }
    /// <summary>定位来源；未绑定时为空。</summary>
    public VisionCoordinateSystem? CoordinateSystem { get; private set; }
    /// <summary>带帧和坐标来源的圆心，可直接连接后续几何节点。</summary>
    public VisionPoint MeasuredCenter => new VisionPoint(FrameId, Center, CoordinateSystem);
    /// <summary>圆心的双坐标；未绑定时为空。</summary>
    public LocatedPoint? LocatedCenter => CoordinateSystem?.Locate(Center);
    /// <summary>局部坐标单位半径；未绑定或非相似变换时为空。</summary>
    public double? LocalRadius => CoordinateSystem is null || !CoordinateSystem.IsSimilarity ? (double?)null : Radius / CoordinateSystem.SimilarityScale;
    /// <summary>局部坐标单位RMS；未绑定或非相似变换时为空。</summary>
    public double? LocalRmsError => CoordinateSystem is null || !CoordinateSystem.IsSimilarity ? (double?)null : RmsError / CoordinateSystem.SimilarityScale;
    /// <summary>附加同帧坐标表达。</summary><param name="system">定位。</param><returns>独立结果。</returns>
    public RobustCircleResult InCoordinates(VisionCoordinateSystem system)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (system.FrameId != FrameId) throw new InvalidOperationException("圆拟合结果与坐标系不是同一帧。");
        var copy = (RobustCircleResult)MemberwiseClone(); copy.CoordinateSystem = system; return copy;
    }
    /// <summary>输入点所属帧。</summary>
    public string FrameId { get; }
    /// <summary>圆心，原图像素边界坐标。</summary>
    public PointD Center { get; }
    /// <summary>半径，原图像素。</summary>
    public double Radius { get; }
    /// <summary>直径，原图像素。</summary>
    public double Diameter => 2 * Radius;
    /// <summary>在输入点序列中的内点索引。</summary>
    public IReadOnlyList<int> InlierIndices { get; }
    /// <summary>内点数。</summary>
    public int InlierCount => InlierIndices.Count;
    /// <summary>内点径向距离RMS，像素。</summary>
    public double RmsError { get; }
    /// <summary>完成状态，不是产品判定。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
}

/// <summary>确定性、有预算的RANSAC圆拟合能力。</summary>
[VisionCapability("measurement.robust-circle", "测量", "鲁棒圆拟合")]
public interface IRobustCircleFitter
{
    /// <summary>三点采样RANSAC后对内点代数拟合并几何细化；拒绝共线、证据不足和不收敛的内点集合。</summary>
    /// <param name="frameId">来源帧。</param><param name="points">原图坐标点，最多8192个。</param>
    /// <param name="distanceThreshold">内点径向距离阈值，像素。</param><param name="iterations">采样次数1..1024，总距离评估不超过400万。</param>
    /// <param name="minimumInliers">最少内点数，至少3。</param><param name="token">取消。</param><returns>完整内点证据。</returns>
    RobustCircleResult Fit(string frameId, IReadOnlyList<PointD> points, double distanceThreshold = .5, int iterations = 256, int minimumInliers = 3, CancellationToken token = default);
}

/// <summary>不依赖SDK的稳健圆拟合，固定随机种子。</summary>
public sealed class RobustCircleFitter : IRobustCircleFitter
{
    /// <inheritdoc/>
    public RobustCircleResult Fit(string frameId, IReadOnlyList<PointD> points, double distanceThreshold = .5, int iterations = 256, int minimumInliers = 3, CancellationToken token = default)
    {
        if (points == null) throw new ArgumentNullException(nameof(points));
        if (string.IsNullOrWhiteSpace(frameId) || points.Count < 3 || points.Count > 8192 || iterations < 1 || iterations > 1024
            || (long)points.Count * iterations > 4000000 || minimumInliers < 3 || minimumInliers > points.Count
            || double.IsNaN(distanceThreshold) || double.IsInfinity(distanceThreshold) || distanceThreshold <= 0 || distanceThreshold > 1000000)
            throw new ArgumentException("圆拟合输入或预算无效：至少3个点、最多8192个点，迭代1..1024，阈值为正。");
        token.ThrowIfCancellationRequested();
        var copy = points.ToArray();
        if (copy.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y) || Math.Abs(p.X) > 1e9 || Math.Abs(p.Y) > 1e9)) throw new ArgumentException("圆拟合点坐标非有限或过大。");
        uint seed = 1; int[] best = Array.Empty<int>(); double bestError = double.PositiveInfinity;
        for (int pass = 0; pass < iterations; pass++)
        {
            token.ThrowIfCancellationRequested();
            int i = Next(ref seed, copy.Length), j = Next(ref seed, copy.Length), k = Next(ref seed, copy.Length);
            if (i == j || j == k || i == k || !Circumcircle(copy[i], copy[j], copy[k], out var center, out var radius)) continue;
            var indices = Classify(copy, center, radius, distanceThreshold, token, out var error);
            if (indices.Length > best.Length || indices.Length == best.Length && error < bestError) { best = indices; bestError = error; }
            if (best.Length == copy.Length) break;
        }
        if (best.Length < minimumInliers) throw new InvalidOperationException($"圆的内点不足：找到 {best.Length} 个，至少需要 {minimumInliers} 个。");
        for (int refinement = 0; refinement < 16; refinement++)
        {
            token.ThrowIfCancellationRequested();
            var (center, radius) = Refine(copy, best);
            var next = Classify(copy, center, radius, distanceThreshold, token, out var error);
            if (next.Length < minimumInliers) throw new InvalidOperationException($"细化后圆的内点不足：{next.Length} 个，至少需要 {minimumInliers} 个。");
            if (next.SequenceEqual(best)) return new RobustCircleResult(frameId, center, radius, best, Math.Sqrt(error / best.Length));
            best = next;
        }
        throw new InvalidOperationException("圆拟合的内点集合在细化预算内没有稳定。");
    }

    private static int Next(ref uint seed, int count) { seed = unchecked(seed * 1664525u + 1013904223u); return (int)((seed >> 8) % (uint)count); }

    private static bool Circumcircle(PointD a, PointD b, PointD c, out PointD center, out double radius)
    {
        double bx = b.X - a.X, by = b.Y - a.Y, cx = c.X - a.X, cy = c.Y - a.Y, d = 2 * (bx * cy - by * cx);
        double scale = Math.Max(bx * bx + by * by, cx * cx + cy * cy);
        center = default; radius = 0;
        if (scale < 1e-18 || Math.Abs(d) <= 1e-9 * scale) return false;
        double b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
        double ux = (cy * b2 - by * c2) / d, uy = (bx * c2 - cx * b2) / d;
        center = new PointD(a.X + ux, a.Y + uy); radius = Math.Sqrt(ux * ux + uy * uy);
        return true;
    }

    private static int[] Classify(PointD[] points, PointD center, double radius, double threshold, CancellationToken token, out double error)
    {
        var indices = new List<int>(); error = 0;
        for (int i = 0; i < points.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            double dx = points[i].X - center.X, dy = points[i].Y - center.Y, d = Math.Sqrt(dx * dx + dy * dy) - radius;
            if (Math.Abs(d) <= threshold) { indices.Add(i); error += d * d; }
        }
        return indices.ToArray();
    }

    // 先以内点质心为原点做代数（Kåsa）拟合得到初值，再用高斯-牛顿最小化径向距离平方和。
    private static (PointD Center, double Radius) Refine(PointD[] points, int[] inliers)
    {
        double mx = inliers.Average(i => points[i].X), my = inliers.Average(i => points[i].Y);
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0; int n = inliers.Length;
        foreach (int i in inliers)
        {
            double x = points[i].X - mx, y = points[i].Y - my, z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; sxz += x * z; syz += y * z; sz += z;
        }
        var abc = Solve(new[,] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } }, new[] { -sxz, -syz, -sz })
            ?? throw new InvalidOperationException("圆拟合退化：内点共线或重合。");
        double a = -abc[0] / 2, b = -abc[1] / 2, r2 = a * a + b * b - abc[2];
        if (r2 <= 0) throw new InvalidOperationException("圆拟合退化：内点共线或重合。");
        double r = Math.Sqrt(r2);
        for (int step = 0; step < 20; step++)
        {
            double jaa = 0, jab = 0, jar = 0, jbb = 0, jbr = 0, jrr = 0, ga = 0, gb = 0, gr = 0;
            foreach (int i in inliers)
            {
                double x = points[i].X - mx - a, y = points[i].Y - my - b, d = Math.Sqrt(x * x + y * y);
                if (d < 1e-12) continue;
                double da = -x / d, db = -y / d, residual = d - r;
                jaa += da * da; jab += da * db; jar -= da; jbb += db * db; jbr -= db; jrr += 1;
                ga += da * residual; gb += db * residual; gr -= residual;
            }
            var delta = Solve(new[,] { { jaa, jab, jar }, { jab, jbb, jbr }, { jar, jbr, jrr } }, new[] { -ga, -gb, -gr });
            if (delta == null) break;
            a += delta[0]; b += delta[1]; r += delta[2];
            if (Math.Abs(delta[0]) + Math.Abs(delta[1]) + Math.Abs(delta[2]) < 1e-12 * Math.Max(1, r)) break;
        }
        if (!(r > 0) || double.IsInfinity(r)) throw new InvalidOperationException("圆拟合没有收敛到有效半径。");
        return (new PointD(mx + a, my + b), r);
    }

    private static double[]? Solve(double[,] m, double[] v)
    {
        // 3×3 部分主元高斯消元；奇异时返回空。
        var a = (double[,])m.Clone(); var b = (double[])v.Clone();
        double norm = 0; foreach (var value in a) norm = Math.Max(norm, Math.Abs(value));
        if (norm == 0) return null;
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 3; row++) if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) <= 1e-12 * norm) return null;
            if (pivot != col)
            {
                for (int k = 0; k < 3; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (int row = col + 1; row < 3; row++)
            {
                double f = a[row, col] / a[col, col];
                for (int k = col; k < 3; k++) a[row, k] -= f * a[col, k];
                b[row] -= f * b[col];
            }
        }
        var x = new double[3];
        for (int row = 2; row >= 0; row--)
        {
            double s = b[row];
            for (int k = row + 1; k < 3; k++) s -= a[row, k] * x[k];
            x[row] = s / a[row, row];
        }
        return x;
    }
}
