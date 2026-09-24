using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>无SDK依赖的RGB编码值均值实现；忽略Alpha权重，Gray8复制到三个通道，拒绝Gray16。</summary>
public sealed class RgbColorAnalyzer : IColorAnalyzer
{
    /// <inheritdoc/>
    public ColorAnalysisResult Analyze(ImageFrame frame, PixelBounds bounds, CancellationToken token = default, RegionGeometry? regionMask = null)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (!bounds.Fits(frame.Image)) throw new ArgumentOutOfRangeException(nameof(bounds));
        InspectionMask.Validate(regionMask, frame.Image);
        var info = frame.Image.Info;
        if (info.Layout == EPixelLayout.Gray16) throw new NotSupportedException("Gray16 is not an 8-bit RGB encoding.");
        token.ThrowIfCancellationRequested();
        var row = new byte[checked(bounds.Width * info.BytesPerPixel)];
        long red = 0, green = 0, blue = 0, count = 0;
        bool rgb = info.Layout == EPixelLayout.Rgb24 || info.Layout == EPixelLayout.Rgba32;
        for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            frame.Image.CopyRegion(bounds.X, y, bounds.Width, 1, row);
            for (int i = 0; i < row.Length; i += info.BytesPerPixel)
            {
                if (regionMask != null && !regionMask.Contains(new PointD(bounds.X + i / info.BytesPerPixel + .5, y + .5))) continue;
                count++;
                if (info.Layout == EPixelLayout.Gray8) { red += row[i]; green += row[i]; blue += row[i]; }
                else { red += row[i + (rgb ? 0 : 2)]; green += row[i + 1]; blue += row[i + (rgb ? 2 : 0)]; }
            }
        }
        token.ThrowIfCancellationRequested();
        if (count == 0) throw new InvalidOperationException("Empty inspection mask has no color mean.");
        return new ColorAnalysisResult(frame.FrameId, count, (double)red / count, (double)green / count, (double)blue / count);
    }
}
