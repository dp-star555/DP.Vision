using System;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>固定方向/尺度的灰度平移定位；归一化均方差对黑色和常量模板同样有定义。</summary>
public sealed class OpenCvTemplateLocator : ITemplateLocator
{
    /// <inheritdoc/>
    public TemplateLocationResult Locate(ImageFrame frame, PixelBounds search, ImageFrame template,
        PixelBounds templateBounds, double minimumScore = .9, CancellationToken token = default, RegionGeometry? regionMask = null, LocatedCoordinateSystem? searchCoordinates = null)
    {
        if (frame == null || template == null) throw new ArgumentNullException(nameof(frame));
        InspectionMask.Validate(regionMask, frame.Image);
        if (searchCoordinates != null)
        {
            searchCoordinates.Validate(frame, searchCoordinates.CoordinateSystemId, searchCoordinates.TemplateSignature);
            if (templateBounds.X != 0 || templateBounds.Y != 0 || templateBounds.Width != template.Image.Info.Width || templateBounds.Height != template.Image.Info.Height)
                throw new ArgumentException("Located translation search requires the complete template.");
            var pose = new OpenCvTemplatePoseLocator().Locate(frame, template, search,
                new TemplatePoseOptions(new[] { searchCoordinates.Pose.AngleRadians }, new[] { searchCoordinates.Pose.Scale }, minimumScore), token, regionMask);
            return TemplateLocationResult.FromPose(pose, searchCoordinates);
        }
        if (!search.Fits(frame.Image) || !templateBounds.Fits(template.Image)
            || templateBounds.Width > search.Width || templateBounds.Height > search.Height)
            throw new ArgumentOutOfRangeException(nameof(search));
        if (double.IsNaN(minimumScore) || minimumScore < 0 || minimumScore > 1) throw new ArgumentOutOfRangeException(nameof(minimumScore));
        if (!CvPixels.Supports(frame.Image) || !CvPixels.Supports(template.Image)) throw new NotSupportedException("Explicit Gray16 conversion required.");
        token.ThrowIfCancellationRequested();
        using var image = CvPixels.Gray(frame.Image);
        using var reference = CvPixels.Gray(template.Image);
        using var roi = new Mat(image, CvImages.Rect(search));
        using var pattern = new Mat(reference, CvImages.Rect(templateBounds));
        using var scores = new Mat();
        Cv2.MatchTemplate(roi, pattern, scores, TemplateMatchModes.SqDiff);
        token.ThrowIfCancellationRequested();
        if (!RegionMatchMinimum.Find(scores, regionMask, frame, search, templateBounds.Width, templateBounds.Height, null, token, out double min, out var location))
            return new TemplateLocationResult(frame.FrameId, template.FrameId, false, 0, null);
        double score = Math.Max(0, Math.Min(1, 1 - min / (65025d * templateBounds.Width * templateBounds.Height)));
        bool found = score >= minimumScore;
        PixelBounds? bounds = found ? new PixelBounds(search.X + location.X, search.Y + location.Y, templateBounds.Width, templateBounds.Height) : (PixelBounds?)null;
        return new TemplateLocationResult(frame.FrameId, template.FrameId, found, score, bounds);
    }
}
