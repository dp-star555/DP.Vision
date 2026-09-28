using System;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>基于ECC（增强相关系数）的受限平移配准：只估计平移，相关系数与平移量都须在可信范围内。</summary>
public sealed class OpenCvTranslationRegistrar : ITranslationRegistrar
{
    /// <inheritdoc/>
    public TranslationRegistrationResult Register(
        IImageSource image,
        IImageSource reference,
        PixelBounds bounds,
        RegionGeometry? mask = null,
        TranslationRegistrationOptions? options = null,
        CancellationToken token = default
    )
    {
        if (image == null || reference == null)
        {
            throw new ArgumentNullException(image == null ? nameof(image) : nameof(reference));
        }

        if (!bounds.Fits(image) || !bounds.Fits(reference))
        {
            throw new ArgumentOutOfRangeException(nameof(bounds));
        }

        if (!CvPixels.Supports(image) || !CvPixels.Supports(reference))
        {
            throw new NotSupportedException("Explicit Gray16 conversion required.");
        }

        InspectionMask.Validate(mask, reference);
        options ??= new TranslationRegistrationOptions();
        token.ThrowIfCancellationRequested();
        using var actualGray = CvPixels.Gray(image);
        using var referenceGray = CvPixels.Gray(reference);
        using var a = new Mat(actualGray, CvPixels.Rect(bounds));
        using var r = new Mat(referenceGray, CvPixels.Rect(bounds));
        using var valid = new Mat(r.Rows, r.Cols, MatType.CV_8UC1, Scalar.All(mask == null ? 255 : 0));
        if (mask != null)
        {
            foreach (var run in mask.Runs)
            {
                int y = run.Row - bounds.Y,
                    start = Math.Max(run.Start, bounds.X) - bounds.X,
                    end = Math.Min(run.EndExclusive, bounds.X + bounds.Width) - bounds.X;
                if (y >= 0 && y < r.Rows && end > start)
                {
                    using var cut = new Mat(valid, new Rect(start, y, end - start, 1));
                    cut.SetTo(Scalar.All(255));
                }
            }
        }

        Cv2.MeanStdDev(r, out _, out Scalar std, valid);
        if (std.Val0 < options.MinimumContrast || Cv2.CountNonZero(valid) < options.MinimumPixels)
        {
            return new TranslationRegistrationResult(false, 0, 0, 0, "low_texture");
        }

        token.ThrowIfCancellationRequested();
        using var warp = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        warp.Set(0, 0, 1f);
        warp.Set(1, 1, 1f);
        double score;
        try
        {
            score = Cv2.FindTransformECC(
                r,
                a,
                warp,
                MotionTypes.Translation,
                new TermCriteria(
                    CriteriaTypes.Count | CriteriaTypes.Eps,
                    options.Iterations,
                    options.Epsilon
                ),
                valid,
                1
            );
        }
        catch (OpenCVException)
        {
            return new TranslationRegistrationResult(false, 0, 0, 0, "not_converged");
        }

        float x = warp.At<float>(0, 2),
            y2 = warp.At<float>(1, 2);
        if (double.IsNaN(score) || float.IsNaN(x) || float.IsNaN(y2))
        {
            return new TranslationRegistrationResult(false, 0, 0, 0, "not_converged");
        }

        if (score < options.MinimumScore)
        {
            return new TranslationRegistrationResult(false, 0, 0, score, "low_score");
        }

        if (Math.Abs(x) > options.MaximumShift || Math.Abs(y2) > options.MaximumShift)
        {
            return new TranslationRegistrationResult(false, 0, 0, score, "shift_limit");
        }

        return new TranslationRegistrationResult(true, x, y2, score, null);
    }
}
