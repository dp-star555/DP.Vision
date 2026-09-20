using System;
using System.Collections.Generic;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>独立的空白与已对齐固定图案测量；无状态，不修改原图或二值排除掩码。</summary>
public sealed class OpenCvInkInspector : IBlankQualityInspector, IFixedQualityInspector
{
    /// <inheritdoc/>
    public InkInspectionResult Inspect(
        IImageSource actual,
        PointD origin,
        InkInspectionOptions options,
        IImageSource? allowedMask = null,
        CancellationToken token = default
    )
    {
        return Run(actual, null, origin, options, allowedMask, token);
    }

    /// <inheritdoc/>
    public InkInspectionResult Inspect(
        IImageSource actual,
        IImageSource reference,
        PointD origin,
        InkInspectionOptions options,
        IImageSource? allowedMask = null,
        CancellationToken token = default
    )
    {
        return Run(
            actual,
            reference ?? throw new ArgumentNullException(nameof(reference)),
            origin,
            options,
            allowedMask,
            token
        );
    }

    private static InkInspectionResult Run(
        IImageSource actual,
        IImageSource? reference,
        PointD origin,
        InkInspectionOptions options,
        IImageSource? allowedMask,
        CancellationToken token
    )
    {
        if (actual == null || options == null)
        {
            throw new ArgumentNullException(nameof(actual));
        }

        token.ThrowIfCancellationRequested();
        if (
            reference != null
            && (actual.Info.Width != reference.Info.Width || actual.Info.Height != reference.Info.Height)
        )
        {
            throw new ArgumentException("Reference size must match actual patch.", nameof(reference));
        }

        // 在昂贵的原生计算之前验证输出坐标范围。
        _ = new RectD(origin.X, origin.Y, actual.Info.Width, actual.Info.Height);
        if (!CvPixels.Supports(actual) || (reference != null && !CvPixels.Supports(reference)))
        {
            return Blocked(
                EAlgorithmStatus.UnsupportedInput,
                "pixel_layout_unsupported",
                "Only 8-bit input layouts are supported."
            );
        }

        using var allowed = Allowed(actual, allowedMask, token);
        if (Cv2.CountNonZero(allowed) == 0)
        {
            return Blocked(
                EAlgorithmStatus.InsufficientEvidence,
                "empty_check_scope",
                "Ignore regions mask the entire requested check."
            );
        }

        using var gray = CvPixels.Gray(actual);
        using var actualInk = CvPixels.Ink(gray, options.Threshold);
        var defects = new List<InkDefect>();
        if (reference == null)
        {
            Cv2.BitwiseAnd(actualInk, allowed, actualInk);
            AddComponents(actualInk, origin, "blank_spot", options.MinimumArea, defects, token);
        }
        else
        {
            using var referenceGray = CvPixels.Gray(reference);
            using var refInk = CvPixels.Ink(referenceGray, options.Threshold);
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new Size(options.Tolerance * 2 + 1, options.Tolerance * 2 + 1)
            );
            using var ignored = new Mat();
            using var ignoredHalo = new Mat();
            using var safe = new Mat();
            Cv2.BitwiseNot(allowed, ignored);
            Cv2.Dilate(ignored, ignoredHalo, kernel);
            Cv2.BitwiseNot(ignoredHalo, safe);
            if (Cv2.CountNonZero(safe) == 0)
            {
                return Blocked(
                    EAlgorithmStatus.InsufficientEvidence,
                    "empty_effective_scope",
                    "Ignore mask plus morphology tolerance leaves no checked pixels."
                );
            }

            Cv2.BitwiseAnd(actualInk, allowed, actualInk);
            Cv2.BitwiseAnd(refInk, allowed, refInk);
            using var expandedActual = new Mat();
            using var expandedReference = new Mat();
            Cv2.Dilate(actualInk, expandedActual, kernel);
            Cv2.Dilate(refInk, expandedReference, kernel);
            using var missing = new Mat();
            using var extra = new Mat();
            using var inverse = new Mat();
            Cv2.BitwiseNot(expandedActual, inverse);
            Cv2.BitwiseAnd(refInk, inverse, missing);
            Cv2.BitwiseAnd(missing, safe, missing);
            Cv2.BitwiseNot(expandedReference, inverse);
            Cv2.BitwiseAnd(actualInk, inverse, extra);
            Cv2.BitwiseAnd(extra, safe, extra);
            AddComponents(missing, origin, "missing_ink", options.MinimumArea, defects, token);
            AddComponents(extra, origin, "extra_ink", options.MinimumArea, defects, token);
        }

        token.ThrowIfCancellationRequested();
        return new InkInspectionResult(EAlgorithmStatus.Completed, "", "", defects);
    }

    private static Mat Allowed(IImageSource actual, IImageSource? mask, CancellationToken token)
    {
        if (mask == null)
        {
            return new Mat(actual.Info.Height, actual.Info.Width, MatType.CV_8UC1, Scalar.All(255));
        }

        if (
            mask.Info.Layout != EPixelLayout.Gray8
            || mask.Info.Width != actual.Info.Width
            || mask.Info.Height != actual.Info.Height
        )
        {
            throw new ArgumentException("Mask must be matching binary Gray8.", nameof(mask));
        }

        var bytes = new byte[mask.Info.ByteLength];
        mask.CopyTo(0, bytes, 0, bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            if (bytes[i] != 0 && bytes[i] != 255)
            {
                throw new ArgumentException("Mask values must be 0 or 255.", nameof(mask));
            }
        }

        return CvPixels.Gray(mask);
    }

    private static InkInspectionResult Blocked(EAlgorithmStatus status, string code, string reason)
    {
        return new InkInspectionResult(status, code, reason, Array.Empty<InkDefect>());
    }

    private static void AddComponents(
        Mat mask,
        PointD origin,
        string code,
        int minimumArea,
        List<InkDefect> output,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(
            mask,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S
        );
        for (int i = 1; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < minimumArea)
            {
                continue;
            }

            var bounds = new RectD(
                origin.X + stats.At<int>(i, (int)ConnectedComponentsTypes.Left),
                origin.Y + stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Height)
            );
            output.Add(new InkDefect(code, bounds, area));
        }
    }
}
