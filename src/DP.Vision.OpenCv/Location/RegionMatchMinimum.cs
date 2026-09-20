using System;
using System.Collections.Generic;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

// A search mask constrains the entire sampled template footprint, not merely its center or bounding box.
internal static class RegionMatchMinimum
{
    internal static bool Find(Mat scores, RegionGeometry? region, ImageFrame frame, PixelBounds search,
        int width, int height, Mat? footprint, CancellationToken token, out double minimum, out Point location)
    {
        if (region == null)
        {
            Cv2.MinMaxLoc(scores, out minimum, out _, out location, out _);
            return true;
        }
        InspectionMask.Validate(region, frame.Image);
        if (frame.Image.Info.ByteLength > 64 * 1024 * 1024 || (long)scores.Rows * scores.Cols * width * height > 2000000000)
            throw new InvalidOperationException("Search mask work budget exceeded.");
        var allowed = new byte[frame.Image.Info.Width * frame.Image.Info.Height];
        foreach (var run in region.Runs)
        { token.ThrowIfCancellationRequested(); for (int x = run.Start; x < run.EndExclusive; x++) allowed[run.Row * frame.Image.Info.Width + x] = 1; }
        var points = new List<Point>();
        for (int y = 0; y < height; y++)
        { token.ThrowIfCancellationRequested(); for (int x = 0; x < width; x++) if (footprint == null || footprint.At<byte>(y, x) != 0) points.Add(new Point(x, y)); }
        minimum = double.PositiveInfinity; location = default; bool found = false;
        for (int y = 0; y < scores.Rows; y++) for (int x = 0; x < scores.Cols; x++)
        {
            token.ThrowIfCancellationRequested();
            double value = scores.At<float>(y, x);
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("Non-finite template score.");
            if (value >= minimum) continue;
            bool valid = true;
            foreach (var p in points)
                if (allowed[(search.Y + y + p.Y) * frame.Image.Info.Width + search.X + x + p.X] == 0) { valid = false; break; }
            if (valid) { minimum = value; location = new Point(x, y); found = true; }
        }
        return found;
    }
}
