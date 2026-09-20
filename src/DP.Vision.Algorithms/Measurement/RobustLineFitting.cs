using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>鲁棒直线拟合的端点、内点索引和残差，不拥有图像。</summary>
public sealed class RobustLineResult
{
    internal RobustLineResult(string frameId, PointD a, PointD b, int[] indices, double rms)
    { FrameId = frameId; A = a; B = b; InlierIndices = Array.AsReadOnly((int[])indices.Clone()); RmsError = rms; }
    /// <summary>定位来源；未绑定时为空。</summary>
    public LocatedCoordinateSystem? CoordinateSystem { get; private set; }
    /// <summary>A端的双坐标。</summary>
    public LocatedPoint? LocatedA => CoordinateSystem?.Locate(A);
    /// <summary>B端的双坐标。</summary>
    public LocatedPoint? LocatedB => CoordinateSystem?.Locate(B);
    /// <summary>模板局部像素单位RMS；未绑定时为空。</summary>
    public double? LocalRmsError => CoordinateSystem is null ? (double?)null : RmsError / CoordinateSystem.Pose.Scale;
    /// <summary>附加同帧坐标表达。</summary><param name="system">定位。</param><returns>独立结果。</returns>
    public RobustLineResult InCoordinates(LocatedCoordinateSystem system)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (system.FrameId != FrameId) throw new InvalidOperationException("Result coordinate frame mismatch.");
        var copy = (RobustLineResult)MemberwiseClone(); copy.CoordinateSystem = system; return copy;
    }
    /// <summary>输入点所属帧。</summary>
    public string FrameId { get; }
    /// <summary>内点投影最小端。</summary>
    public PointD A { get; }
    /// <summary>内点投影最大端。</summary>
    public PointD B { get; }
    /// <summary>在输入点序列中的内点索引。</summary>
    public IReadOnlyList<int> InlierIndices { get; }
    /// <summary>内点数。</summary>
    public int InlierCount => InlierIndices.Count;
    /// <summary>内点正交距离RMS，像素。</summary>
    public double RmsError { get; }
    /// <summary>完成状态，不是产品判定。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
}

/// <summary>确定性、有预算的RANSAC直线拟合能力。</summary>
public interface IRobustLineFitter
{
    /// <summary>两点采样RANSAC后正交TLS重拟合；拒绝重合、证据不足和不收敛的内点集合。</summary>
    /// <param name="frameId">来源帧。</param><param name="points">原图坐标点，最多8192个。</param>
    /// <param name="distanceThreshold">内点距离阈值，像素。</param><param name="iterations">采样次数1..1024，总距离评估不超过400万。</param>
    /// <param name="minimumInliers">最少内点数，至少3。</param><param name="token">取消。</param><returns>完整内点证据。</returns>
    RobustLineResult Fit(string frameId, IReadOnlyList<PointD> points, double distanceThreshold = .5, int iterations = 256, int minimumInliers = 3, CancellationToken token = default);
}

/// <summary>不依赖SDK的稳健直线拟合，固定随机种子，不宣称圆/圆弧拟合。</summary>
public sealed class RobustLineFitter : IRobustLineFitter
{
    /// <inheritdoc/>
    public RobustLineResult Fit(string frameId, IReadOnlyList<PointD> points, double distanceThreshold = .5, int iterations = 256, int minimumInliers = 3, CancellationToken token = default)
    {
        if (points == null) throw new ArgumentNullException(nameof(points));
        if (string.IsNullOrWhiteSpace(frameId) || points.Count < 3 || points.Count > 8192 || iterations < 1 || iterations > 1024
            || (long)points.Count * iterations > 4000000 || minimumInliers < 3 || minimumInliers > points.Count
            || double.IsNaN(distanceThreshold) || double.IsInfinity(distanceThreshold) || distanceThreshold <= 0 || distanceThreshold > 1000000)
            throw new ArgumentException("Invalid robust fit input or budget.");
        token.ThrowIfCancellationRequested();
        var copy = points.ToArray();
        if (copy.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y) || Math.Abs(p.X) > 1e9 || Math.Abs(p.Y) > 1e9)) throw new ArgumentException("Non-finite or excessive coordinates.");
        uint seed = 1; int[] best = Array.Empty<int>(); double bestError = double.PositiveInfinity;
        for (int pass = 0; pass < iterations; pass++)
        {
            token.ThrowIfCancellationRequested();
            int i = Next(ref seed, copy.Length), j = Next(ref seed, copy.Length);
            if (i == j) continue;
            double dx = copy[j].X - copy[i].X, dy = copy[j].Y - copy[i].Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            var indices = Classify(copy, copy[i], dx / length, dy / length, distanceThreshold, token, out var error);
            if (indices.Length > best.Length || indices.Length == best.Length && error < bestError) { best = indices; bestError = error; }
            if (best.Length == copy.Length) break;
        }
        if (best.Length < minimumInliers) throw new InvalidOperationException("Insufficient line inliers.");
        for (int refinement = 0; refinement < 16; refinement++)
        {
            token.ThrowIfCancellationRequested();
            double x = 0, y = 0;
            foreach (int i in best) { x += copy[i].X; y += copy[i].Y; }
            var center = new PointD(x / best.Length, y / best.Length);
            double xx = 0, xy = 0, yy = 0;
            foreach (int i in best) { x = copy[i].X - center.X; y = copy[i].Y - center.Y; xx += x * x; xy += x * y; yy += y * y; }
            if (xx + yy < 1e-12) throw new InvalidOperationException("Coincident line observations.");
            if (Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy) <= 1e-12 * (xx + yy))
                throw new InvalidOperationException("Line direction is not identifiable from isotropic observations.");
            double angle = .5 * Math.Atan2(2 * xy, xx - yy), dx = Math.Cos(angle), dy = Math.Sin(angle);
            var next = Classify(copy, center, dx, dy, distanceThreshold, token, out var error);
            if (next.Length < minimumInliers) throw new InvalidOperationException("Insufficient refined line inliers.");
            if (next.SequenceEqual(best))
            {
                var projections = best.Select(i => (copy[i].X - center.X) * dx + (copy[i].Y - center.Y) * dy).ToArray();
                double min = projections.Min(), max = projections.Max();
                return new RobustLineResult(frameId, new PointD(center.X + dx * min, center.Y + dy * min),
                    new PointD(center.X + dx * max, center.Y + dy * max), best, Math.Sqrt(error / best.Length));
            }
            best = next;
        }
        throw new InvalidOperationException("Line consensus did not stabilize within refinement budget.");
    }

    private static int Next(ref uint seed, int count) { seed = unchecked(seed * 1664525u + 1013904223u); return (int)((seed >> 8) % (uint)count); }
    private static int[] Classify(PointD[] points, PointD origin, double dx, double dy, double threshold, CancellationToken token, out double error)
    {
        var indices = new List<int>(); error = 0;
        for (int i = 0; i < points.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            double d = (points[i].X - origin.X) * dy - (points[i].Y - origin.Y) * dx;
            if (Math.Abs(d) <= threshold) { indices.Add(i); error += d * d; }
        }
        return indices.ToArray();
    }
}
