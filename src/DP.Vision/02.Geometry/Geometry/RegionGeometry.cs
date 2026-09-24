using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision;

/// <summary>精确的栅格Region，保留孔洞、不连通部分和空对象。</summary>
public sealed class RegionGeometry : Geometry
{
    /// <summary>一个Region最多容纳的游程数。</summary>
    internal const int MaximumRuns = 2000000;

    /// <summary>复制有序且互不重叠的游程，允许空Region，不自行填孔或合并外接框。</summary>
    /// <param name = "runs">按行、起始列排序的非重叠游程，最多2000000条；构造时复制。</param>
    public RegionGeometry(IEnumerable<RegionRun> runs)
    {
        var copy = runs?.ToArray() ?? throw new ArgumentNullException(nameof(runs));
        if (copy.Length > MaximumRuns)
        {
            throw new ArgumentException("Too many runs.");
        }

        long area = 0;
        int left = int.MaxValue,
            right = int.MinValue;
        for (int i = 0; i < copy.Length; i++)
        {
            var r = copy[i];
            if (
                r.EndExclusive <= r.Start
                || i > 0
                    && (
                        r.Row < copy[i - 1].Row
                        || r.Row == copy[i - 1].Row && r.Start < copy[i - 1].EndExclusive
                    )
            )
            {
                throw new ArgumentException("Runs must be sorted and nonoverlapping.");
            }

            area += (long)r.EndExclusive - r.Start;
            left = Math.Min(left, r.Start);
            right = Math.Max(right, r.EndExclusive);
        }

        Runs = Array.AsReadOnly(copy);
        AreaPixels = area;
        Bounds =
            copy.Length == 0
                ? new RectD(0, 0, 0, 0)
                : new RectD(left, copy[0].Row, right - left, copy[copy.Length - 1].Row - copy[0].Row + 1);
    }

    /// <summary>精确的原始游程快照。</summary>
    public IReadOnlyList<RegionRun> Runs { get; }

    /// <summary>精确的原图像素面积，不是缩放后的屏幕面积。</summary>
    public long AreaPixels { get; }

    /// <inheritdoc/>
    public override RectD Bounds { get; }

    /// <inheritdoc/>
    public override bool Contains(PointD p, double tolerance = 0)
    {
        GeometryMath.ValidateTolerance(tolerance);
        int row = (int)Math.Floor(p.Y),
            col = (int)Math.Floor(p.X),
            lo = 0,
            hi = Runs.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            var r = Runs[mid];
            if (r.Row < row || r.Row == row && r.EndExclusive <= col)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo < Runs.Count && Runs[lo].Row == row && Runs[lo].Start <= col && col < Runs[lo].EndExclusive;
    }

    /// <inheritdoc/>
    public override long ElementCount => Runs.Count;

    /// <summary>按整像素平移；dx、dy四舍五入到最近整数（中点取偶），保持游程精确，不重采样。</summary>
    /// <param name="dx">X方向偏移，原图像素。</param>
    /// <param name="dy">Y方向偏移，原图像素。</param>
    /// <returns>新的Region。</returns>
    public override Geometry Translate(double dx, double dy)
    {
        int x = (int)Math.Round(dx),
            y = (int)Math.Round(dy);
        return new RegionGeometry(
            Runs.Select(run => new RegionRun(
                checked(run.Row + y),
                checked(run.Start + x),
                checked(run.EndExclusive + x)
            ))
        );
    }

