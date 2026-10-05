using System;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>固定方向/尺度的灰度平移定位；归一化均方差对黑色和常量模板同样有定义。</summary>
public sealed class OpenCvTemplateLocator : ITemplateLocator
{
    /// <inheritdoc/>
    public TemplatePoseResult Locate(ImageFrame frame, PixelBounds search, ImageFrame template,
        PixelBounds templateBounds, double minimumScore = .9, CancellationToken token = default, RegionGeometry? regionMask = null, VisionCoordinateSystem? searchCoordinates = null)
    {
        if (frame == null || template == null) throw new ArgumentNullException(nameof(frame));
        InspectionMask.Validate(regionMask, frame.Image);
        if (searchCoordinates != null)
        {
            searchCoordinates.ValidateFrame(frame);
            if (templateBounds.X != 0 || templateBounds.Y != 0 || templateBounds.Width != template.Image.Info.Width || templateBounds.Height != template.Image.Info.Height)
                throw new ArgumentException("Located translation search requires the complete template.");
            return new OpenCvTemplatePoseLocator().Locate(frame, template, search,
                new TemplatePoseOptions(searchCoordinates.RotationRadians, searchCoordinates.RotationRadians, searchCoordinates.SimilarityScale, searchCoordinates.SimilarityScale, minimumScore), token, regionMask);
        }
        if (!search.Fits(frame.Image) || !templateBounds.Fits(template.Image)
            || templateBounds.Width > search.Width || templateBounds.Height > search.Height)
            throw new ArgumentOutOfRangeException(nameof(search));
        if (double.IsNaN(minimumScore) || minimumScore < 0 || minimumScore > 1) throw new ArgumentOutOfRangeException(nameof(minimumScore));
        if (!CvPixels.Supports(frame.Image) || !CvPixels.Supports(template.Image)) throw new NotSupportedException("Explicit Gray16 conversion required.");
        token.ThrowIfCancellationRequested();
        var templateReference = TemplateReference.FromImage(template.Image, templateBounds, token);
        // 模板必须整体落在区域内：搜索矩形收缩到区域外接框，结果不变、比较面积更小。
        if (RegionMatchMinimum.Narrow(search, regionMask) is not { } narrowed || narrowed.Width < templateBounds.Width || narrowed.Height < templateBounds.Height)
            return new TemplatePoseResult(frame.FrameId, template.FrameId, 0, null, templateReference);
        search = narrowed;
        using var window = RegionWindow.Create(regionMask, frame, search, token);
        using var image = CvPixels.Gray(frame.Image);
        using var reference = CvPixels.Gray(template.Image);
        using var roi = new Mat(image, CvPixels.Rect(search));
        using var pattern = new Mat(reference, CvPixels.Rect(templateBounds));
        using var scores = new Mat();
        Cv2.MatchTemplate(roi, pattern, scores, TemplateMatchModes.SqDiff);
        token.ThrowIfCancellationRequested();
        if (!RegionMatchMinimum.Find(scores, window, templateBounds.Width, templateBounds.Height, null, token, out double min, out var location))
            return new TemplatePoseResult(frame.FrameId, template.FrameId, 0, null, templateReference);
        double score = Math.Max(0, Math.Min(1, 1 - min / (65025d * templateBounds.Width * templateBounds.Height)));
        var transform = score >= minimumScore ? new TemplatePoseTransform(templateBounds.Width, templateBounds.Height,
            new PointD(search.X + location.X + templateBounds.Width / 2d, search.Y + location.Y + templateBounds.Height / 2d), 0, 1) : null;
        return new TemplatePoseResult(frame.FrameId, template.FrameId, score, transform, templateReference);
    }
}
