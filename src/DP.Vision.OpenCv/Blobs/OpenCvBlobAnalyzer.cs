using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>固定灰度闭区间阈值及4/8连通域实现；保留原图Region，不填孔、不裁剪、不做形态学修补。</summary>
public sealed class OpenCvBlobAnalyzer : IBlobAnalyzer
{
    /// <inheritdoc/>
    public BlobAnalysisResult Analyze(ImageFrame frame, PixelBounds bounds, BlobOptions options, CancellationToken token = default, RegionGeometry? regionMask = null)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (!bounds.Fits(frame.Image)) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (!CvPixels.Supports(frame.Image)) throw new NotSupportedException("Gray16 Blob analysis requires an explicit conversion.");
        token.ThrowIfCancellationRequested();
        InspectionMask.Validate(regionMask, frame.Image);
        using var gray = CvPixels.Gray(frame.Image);
        using var roi = new Mat(gray, CvImages.Rect(bounds));
        using var mask = new Mat();
        Cv2.InRange(roi, new Scalar(options.MinimumGray), new Scalar(options.MaximumGray), mask);
        if (regionMask != null)
        {
            using var allowed = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.All(0));
            foreach (var run in regionMask.Runs)
            {
                token.ThrowIfCancellationRequested();
                int left = Math.Max(bounds.X, run.Start), right = Math.Min(bounds.X + bounds.Width, run.EndExclusive);
                if (run.Row < bounds.Y || run.Row >= bounds.Y + bounds.Height || right <= left) continue;
                using var segment = new Mat(allowed, new Rect(left - bounds.X, run.Row - bounds.Y, right - left, 1));
                segment.SetTo(Scalar.All(255));
            }
            Cv2.BitwiseAnd(mask, allowed, mask);
        }
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        token.ThrowIfCancellationRequested();
        int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            options.EightConnected ? PixelConnectivity.Connectivity8 : PixelConnectivity.Connectivity4, MatType.CV_32S);
        var runs = new Dictionary<int, List<RegionRun>>();
        for (int i = 1; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) >= options.MinimumArea)
                runs.Add(i, new List<RegionRun>());
        }
        // 一次扫描所有标签，避免每个Blob重复扫描整张图；预算限制防止证据无界增长。
        int runCount = 0;
        for (int y = 0; y < bounds.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            int x = 0;
            while (x < bounds.Width)
            {
                int start = x, label = labels.At<int>(y, x++);
                while (x < bounds.Width && labels.At<int>(y, x) == label) x++;
                if (runs.TryGetValue(label, out var component))
                {
                    if (++runCount > 2000000) throw new InvalidOperationException("Blob evidence exceeds the run budget.");
                    component.Add(new RegionRun(y + bounds.Y, start + bounds.X, x + bounds.X));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        var blobs = runs.Values.Select(r => new BlobObservation(new RegionGeometry(r), token))
            .OrderBy(b => b.Region.Runs[0].Row).ThenBy(b => b.Region.Runs[0].Start).ToArray();
        return new BlobAnalysisResult(frame.FrameId, blobs);
    }
}
