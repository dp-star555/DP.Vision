using System;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>归一化单字差异测量：墨迹长边缩放到80像素，放入112×112画布，执行±2像素平移及±6%缩放对齐搜索；差异按块计入，只触及笔画边缘带（容差加整体粗细差）的块视为印刷波动，深入笔画的缺墨/远离笔画的多墨完整计入。支持8位布局，明确拒绝Gray16。</summary>
public sealed class OpenCvGlyphComparer : IGlyphComparer
{
    /// <summary>对齐时尝试的缩放百分比；100优先，其余用于吸收外接框归一化带来的粗细缩放误差。</summary>
    private static readonly int[] Scales = { 100, 98, 102, 96, 104, 94, 106 };

    /// <summary>贴边差异块内切半径不超过此值（归一化像素，约4像素宽以内）时视为轮廓细条，不计入。</summary>
    private const float MinimumEdgeDefectRadius = 2f;

    /// <summary>贴边细条最深点达到附近笔画中心深度的该比例时，视为切断笔画的裂纹而非轮廓波动。</summary>
    private const float CutThroughRatio = .75f;

    /// <summary>整体笔画粗细差可放宽边缘带的上限，占参考最大半宽的比例。</summary>
    private const double MaximumWeightSlack = .4;

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
        double bestScore = -1;
        int bestDistance = int.MaxValue;
        using var shifted = new Mat();
        using var intersection = new Mat();
        using var union = new Mat();
        using var transform = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        // 外接框归一化会把笔画粗细变化转成整体缩放误差（越靠近外缘误差越大），因此在平移之外搜索±6%缩放。
        // 缩放会改变面积，用交并比而不是交集选择最佳对齐，避免偏向放大。
        foreach (int percent in Scales)
        {
            float scale = percent / 100f;
            transform.Set(0, 0, scale);
            transform.Set(1, 1, scale);
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    token.ThrowIfCancellationRequested();
                    transform.Set(0, 2, 56 * (1 - scale) + dx);
                    transform.Set(1, 2, 56 * (1 - scale) + dy);
                    Cv2.WarpAffine(a, shifted, transform, new Size(112, 112), InterpolationFlags.Nearest);
                    Cv2.BitwiseAnd(shifted, r, intersection);
                    Cv2.BitwiseOr(shifted, r, union);
                    int overlap = Cv2.CountNonZero(intersection),
                        total = Cv2.CountNonZero(union),
                        distance = dx * dx + dy * dy + (percent - 100) * (percent - 100);
                    double score = total == 0 ? 0 : overlap / (double)total;
                    if (
                        score > bestScore + 1e-9
                        || (Math.Abs(score - bestScore) <= 1e-9 && distance < bestDistance)
                    )
                    {
                        shifted.CopyTo(best);
                        bestScore = score;
                        bestDistance = distance;
                    }
                }
            }
        }

        // 不先膨胀：膨胀会把内部小缺墨一并缩掉。先按原始差异分块，再用到参考边缘的距离判断每块是否深入笔画。
        using var inv = new Mat();
        using var background = new Mat();
        using var rawMissing = new Mat();
        using var rawExtra = new Mat();
        Cv2.BitwiseNot(best, inv);
        Cv2.BitwiseAnd(r, inv, rawMissing);
        Cv2.BitwiseNot(r, background);
        Cv2.BitwiseAnd(best, background, rawExtra);
        using var inside = new Mat();
        using var outside = new Mat();
        Cv2.DistanceTransform(r, inside, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        Cv2.DistanceTransform(background, outside, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        Cv2.MinMaxLoc(inside, out _, out double halfWidth);
        // 整体笔画粗细差：均匀笔画内距离的均值约为(半宽+1)/2，两者之差的两倍近似半宽差。局部缺陷面积小，几乎不影响均值。
        // 实际字偏细时放宽缺墨边缘带，偏粗时放宽多墨边缘带；放宽量以参考半宽的40%为上限，严重欠墨/过墨仍计入。
        using var actualInside = new Mat();
        Cv2.DistanceTransform(best, actualInside, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        double weight =
            Cv2.CountNonZero(best) == 0 || Cv2.CountNonZero(r) == 0
                ? 0
                : 2 * (Cv2.Mean(actualInside, best).Val0 - Cv2.Mean(inside, r).Val0);
        double slack = MaximumWeightSlack * halfWidth;
        double missingBand = options.Tolerance + Math.Min(slack, Math.Max(0, -weight));
        double extraBand = options.Tolerance + Math.Min(slack, Math.Max(0, weight));
        // 到实际墨迹的距离：变细只让轮廓旁出现缺墨带，旁边仍有实际墨迹；整段笔画缺失时附近没有实际墨迹。
        using var away = new Mat();
        Cv2.DistanceTransform(inv, away, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        using var missing = Significant(
            rawMissing,
            inside,
            inside,
            away,
            halfWidth,
            missingBand,
            options,
            true,
            token
        );
        using var extra = Significant(
            rawExtra,
            outside,
            null,
            null,
            halfWidth,
            extraBand,
            options,
            false,
            token
        );
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

                // 浅色为已忽略的边缘波动，深色为计入差异的缺墨/多墨。
                if (missing.At<byte>(y, x) > 0)
                {
                    delta.Set(y, x, new Vec3b(30, 30, 230));
                }
                else if (rawMissing.At<byte>(y, x) > 0)
                {
                    delta.Set(y, x, new Vec3b(200, 200, 250));
                }

                if (extra.At<byte>(y, x) > 0)
                {
                    delta.Set(y, x, new Vec3b(230, 180, 0));
                }
                else if (rawExtra.At<byte>(y, x) > 0)
                {
                    delta.Set(y, x, new Vec3b(250, 235, 190));
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
    /// 保留深入笔画（或远离笔画）的差异块，完整计入其面积；只触及边缘带的差异块视为印刷波动。
    /// 某块最深点须超过容差带，且达到局部笔画半宽的<see cref="GlyphComparisonOptions.DepthRatio"/>；
    /// 缺墨要求以整个半宽为上限，细笔画整段断开仍会计入。与边缘相连的块还须有一定宽度或切到笔画中心，
    /// 贴着轮廓的细条不计入。计入块中保留超出容差带的全部像素，
    /// 并只在其附近恢复容差带宽度的边缘，避免与缺陷相连的整圈细边被一起计入；
    /// 远离实际墨迹的缺墨（整段细笔画消失）不受此限制，完整计入。
    /// </summary>
    /// <param name = "raw">未经膨胀的缺墨或多墨二值图。</param>
    /// <param name = "depth">每个像素到参考墨迹边缘的距离：缺墨用墨迹内距离，多墨用墨迹外距离。</param>
    /// <param name = "inside">缺墨时为参考墨迹内距离，用于估计局部半宽；多墨时为null，改用整字最大半宽。</param>
    /// <param name = "away">缺墨时为到实际墨迹的距离；远离实际墨迹的缺墨（整段笔画缺失）即使很浅也完整计入。多墨时为null。</param>
    /// <param name = "halfWidth">参考字最大笔画半宽。</param>
    /// <param name = "band">本方向的边缘带宽度：容差加上整体粗细差的放宽量。</param>
    /// <param name = "options">深度比例。</param>
    /// <param name = "missing">是否为缺墨；缺墨的深度要求不超过局部半宽。</param>
    /// <param name = "token">逐行检查的取消标记。</param>
    /// <returns>调用方拥有的计入差异的二值图。</returns>
    private static Mat Significant(
        Mat raw,
        Mat depth,
        Mat? inside,
        Mat? away,
        double halfWidth,
        double band,
        GlyphComparisonOptions options,
        bool missing,
        CancellationToken token
    )
    {
        using var labels = new Mat();
        int count = Cv2.ConnectedComponents(raw, labels, PixelConnectivity.Connectivity8, MatType.CV_32S);
        // own：差异块内部到自身边界的距离，最大值为块的内切半径，用于区分贴边细条和有宽度的缺口。
        using var own = new Mat();
        Cv2.DistanceTransform(raw, own, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        var deepest = new float[count];
        var shallowest = new float[count];
        var width = new float[count];
        var at = new Point[count];
        for (int i = 1; i < count; i++)
        {
            shallowest[i] = float.MaxValue;
        }

        for (int y = 0; y < raw.Rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < raw.Cols; x++)
            {
                int label = labels.At<int>(y, x);
                if (label == 0)
                {
                    continue;
                }

                float d = depth.At<float>(y, x);
                shallowest[label] = Math.Min(shallowest[label], d);
                width[label] = Math.Max(width[label], own.At<float>(y, x));
                if (d > deepest[label])
                {
                    deepest[label] = d;
                    at[label] = new Point(x, y);
                }
            }
        }

        var required = new float[count];
        for (int i = 1; i < count; i++)
        {
            // 局部半宽：最深点附近（约两倍深度范围内）参考墨迹的最大内距离，粗细不同的笔画各自按比例判断。
            // 中心线判断：最深点在自身深度范围内已是局部最大值，说明差异切到了笔画中心。
            float half = inside == null ? (float)halfWidth : DiskMaximum(inside, at[i], 2 * deepest[i] + 1);
            float center = inside == null ? float.MaxValue : DiskMaximum(inside, at[i], deepest[i] + 1);
            double need = Math.Max(band + 1, options.DepthRatio * half);
            required[i] = (float)(missing ? Math.Min(need, half) : need);
            // 贴着笔画边缘的差异块必须是有宽度的缺口，或者（缺墨时）切到笔画中心；
            // 只有1–2像素宽的贴边细条是对齐/粗细造成的轮廓波动，不计入。内部缺墨不受此限制。
            bool attached = shallowest[i] <= band + 1e-3;
            bool cutsThrough = missing && deepest[i] >= CutThroughRatio * center;
            if (attached && width[i] <= MinimumEdgeDefectRadius + 1e-3f && !cutsThrough)
            {
                required[i] = float.MaxValue;
            }
        }

        var core = new Mat(raw.Size(), MatType.CV_8UC1, Scalar.All(0));
        using var kept = new Mat(raw.Size(), MatType.CV_8UC1, Scalar.All(0));
        try
        {
            for (int y = 0; y < raw.Rows; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < raw.Cols; x++)
                {
                    int label = labels.At<int>(y, x);
                    if (label > 0 && deepest[label] >= required[label] - 1e-3f)
                    {
                        kept.Set(y, x, (byte)255);
                        if (depth.At<float>(y, x) >= (float)Math.Min(required[label], band + 1) - 1e-3f)
                        {
                            core.Set(y, x, (byte)255);
                        }
                    }
                }
            }

            // 从深处向外恢复容差带宽度，完整量出缺陷边缘，而不是像膨胀容差那样缩小它。
            int radius = (int)Math.Ceiling(band) + 1;
            using var grow = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(radius * 2 + 1, radius * 2 + 1)
            );
            Cv2.Dilate(core, core, grow);
            if (away != null)
            {
                using var vanished = new Mat();
                Cv2.Threshold(away, vanished, band, 255, ThresholdTypes.Binary);
                vanished.ConvertTo(vanished, MatType.CV_8UC1);
                Cv2.BitwiseOr(core, vanished, core);
            }

            Cv2.BitwiseAnd(core, kept, core);
            return core;
        }
        catch
        {
            core.Dispose();
            throw;
        }
    }

    /// <summary>取以某点为圆心、指定半径内的最大距离值。</summary>
    /// <param name = "distance">32位浮点距离图。</param>
    /// <param name = "center">圆心像素。</param>
    /// <param name = "radius">归一化像素半径。</param>
    /// <returns>圆内最大距离值。</returns>
    private static float DiskMaximum(Mat distance, Point center, float radius)
    {
        int r = (int)Math.Ceiling(radius);
        float maximum = 0;
        for (int y = Math.Max(0, center.Y - r); y <= Math.Min(distance.Rows - 1, center.Y + r); y++)
        {
            for (int x = Math.Max(0, center.X - r); x <= Math.Min(distance.Cols - 1, center.X + r); x++)
            {
                int dx = x - center.X,
                    dy = y - center.Y;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    maximum = Math.Max(maximum, distance.At<float>(y, x));
                }
            }
        }

        return maximum;
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
