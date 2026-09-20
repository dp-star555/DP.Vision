using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>Canny边缘上的正交直线/代数圆拟合；不宣称鲁棒离群点拟合或亚像素卡尺。</summary>
public sealed class OpenCvEdgeMeasurer : IEdgeMeasurer
{
    /// <inheritdoc/>
    public EdgeMeasurementResult Measure(ImageFrame frame, PixelBounds bounds, EdgeMeasurementOptions options, CancellationToken token = default, RegionGeometry? regionMask = null)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (!bounds.Fits(frame.Image)) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (!CvPixels.Supports(frame.Image)) throw new NotSupportedException("Explicit Gray16 conversion required.");
        InspectionMask.Validate(regionMask, frame.Image);
        token.ThrowIfCancellationRequested();
        using var gray = CvPixels.Gray(frame.Image);
        using var allEdges = new Mat();
        Cv2.Canny(gray, allEdges, options.LowThreshold, options.HighThreshold);
        using var edges = new Mat(allEdges, CvImages.Rect(bounds));
        var points = new List<Coordinate2D>();
        for (int y = 0; y < edges.Rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < edges.Cols; x++) if (edges.At<byte>(y, x) != 0)
            {
                if (regionMask != null && !regionMask.Contains(new PointD(bounds.X + x + .5, bounds.Y + y + .5))) continue;
                if (points.Count == 1000000) throw new InvalidOperationException("Edge evidence budget exceeded.");
                points.Add(new Coordinate2D(bounds.X + x + .5, bounds.Y + y + .5));
            }
        }
        if (points.Count < options.MinimumPoints) throw new InvalidOperationException("Insufficient edge evidence for measurement.");
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y);
        if (options.Model == EEdgeModel.Circle)
        {
            var center = CalibrationSolver.FitRotationCenter(points, token);
            var distances = points.Select(p => Math.Sqrt((p.X - center.X) * (p.X - center.X) + (p.Y - center.Y) * (p.Y - center.Y))).ToArray();
            double radius = distances.Average();
            double rms = Math.Sqrt(distances.Average(d => (d - radius) * (d - radius)));
            token.ThrowIfCancellationRequested();
            return new EdgeMeasurementResult(frame.FrameId, options.Model, new PointD(center.X, center.Y),
                new PointD(center.X + radius, center.Y), radius, rms, points.Count);
        }
        double xx = 0, yy = 0, xy = 0;
        foreach (var p in points) { double x = p.X - mx, y = p.Y - my; xx += x * x; yy += y * y; xy += x * y; }
        double separation = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
        if (separation <= 1e-10 * (xx + yy)) throw new InvalidOperationException("Edge cloud has no unique line direction.");
        double angle = .5 * Math.Atan2(2 * xy, xx - yy), dx = Math.Cos(angle), dy = Math.Sin(angle);
        double min = double.PositiveInfinity, max = double.NegativeInfinity, squared = 0;
        foreach (var p in points)
        {
            token.ThrowIfCancellationRequested();
            double t = (p.X - mx) * dx + (p.Y - my) * dy;
            min = Math.Min(min, t); max = Math.Max(max, t);
            double d = -(p.X - mx) * dy + (p.Y - my) * dx; squared += d * d;
        }
        return new EdgeMeasurementResult(frame.FrameId, options.Model, new PointD(mx + min * dx, my + min * dy),
            new PointD(mx + max * dx, my + max * dy), 0, Math.Sqrt(squared / points.Count), points.Count);
    }
}
