using System;

namespace DP.Vision;

/// <summary>Region像素游程，右端列为排他端点；与HALCON包含右端像素的约定不同。</summary>
public readonly struct RegionRun : IEquatable<RegionRun>
{
    /// <summary>创建非空游程，坐标绝对值不得超过<see cref="ImageInfo.MaxDimension"/>，因此任何合法原图的整行都能表示。</summary>
    /// <param name = "row">游程所在的原图像素行。</param>
    /// <param name = "start">首个包含的像素列。</param>
    /// <param name = "endExclusive">首个不包含的像素列，必须大于start。</param>
    public RegionRun(int row, int start, int endExclusive)
    {
        if (Math.Abs((long)row) > ImageInfo.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if (Math.Abs((long)start) > ImageInfo.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (Math.Abs((long)endExclusive) > ImageInfo.MaxDimension || endExclusive <= start)
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

    /// <summary>行、起始列与排他右端都相等时相等。</summary>
    /// <param name="other">另一个值。</param>
    /// <returns>是否相等。</returns>
    public bool Equals(RegionRun other)
    {
        return Row == other.Row && Start == other.Start && EndExclusive == other.EndExclusive;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is RegionRun other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Row.GetHashCode();
            hash = (hash * 397) ^ Start.GetHashCode();
            hash = (hash * 397) ^ EndExclusive.GetHashCode();
            return hash;
        }
    }

    /// <summary>值相等比较。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否相等。</returns>
    public static bool operator ==(RegionRun left, RegionRun right)
    {
        return left.Equals(right);
    }

    /// <summary>值不等比较。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>是否不等。</returns>
    public static bool operator !=(RegionRun left, RegionRun right)
    {
        return !left.Equals(right);
    }
}
