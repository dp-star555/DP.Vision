using System;

namespace DP.Vision;

/// <summary>Region像素游程，右端列为排他端点；与HALCON包含右端像素的约定不同。</summary>
public readonly struct RegionRun
{
    /// <summary>创建非空游程，坐标绝对值不得超过1000000。</summary>
    /// <param name = "row">游程所在的原图像素行。</param>
    /// <param name = "start">首个包含的像素列。</param>
    /// <param name = "endExclusive">首个不包含的像素列，必须大于start。</param>
    public RegionRun(int row, int start, int endExclusive)
    {
        if (
            Math.Abs((long)row) > 1000000
            || Math.Abs((long)start) > 1000000
            || Math.Abs((long)endExclusive) > 1000000
            || endExclusive <= start
        )
        {
            throw new ArgumentOutOfRangeException(nameof(endExclusive));
        }

        Row = row;
        Start = start;
        EndExclusive = endExclusive;
    }

    /// <summary>原图像素行。</summary>
    public int Row { get; }

    /// <summary>首个包含的像素列。</summary>
    public int Start { get; }

    /// <summary>右端首个排除的像素列。</summary>
    public int EndExclusive { get; }
}
