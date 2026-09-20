using System;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>归一化单字差异测量：墨迹长边缩放到80像素，放入112×112画布，执行±2像素平移搜索和配置容差过滤。支持8位布局，明确拒绝Gray16。</summary>
public sealed class OpenCvGlyphComparer : IGlyphComparer
{
    /// <inheritdoc/>
    public GlyphComparisonResult Compare(
        IImageSource actual,
        IImageSource reference,
        GlyphComparisonOptions options,
        CancellationToken token = default
    )
    {
        if (actual == null || reference == null || options == null)
        {
            throw new ArgumentNullException(nameof(actual));
        }

        token.ThrowIfCancellationRequested();
        if (!CvPixels.Supports(actual) || !CvPixels.Supports(reference))
        {
            return new GlyphComparisonResult(
                EAlgorithmStatus.UnsupportedInput,
                "pixel_layout_unsupported",
                0,
                0,
                0
            );
        }

        using var a = Normalize(actual, options, token);
        using var r = Normalize(reference, options, token);
        using var best = a.Clone();
        int bestScore = -1,
            bestDistance = int.MaxValue;
        using var shifted = new Mat();
        using var intersection = new Mat();
        using var transform = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        transform.Set(0, 0, 1f);
        transform.Set(1, 1, 1f);
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                token.ThrowIfCancellationRequested();
                transform.Set(0, 2, (float)dx);
                transform.Set(1, 2, (float)dy);
                Cv2.WarpAffine(a, shifted, transform, new Size(112, 112), InterpolationFlags.Nearest);
                Cv2.BitwiseAnd(shifted, r, intersection);
                int score = Cv2.CountNonZero(intersection),
                    distance = dx * dx + dy * dy;
                if (score > bestScore || (score == bestScore && distance < bestDistance))
                {
                    shifted.CopyTo(best);
                    bestScore = score;
                    bestDistance = distance;
                }
            }
        }

        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(options.Tolerance * 2 + 1, options.Tolerance * 2 + 1)
        );
        using var da = new Mat();
        using var dr = new Mat();
        using var inv = new Mat();
        using var missing = new Mat();
        using var extra = new Mat();
        Cv2.Dilate(best, da, kernel);
        Cv2.Dilate(r, dr, kernel);
        Cv2.BitwiseNot(da, inv);
        Cv2.BitwiseAnd(r, inv, missing);
        Cv2.BitwiseNot(dr, inv);
        Cv2.BitwiseAnd(best, inv, extra);
        int m = Cv2.CountNonZero(missing),
            e = Cv2.CountNonZero(extra),
            n = Cv2.CountNonZero(r);
        using var delta = new Mat(112, 112, MatType.CV_8UC3, Scalar.All(255));
        for (int y = 0; y < 112; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < 112; x++)
            {
                if (best.At<byte>(y, x) > 0 || r.At<byte>(y, x) > 0)
                {
                    delta.Set(y, x, new Vec3b(190, 190, 190));
                }

                if (missing.At<byte>(y, x) > 0)
                {
                    delta.Set(y, x, new Vec3b(30, 30, 230));
                }

                if (extra.At<byte>(y, x) > 0)
                {
                    delta.Set(y, x, new Vec3b(230, 180, 0));
                }
            }
        }

        using var actualView = new Mat();
        using var referenceView = new Mat();
        Cv2.BitwiseNot(best, actualView);
        Cv2.BitwiseNot(r, referenceView);
        using var actualImage = CvPixels.Buffer(actualView);
        using var referenceImage = CvPixels.Buffer(referenceView);
        using var deltaImage = CvPixels.Buffer(delta);
        token.ThrowIfCancellationRequested();
        return new GlyphComparisonResult(
            n == 0 ? EAlgorithmStatus.InsufficientEvidence : EAlgorithmStatus.Completed,
            n == 0 ? "empty_reference" : "",
            n == 0 ? 0 : Math.Round((m + e) / (double)n, 5),
            m,
            e,
            actualImage,
            referenceImage,
            deltaImage
        );
    }

    /// <summary>
    /// 先二值化并裁到墨迹外接框，再等比例缩放和居中；不是把带白边的整个输入图块直接缩放。
    /// 噪点、截字或二值化变化仍会影响墨迹框，此步骤不保证分割结果正确。
    /// </summary>
    /// <param name = "frame">调用期间借用的单字图块。</param>
    /// <param name = "options">二值化模式及固定阈值；此步骤不应用膨胀容差。</param>
    /// <param name = "token">逐行检查的取消标记。</param>
    /// <returns>调用方拥有的112×112二值墨迹Mat；墨迹为255，背景为0。</returns>
    private static Mat Normalize(IImageSource frame, GlyphComparisonOptions options, CancellationToken token)
    {
        using var gray = CvPixels.Gray(frame);
        using var mask = new Mat();
        if (options.Binarization == EGlyphBinarization.Otsu)
        {
            // 保留2%/98%灰度分位差的低对比度保护；不能与不带保护的普通Otsu等同。
            // 统计发生在裁白边之前，因此白边比例仍可能影响这一前处理结果。
            var histogram = new int[256];
            for (int y = 0; y < gray.Rows; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < gray.Cols; x++)
                {
                    histogram[gray.At<byte>(y, x)]++;
                }
            }

            long count = (long)gray.Rows * gray.Cols;
            int low = Percentile(histogram, count, .02),
                high = Percentile(histogram, count, .98);
            if (high - low < 12)
            {
                return new Mat(112, 112, MatType.CV_8UC1, Scalar.All(0));
            }

            Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        }
        else
        {
            Cv2.Threshold(gray, mask, options.Threshold - 1, 255, ThresholdTypes.BinaryInv);
        }

        var output = new Mat(112, 112, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            if (Cv2.CountNonZero(mask) == 0)
            {
                return output;
            }

            var box = Cv2.BoundingRect(mask);
            double scale = 80.0 / Math.Max(box.Width, box.Height);
            int width = Math.Max(1, (int)Math.Round(box.Width * scale)),
                height = Math.Max(1, (int)Math.Round(box.Height * scale));
            using var crop = new Mat(mask, box);
            using var scaled = new Mat();
            Cv2.Resize(crop, scaled, new Size(width, height), 0, 0, InterpolationFlags.Nearest);
            using var target = new Mat(
                output,
                new Rect((112 - width) / 2, (112 - height) / 2, width, height)
            );
            scaled.CopyTo(target);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>在离散灰度直方图中查找指定分位值。</summary>
    /// <param name = "histogram">按灰度值排列的256个计数桶。</param>
    /// <param name = "count">图像总像素数，用于计算零起始分位秩。</param>
    /// <param name = "fraction">分位比例，例如0.02或0.98。</param>
    /// <returns>累计计数首次超过分位秩的灰度值；未命中时返回255。</returns>
    private static int Percentile(int[] histogram, long count, double fraction)
    {
        long rank = (long)Math.Floor((count - 1) * fraction),
            sum = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            sum += histogram[i];
            if (sum > rank)
            {
                return i;
            }
        }

        return 255;
    }
}
