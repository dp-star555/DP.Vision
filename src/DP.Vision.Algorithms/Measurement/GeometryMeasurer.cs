using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>纯托管几何计算；显式校验帧、坐标定义及距离空间。</summary>
public sealed class GeometryMeasurer : IGeometryMeasurer
{
    /// <inheritdoc/>
    public VisionLine GenerateLine(VisionPoint a, VisionPoint b, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return new VisionLine(a, b); }

    /// <inheritdoc/>
    public GeometricDistanceResult PointToPoint(VisionPoint a, VisionPoint b, EVisionCoordinateSpace space = EVisionCoordinateSpace.Image, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (a == null || b == null) throw new ArgumentNullException(nameof(a));
        Validate(a, b, space, EVisionLineDistanceMode.InfiniteLines);
        return Result(a.Position(space), b.Position(space), a, space, EVisionLineDistanceMode.InfiniteLines, EVisionDistanceKind.PointToPoint);
    }

    /// <inheritdoc/>
    public GeometricDistanceResult PointToLine(VisionPoint point, VisionLine line, EVisionCoordinateSpace space = EVisionCoordinateSpace.Image,
        EVisionLineDistanceMode mode = EVisionLineDistanceMode.InfiniteLines, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (point == null || line == null) throw new ArgumentNullException(nameof(point));
        Validate(point, line.A, space, mode);
        var position = point.Position(space);
        var foot = Project(position, line.A.Position(space), line.B.Position(space), mode == EVisionLineDistanceMode.Segments);
        return Result(position, foot, point, space, mode, EVisionDistanceKind.PointToLine);
    }

    /// <inheritdoc/>
    public GeometricDistanceResult LineToLine(VisionLine a, VisionLine b, EVisionCoordinateSpace space = EVisionCoordinateSpace.Image,
        EVisionLineDistanceMode mode = EVisionLineDistanceMode.InfiniteLines, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (a == null || b == null) throw new ArgumentNullException(nameof(a));
        Validate(a.A, b.A, space, mode);
        var p = a.A.Position(space); var q = b.A.Position(space);
        var aEnd = a.B.Position(space); var bEnd = b.B.Position(space);
        var r = Subtract(aEnd, p); var s = Subtract(bEnd, q);
        var cross = Cross(r, s);
        // 无限直线的数值平行判定使用无量纲角度容差，避免旋转后的舍入噪声制造遥远交点。
        if (mode == EVisionLineDistanceMode.InfiniteLines
            && Math.Abs(cross) <= 1e-12 * Math.Sqrt((r.X * r.X + r.Y * r.Y) * (s.X * s.X + s.Y * s.Y))) cross = 0;
        if (cross != 0)
        {
            var t = Cross(Subtract(q, p), s) / cross; var u = Cross(Subtract(q, p), r) / cross;
            if (mode == EVisionLineDistanceMode.InfiniteLines || t >= 0 && t <= 1 && u >= 0 && u <= 1)
            {
                var intersection = new PointD(p.X + t * r.X, p.Y + t * r.Y);
                return Result(intersection, intersection, a.A, space, mode);
            }
        }
        if (mode == EVisionLineDistanceMode.InfiniteLines)
            return Result(p, Project(p, q, bEnd, false), a.A, space, mode);
        var first = Result(p, Project(p, q, bEnd, true), a.A, space, mode);
        var candidate = Result(aEnd, Project(aEnd, q, bEnd, true), a.A, space, mode);
        if (candidate.Distance < first.Distance) first = candidate;
        candidate = Result(Project(q, p, aEnd, true), q, a.A, space, mode);
        if (candidate.Distance < first.Distance) first = candidate;
        candidate = Result(Project(bEnd, p, aEnd, true), bEnd, a.A, space, mode);
        return candidate.Distance < first.Distance ? candidate : first;
    }

    private static void Validate(VisionPoint a, VisionPoint b, EVisionCoordinateSpace space, EVisionLineDistanceMode mode)
    {
        if (!Enum.IsDefined(typeof(EVisionCoordinateSpace), space) || !Enum.IsDefined(typeof(EVisionLineDistanceMode), mode)) throw new ArgumentException("测量空间或距离模式无效。");
        if (a.FrameId != b.FrameId) throw new InvalidOperationException("禁止混合不同帧的几何事实。");
        if (!VisionPoint.SameCoordinates(a.CoordinateSystem, b.CoordinateSystem))
            throw new InvalidOperationException("几何事实的坐标来源不同，请先显式转换。");
        if (space == EVisionCoordinateSpace.TemplateLocal && a.CoordinateSystem == null)
            throw new InvalidOperationException("局部距离必须有共同的定位坐标系。");
    }
    private static GeometricDistanceResult Result(PointD a, PointD b, VisionPoint source, EVisionCoordinateSpace space, EVisionLineDistanceMode mode,
        EVisionDistanceKind kind = EVisionDistanceKind.LineToLine) =>
        new GeometricDistanceResult(VisionPoint.Create(source.FrameId, a, space, source.CoordinateSystem), VisionPoint.Create(source.FrameId, b, space, source.CoordinateSystem), space, mode, kind);
    private static PointD Project(PointD point, PointD a, PointD b, bool clamp)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / (dx * dx + dy * dy);
        if (clamp) t = Math.Max(0, Math.Min(1, t));
        return new PointD(a.X + t * dx, a.Y + t * dy);
    }
    private static PointD Subtract(PointD a, PointD b) => new PointD(a.X - b.X, a.Y - b.Y);
    private static double Cross(PointD a, PointD b) => a.X * b.Y - a.Y * b.X;
}
