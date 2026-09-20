using System;
using System.Threading;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>像素单元面积矩的等效椭圆和四邻域栅格周长；不是亚像素轮廓特征。</summary>
public sealed class BlobShapeFeatures
{
    internal BlobShapeFeatures(RegionGeometry region, PointD centroid, CancellationToken token)
    {
        double xx = 0, yy = 0, xy = 0; long perimeter = 0; int previous = 0;
        for (int i = 0; i < region.Runs.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var r = region.Runs[i]; double n = (long)r.EndExclusive - r.Start;
            double x = (r.Start + (double)r.EndExclusive) / 2 - centroid.X, y = r.Row + .5 - centroid.Y;
            xx += n * (x * x + n * n / 12); yy += n * (y * y + 1d / 12); xy += n * x * y;
            perimeter += 2 + 2 * ((long)r.EndExclusive - r.Start);
            if (i > 0 && region.Runs[i - 1].Row == r.Row && region.Runs[i - 1].EndExclusive == r.Start) perimeter -= 2;
            while (previous < i && (long)region.Runs[previous].Row < (long)r.Row - 1) previous++;
            while (previous < i && (long)region.Runs[previous].Row == (long)r.Row - 1 && region.Runs[previous].EndExclusive <= r.Start) previous++;
            for (int k = previous; k < i && (long)region.Runs[k].Row == (long)r.Row - 1; k++)
            {
                var p = region.Runs[k];
                if (p.Start >= r.EndExclusive) break;
                perimeter -= 2 * Math.Max(0L, (long)Math.Min(p.EndExclusive, r.EndExclusive) - Math.Max(p.Start, r.Start));
            }
        }
        xx /= region.AreaPixels; yy /= region.AreaPixels; xy /= region.AreaPixels;
        double delta = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
        MajorAxisLength = 4 * Math.Sqrt(Math.Max(1d / 12, (xx + yy + delta) / 2));
        MinorAxisLength = 4 * Math.Sqrt(Math.Max(1d / 12, (xx + yy - delta) / 2));
        OrientationRadians = delta <= 1e-12 * Math.Max(1, xx + yy) ? 0 : .5 * Math.Atan2(2 * xy, xx - yy);
        GridPerimeter = perimeter; Circularity = 4 * Math.PI * region.AreaPixels / ((double)perimeter * perimeter);
    }
    /// <summary>像素单元边界总长，包含孔洞边界；相邻单元共边不计。</summary>
    public long GridPerimeter { get; }
    /// <summary>4π面积/栅格周长²，受栅格方向偏差影响，单像素为π/4。</summary>
    public double Circularity { get; }
    /// <summary>面积矩等效椭圆长轴全长，像素。</summary>
    public double MajorAxisLength { get; }
    /// <summary>面积矩等效椭圆短轴全长，像素。</summary>
    public double MinorAxisLength { get; }
    /// <summary>图像坐标长轴方向，弧度；各向同性为0。</summary>
    public double OrientationRadians { get; }
    /// <summary>长短轴比。</summary>
    public double Elongation => MajorAxisLength / MinorAxisLength;
}

/// <summary>显式Blob事实筛选范围，全部为闭区间。</summary>
public sealed class BlobSelectionOptions
{
    /// <summary>创建面积、圆度和伸长比条件。</summary>
    /// <param name="minimumArea">最小面积。</param><param name="maximumArea">最大面积。</param>
    /// <param name="minimumCircularity">最小栅格圆度。</param><param name="maximumElongation">最大长短轴比。</param>
    public BlobSelectionOptions(long minimumArea = 1, long maximumArea = long.MaxValue, double minimumCircularity = 0, double maximumElongation = double.MaxValue)
    {
        if (minimumArea < 1 || maximumArea < minimumArea || double.IsNaN(minimumCircularity) || minimumCircularity < 0 || minimumCircularity > 1
            || double.IsNaN(maximumElongation) || double.IsInfinity(maximumElongation) || maximumElongation < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumArea));
        MinimumArea = minimumArea; MaximumArea = maximumArea; MinimumCircularity = minimumCircularity; MaximumElongation = maximumElongation;
    }
    /// <summary>最小面积。</summary>
    public long MinimumArea { get; }
    /// <summary>最大面积。</summary>
    public long MaximumArea { get; }
    /// <summary>最小栅格圆度。</summary>
    public double MinimumCircularity { get; }
    /// <summary>最大轴比。</summary>
    public double MaximumElongation { get; }
}

/// <summary>Blob事实选择能力。</summary>
public interface IBlobSelector
{
    /// <summary>选择已有事实，保持输入顺序和FrameId，空选集正常完成。</summary>
    /// <param name="input">输入事实。</param><param name="options">范围。</param><param name="token">取消。</param><returns>选中事实。</returns>
    BlobAnalysisResult Select(BlobAnalysisResult input, BlobSelectionOptions options, CancellationToken token = default);
}

/// <summary>不依赖SDK的确定性Blob选择。</summary>
public sealed class BlobSelector : IBlobSelector
{
    /// <inheritdoc/>
    public BlobAnalysisResult Select(BlobAnalysisResult input, BlobSelectionOptions options, CancellationToken token = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (options == null) throw new ArgumentNullException(nameof(options));
        token.ThrowIfCancellationRequested();
        var result = new BlobAnalysisResult(input.FrameId, input.Blobs.Where(b =>
        {
            token.ThrowIfCancellationRequested();
            return b.Area >= options.MinimumArea && b.Area <= options.MaximumArea && b.Features.Circularity >= options.MinimumCircularity
                && b.Features.Elongation <= options.MaximumElongation;
        }));
        return input.CoordinateSystem is null ? result : result.InCoordinates(input.CoordinateSystem);
    }
}
