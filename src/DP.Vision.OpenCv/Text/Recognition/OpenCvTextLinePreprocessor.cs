using System;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OpenCv;

/// <summary>PP-OCRv4单行BGR缩放与归一化，不跨任务接口暴露原生类型。</summary>
public sealed class OpenCvTextLinePreprocessor : ITextLinePreprocessor
{
    /// <inheritdoc/>
    public TextLineInput Prepare(IImageSource frame, PixelRect bounds, CancellationToken token)
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (!bounds.Fits(frame))
        {
            throw new ArgumentException("Line ROI outside input.", nameof(bounds));
        }

        token.ThrowIfCancellationRequested();
        double ratio = bounds.Width / (double)bounds.Height;
        int width = (int)(48 * Math.Max(320.0 / 48, ratio));
        if (width > 4096)
        {
            throw new ArgumentException("Line aspect ratio exceeds OCR input limit.", nameof(bounds));
        }

        int content = Math.Min(width, (int)Math.Ceiling(48 * ratio));
        using var raw = CvImages.Mat(frame);
        using var crop = new Mat(raw, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        using var color = new Mat();
        if (frame.Info.Layout == EPixelLayout.Gray8)
        {
            Cv2.CvtColor(crop, color, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            crop.CopyTo(color);
        }

        using var resized = new Mat();
        Cv2.Resize(color, resized, new Size(content, 48), 0, 0, InterpolationFlags.Linear);
        var bytes = new byte[48 * content * 3];
        Marshal.Copy(resized.Data, bytes, 0, bytes.Length);
        var values = new float[3 * 48 * width];
        for (int y = 0; y < 48; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < content; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    values[(c * 48 + y) * width + x] = (bytes[(y * content + x) * 3 + c] / 255f - .5f) / .5f;
                }
            }
        }

        return new TextLineInput(width, content, values);
    }
}
