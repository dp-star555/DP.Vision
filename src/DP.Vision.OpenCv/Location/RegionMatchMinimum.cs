using System;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

// A search mask constrains the entire sampled template footprint, not merely its center or bounding box.
internal static class RegionMatchMinimum
{
    /// <summary>
    /// 把搜索矩形收缩到搜索区域的外接框：模板必须整体落在区域内，外接框以外不可能有合法位置，
    /// 所以收缩不改变结果，只减少模板比较的面积。区域为空或与搜索矩形不相交时返回null，表示没有合法位置。
    /// </summary>
    internal static PixelBounds? Narrow(PixelBounds search, RegionGeometry? region)
    {
        if (region == null) return search;
        if (region.AreaPixels == 0) return null;
        var b = region.Bounds;
        int left = Math.Max(search.X, (int)b.X), top = Math.Max(search.Y, (int)b.Y);
        int right = Math.Min(search.X + search.Width, (int)(b.X + b.Width)), bottom = Math.Min(search.Y + search.Height, (int)(b.Y + b.Height));
        return right > left && bottom > top ? new PixelBounds(left, top, right - left, bottom - top) : (PixelBounds?)null;
    }

    /// <summary>
    /// 在分数图中找最小值，只考虑模板有效像素全部落在区域内的位置；window为null表示不限制区域。
    /// 合法位置用“窗口内区域外像素”与模板足迹做一次相关（OpenCV原生、大模板自动走DFT）得到，
    /// 不再为每个候选分配整幅图掩码、也不在托管代码里逐像素扫描。
    /// </summary>
    internal static bool Find(Mat scores, RegionWindow? window, int width, int height, Mat? footprint,
        CancellationToken token, out double minimum, out Point location)
    {
        minimum = double.PositiveInfinity; location = default;
        if (window == null || !window.HasOutside)
        {
            Cv2.MinMaxLoc(scores, out minimum, out _, out location, out _);
            return true;
        }
        using var kernel = new Mat();
        if (footprint == null) using (var ones = new Mat(height, width, MatType.CV_8UC1, Scalar.All(1))) ones.ConvertTo(kernel, MatType.CV_32F);
        else { using var binary = new Mat(); Cv2.Threshold(footprint, binary, 0, 1, ThresholdTypes.Binary); binary.ConvertTo(kernel, MatType.CV_32F); }
        // 每个位置上压到区域外的足迹像素数，大于0.5即非法。
        using var overlap = new Mat();
        Cv2.MatchTemplate(window.Outside, kernel, overlap, TemplateMatchModes.CCorr);
        token.ThrowIfCancellationRequested();
        if (overlap.Rows != scores.Rows || overlap.Cols != scores.Cols) throw new InvalidOperationException("Search mask size mismatch.");
        using var valid = new Mat();
        Cv2.Threshold(overlap, overlap, .5, 255, ThresholdTypes.BinaryInv);
        overlap.ConvertTo(valid, MatType.CV_8U);
        if (Cv2.CountNonZero(valid) == 0) return false;
        Cv2.MinMaxLoc(scores, out minimum, out _, out location, out _, valid);
        return true;
    }
}

/// <summary>一次匹配内共用的搜索窗口区域：窗口内不属于区域的像素为1（CV_32F）。</summary>
internal sealed class RegionWindow : IDisposable
{
    private RegionWindow(Mat outside, bool hasOutside) { Outside = outside; HasOutside = hasOutside; }

    internal Mat Outside { get; }

    /// <summary>窗口内是否存在区域外像素；没有时无需限制。</summary>
    internal bool HasOutside { get; }

    /// <summary>为搜索窗口构建区域；region为null时返回null。</summary>
    internal static RegionWindow? Create(RegionGeometry? region, ImageFrame frame, PixelBounds search, CancellationToken token)
    {
        if (region == null) return null;
        InspectionMask.Validate(region, frame.Image);
        var data = new float[search.Width * search.Height];
        for (int i = 0; i < data.Length; i++) data[i] = 1;
        long inside = 0;
        foreach (var run in region.Runs)
        {
            int y = run.Row - search.Y;
            if (y < 0) continue;
            if (y >= search.Height) break;
            token.ThrowIfCancellationRequested();
            int start = Math.Max(run.Start, search.X) - search.X, end = Math.Min(run.EndExclusive, search.X + search.Width) - search.X;
            for (int x = start; x < end; x++) data[y * search.Width + x] = 0;
            if (end > start) inside += end - start;
        }
        var mat = new Mat(search.Height, search.Width, MatType.CV_32F);
        Marshal.Copy(data, 0, mat.Data, data.Length);
        return new RegionWindow(mat, inside < (long)search.Width * search.Height);
    }

    public void Dispose() => Outside.Dispose();
}