    /// <summary>线性游程求交，保留孔洞；与原RegionAnalysisResult.Intersect逐游程相同，不合并相接游程。</summary>
    /// <param name="other">另一个Region。</param>
    /// <param name="token">取消。</param>
    /// <returns>精确交集。</returns>
    public RegionGeometry Intersect(RegionGeometry other, CancellationToken token = default)
    {
        if (other == null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        var runs = new List<RegionRun>();
        int i = 0,
            j = 0;
        while (i < Runs.Count && j < other.Runs.Count)
        {
            token.ThrowIfCancellationRequested();
            var x = Runs[i];
            var y = other.Runs[j];
            if (x.Row < y.Row)
            {
                i++;
                continue;
            }

            if (y.Row < x.Row)
            {
                j++;
                continue;
            }

            int left = Math.Max(x.Start, y.Start),
                right = Math.Min(x.EndExclusive, y.EndExclusive);
            if (right > left)
            {
                Add(runs, new RegionRun(x.Row, left, right));
            }

            if (x.EndExclusive <= y.EndExclusive)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        token.ThrowIfCancellationRequested();
        return new RegionGeometry(runs);
    }

    /// <summary>线性游程求并；同一行相交或相接的游程合并为一条。</summary>
    /// <param name="other">另一个Region。</param>
    /// <param name="token">取消。</param>
    /// <returns>精确并集。</returns>
    public RegionGeometry Union(RegionGeometry other, CancellationToken token = default)
    {
        if (other == null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        var runs = new List<RegionRun>();
        int i = 0,
            j = 0;
        while (i < Runs.Count || j < other.Runs.Count)
        {
            token.ThrowIfCancellationRequested();
            bool takeThis =
                j >= other.Runs.Count || i < Runs.Count && Precedes(Runs[i], other.Runs[j]);
            var next = takeThis ? Runs[i++] : other.Runs[j++];
            int last = runs.Count - 1;
            if (last >= 0 && runs[last].Row == next.Row && next.Start <= runs[last].EndExclusive)
            {
                if (next.EndExclusive > runs[last].EndExclusive)
                {
                    runs[last] = new RegionRun(next.Row, runs[last].Start, next.EndExclusive);
                }
            }
            else
            {
                Add(runs, next);
            }
        }

        token.ThrowIfCancellationRequested();
        return new RegionGeometry(runs);
    }

    /// <summary>线性游程求差：保留本Region中不属于另一个Region的像素；同一行相接的结果游程合并为一条。</summary>
    /// <param name="other">要去掉的Region。</param>
    /// <param name="token">取消。</param>
    /// <returns>精确差集。</returns>
    public RegionGeometry Subtract(RegionGeometry other, CancellationToken token = default)
    {
        if (other == null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        var runs = new List<RegionRun>();
        int j = 0;
        foreach (var run in Runs)
        {
            token.ThrowIfCancellationRequested();

            // 另一个Region的游程同样有序：跳过完全位于本游程之前的部分，j只增不减。
            while (
                j < other.Runs.Count
                && (
                    other.Runs[j].Row < run.Row
                    || other.Runs[j].Row == run.Row && other.Runs[j].EndExclusive <= run.Start
                )
            )
            {
                j++;
            }

            // 逐段扣除与本游程重叠的部分；不移动j，因为同一条游程可能还会切到本行下一条游程。
            int start = run.Start;
            for (int k = j; k < other.Runs.Count; k++)
            {
                var cut = other.Runs[k];
                if (cut.Row != run.Row || cut.Start >= run.EndExclusive)
                {
                    break;
                }

                if (cut.Start > start)
                {
                    AddMerged(runs, new RegionRun(run.Row, start, cut.Start));
                }

                start = Math.Max(start, cut.EndExclusive);
                if (start >= run.EndExclusive)
                {
                    break;
                }
            }

            if (start < run.EndExclusive)
            {
                AddMerged(runs, new RegionRun(run.Row, start, run.EndExclusive));
            }
        }

        token.ThrowIfCancellationRequested();
        return new RegionGeometry(runs);
    }

    private static bool Precedes(RegionRun a, RegionRun b)
    {
        return a.Row < b.Row || a.Row == b.Row && a.Start <= b.Start;
    }

    private static void AddMerged(List<RegionRun> runs, RegionRun run)
    {
        int last = runs.Count - 1;
        if (last >= 0 && runs[last].Row == run.Row && runs[last].EndExclusive == run.Start)
        {
            runs[last] = new RegionRun(run.Row, runs[last].Start, run.EndExclusive);
            return;
        }

        Add(runs, run);
    }

    private static void Add(List<RegionRun> runs, RegionRun run)
    {
        if (runs.Count >= MaximumRuns)
        {
            throw new InvalidOperationException("Region run budget exceeded.");
        }

        runs.Add(run);
    }
}
