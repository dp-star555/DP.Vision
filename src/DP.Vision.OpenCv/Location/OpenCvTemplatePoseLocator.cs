using System;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>旋转/尺度候选的有效模板掩码SqDiff搜索；不把旋转后的空白角当模板证据。</summary>
public sealed class OpenCvTemplatePoseLocator : ITemplatePoseLocator
{
    /// <inheritdoc/>
    public TemplatePoseResult Locate(ImageFrame frame, ImageFrame template, PixelBounds bounds, TemplatePoseOptions options, CancellationToken token = default, RegionGeometry? regionMask = null)
    {
        if (frame == null || template == null || options == null) throw new ArgumentNullException(nameof(frame));
        if (!bounds.Fits(frame.Image)) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (!CvPixels.Supports(frame.Image) || !CvPixels.Supports(template.Image)) throw new NotSupportedException("Convert Gray16 explicitly first.");
        InspectionMask.Validate(regionMask, frame.Image);
        var info = template.Image.Info;
        if ((long)frame.Image.Info.Width * frame.Image.Info.Height > 16777216 || (long)info.Width * info.Height > 16777216) throw new ArgumentException("Pose image budget exceeded.");
        token.ThrowIfCancellationRequested(); long work = 0;
        // 搜索前验证累计预算，避免运行一部分后返回被截断的“最佳”结果。
        foreach (double angle in options.AnglesRadians) foreach (double scale in options.Scales)
        {
            var size = RotatedSize(info.Width, info.Height, angle, scale);
            if (size.Width > bounds.Width || size.Height > bounds.Height) continue;
            long positions = (long)(bounds.Width - size.Width + 1) * (bounds.Height - size.Height + 1);
            long cost = positions * size.Width * size.Height;
            if (cost > options.MaximumWork - work) throw new InvalidOperationException("Pose comparison budget exceeded; narrow the ROI or candidates.");
            work += cost;
        }
        using var image = CvPixels.Gray(frame.Image); using var original = CvPixels.Gray(template.Image);
        using var search = new Mat(image, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        using var fullMask = new Mat(info.Height, info.Width, MatType.CV_8UC1, Scalar.All(255));
        double best = -1; TemplatePoseTransform? transform = null;
        foreach (double angle in options.AnglesRadians) foreach (double scale in options.Scales)
        {
            token.ThrowIfCancellationRequested();
            var size = RotatedSize(info.Width, info.Height, angle, scale);
            if (size.Width > bounds.Width || size.Height > bounds.Height) continue;
            double a = scale * Math.Cos(angle), b = -scale * Math.Sin(angle), c = -b, d = a;
            using var matrix = new Mat(2, 3, MatType.CV_64FC1);
            matrix.Set(0, 0, a); matrix.Set(0, 1, b); matrix.Set(0, 2, (size.Width - 1) / 2d - a * (info.Width - 1) / 2d - b * (info.Height - 1) / 2d);
            matrix.Set(1, 0, c); matrix.Set(1, 1, d); matrix.Set(1, 2, (size.Height - 1) / 2d - c * (info.Width - 1) / 2d - d * (info.Height - 1) / 2d);
            using var warped = new Mat(); using var mask = new Mat();
            Cv2.WarpAffine(original, warped, matrix, size, InterpolationFlags.Linear, BorderTypes.Replicate);
            Cv2.WarpAffine(fullMask, mask, matrix, size, InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
            int area = Cv2.CountNonZero(mask); if (area == 0) continue;
            using var scores = new Mat(); Cv2.MatchTemplate(search, warped, scores, TemplateMatchModes.SqDiff, mask);
            if (!RegionMatchMinimum.Find(scores, regionMask, frame, bounds, size.Width, size.Height, mask, token, out double minimum, out Point location)) continue;
            if (double.IsNaN(minimum) || double.IsInfinity(minimum)) throw new InvalidOperationException("Non-finite pose score.");
            double score = Math.Max(0, Math.Min(1, 1 - minimum / (65025d * area)));
            if (score > best)
            {
                best = score;
                transform = new TemplatePoseTransform(info.Width, info.Height,
                    new PointD(bounds.X + location.X + size.Width / 2d, bounds.Y + location.Y + size.Height / 2d), angle, scale);
            }
        }
        token.ThrowIfCancellationRequested();
        return new TemplatePoseResult(frame.FrameId, template.FrameId, Math.Max(0, best), best >= options.MinimumScore ? transform : null);
    }

    private static Size RotatedSize(int width, int height, double angle, double scale) => new Size(
        Math.Max(1, (int)Math.Ceiling(scale * (Math.Abs(Math.Cos(angle)) * width + Math.Abs(Math.Sin(angle)) * height) - 1e-10)),
        Math.Max(1, (int)Math.Ceiling(scale * (Math.Abs(Math.Sin(angle)) * width + Math.Abs(Math.Cos(angle)) * height) - 1e-10)));
}
