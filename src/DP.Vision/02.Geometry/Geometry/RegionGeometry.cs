using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>精确的栅格Region，保留孔洞、不连通部分和空对象。</summary>
public sealed class RegionGeometry : Geometry
{
    /// <summary>复制有序且互不重叠的游程，允许空Region，不自行填孔或合并外接框。</summary>
    /// <param name = "runs">按行、起始列排序的非重叠游程，最多2000000条；构造时复制。</param>
    public RegionGeometry(IEnumerable<RegionRun> runs)
    {
        var copy = runs?.ToArray() ?? throw new ArgumentNullException(nameof(runs));
        if (copy.Length > 2000000)
        {
            throw new ArgumentException("Too many runs.");
        }

        long area = 0;
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
        }

        Runs = Array.AsReadOnly(copy);
        AreaPixels = area;
        Bounds =
            copy.Length == 0
                ? new RectD(0, 0, 0, 0)
                : new RectD(
                    copy.Min(r => r.Start),
                    copy[0].Row,
                    copy.Max(r => r.EndExclusive) - copy.Min(r => r.Start),
                    copy[copy.Length - 1].Row - copy[0].Row + 1
                );
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
}
