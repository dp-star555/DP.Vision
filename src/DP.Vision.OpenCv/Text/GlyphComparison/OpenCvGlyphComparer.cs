using System;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>归一化单字差异测量：墨迹长边缩放到80像素，放入112×112画布，执行±2像素平移及±6%缩放对齐搜索；差异按块计入，只触及笔画边缘带（容差加整体粗细差）的块视为印刷波动，深入笔画的缺墨/远离笔画的多墨完整计入。支持8位布局，明确拒绝Gray16。</summary>
public sealed class OpenCvGlyphComparer : IGlyphComparer
{
    /// <summary>对齐时尝试的缩放百分比；100优先，其余用于吸收外接框归一化带来的粗细缩放误差。</summary>
    private static readonly int[] Scales = { 100, 98, 102, 96, 104, 94, 106 };

    /// <summary>归一化画布边长。</summary>
    private const int Side = 112;

    /// <summary>对齐搜索的最大平移（像素）。</summary>
    private const int Shift = 2;

    /// <summary>每行打包所需的64位字数。</summary>
    private const int Words = 2;


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
        using var best = Align(a, r, token);

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
        var bestData = Bytes(best);
        var referenceData = Bytes(r);
        var missingData = Bytes(missing);
        var rawMissingData = Bytes(rawMissing);
        var extraData = Bytes(extra);
        var rawExtraData = Bytes(rawExtra);
        var deltaData = new byte[Side * Side * 3];
        for (int p = 0; p < Side * Side; p++)
        {
            // 浅色为已忽略的边缘波动，深色为计入差异的缺墨/多墨（BGR）。
            byte b = 255,
                g = 255,
                red = 255;
            if (bestData[p] > 0 || referenceData[p] > 0)
            {
                (b, g, red) = (190, 190, 190);
            }

            if (missingData[p] > 0)
            {
                (b, g, red) = (30, 30, 230);
            }
            else if (rawMissingData[p] > 0)
            {
                (b, g, red) = (200, 200, 250);
            }

            if (extraData[p] > 0)
            {
                (b, g, red) = (230, 180, 0);
            }
            else if (rawExtraData[p] > 0)
            {
                (b, g, red) = (250, 235, 190);
            }

            deltaData[p * 3] = b;
            deltaData[p * 3 + 1] = g;
            deltaData[p * 3 + 2] = red;
        }

        token.ThrowIfCancellationRequested();
        using var delta = new Mat(Side, Side, MatType.CV_8UC3);
        Marshal.Copy(deltaData, 0, delta.Data, deltaData.Length);

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

        int cols = raw.Cols;
        labels.GetArray(out int[] labelData);
        depth.GetArray(out float[] depthData);
        own.GetArray(out float[] ownData);
        float[]? insideData = null;
        if (inside != null)
        {
            inside.GetArray(out float[] values);
            insideData = values;
        }

        for (int p = 0; p < labelData.Length; p++)
        {
            int label = labelData[p];
            if (label == 0)
            {
                continue;
            }

            float d = depthData[p];
            shallowest[label] = Math.Min(shallowest[label], d);
            width[label] = Math.Max(width[label], ownData[p]);
            if (d > deepest[label])
            {
                deepest[label] = d;
                at[label] = new Point(p % cols, p / cols);
            }
        }

        token.ThrowIfCancellationRequested();
        var required = new float[count];
        for (int i = 1; i < count; i++)
        {
            // 局部半宽：最深点附近（约两倍深度范围内）参考墨迹的最大内距离，粗细不同的笔画各自按比例判断。
            // 中心线判断：最深点在自身深度范围内已是局部最大值，说明差异切到了笔画中心。
            float half =
                insideData == null
                    ? (float)halfWidth
                    : DiskMaximum(insideData, raw.Rows, cols, at[i], 2 * deepest[i] + 1);
            float center =
                insideData == null
                    ? float.MaxValue
                    : DiskMaximum(insideData, raw.Rows, cols, at[i], deepest[i] + 1);
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

        var keptData = new byte[labelData.Length];
        var coreData = new byte[labelData.Length];
        for (int p = 0; p < labelData.Length; p++)
        {
            int label = labelData[p];
            if (label > 0 && deepest[label] >= required[label] - 1e-3f)
            {
                keptData[p] = 255;
                if (depthData[p] >= (float)Math.Min(required[label], band + 1) - 1e-3f)
                {
                    coreData[p] = 255;
                }
            }
        }

        token.ThrowIfCancellationRequested();
        var core = new Mat(raw.Rows, cols, MatType.CV_8UC1);
        using var kept = new Mat(raw.Rows, cols, MatType.CV_8UC1);
        try
        {
            Marshal.Copy(coreData, 0, core.Data, coreData.Length);
            Marshal.Copy(keptData, 0, kept.Data, keptData.Length);
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
    /// <param name = "distance">按行排列的32位浮点距离图。</param>
    /// <param name = "rows">行数。</param>
    /// <param name = "cols">列数。</param>
    /// <param name = "center">圆心像素。</param>
    /// <param name = "radius">归一化像素半径。</param>
    /// <returns>圆内最大距离值。</returns>
    private static float DiskMaximum(float[] distance, int rows, int cols, Point center, float radius)
    {
        int r = (int)Math.Ceiling(radius);
        float maximum = 0;
        for (int y = Math.Max(0, center.Y - r); y <= Math.Min(rows - 1, center.Y + r); y++)
        {
            for (int x = Math.Max(0, center.X - r); x <= Math.Min(cols - 1, center.X + r); x++)
            {
                int dx = x - center.X,
                    dy = y - center.Y;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    maximum = Math.Max(maximum, distance[y * cols + x]);
                }
            }
        }

        return maximum;
    }

    /// <summary>
    /// 对齐搜索：每个缩放只做一次最近邻仿射，画到四周各多2像素的画布上；±2像素平移即从该画布裁取112×112，
    /// 按行打包成位串用位与/位计数求交集和并集。按交并比选最佳（缩放会改变面积，不用交集），同分取离原位最近的。
    /// </summary>
    /// <param name = "actual">归一化实际字。</param>
    /// <param name = "reference">归一化参考字。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>调用方拥有的最佳对齐后的实际字。</returns>
    private static Mat Align(Mat actual, Mat reference, CancellationToken token)
    {
        const int padded = Side + 2 * Shift;
        var referenceRows = Pack(Bytes(reference), Side, 0, Side);
        int referenceCount = 0;
        foreach (ulong word in referenceRows)
        {
            referenceCount += PopCount(word);
        }

        double bestScore = -1;
        int bestDistance = int.MaxValue,
            bestPercent = 100,
            bestDx = 0,
            bestDy = 0;
        using var transform = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        using var canvas = new Mat();
        // 外接框归一化会把笔画粗细变化转成整体缩放误差（越靠近外缘误差越大），因此在平移之外搜索±6%缩放。
        foreach (int percent in Scales)
        {
            token.ThrowIfCancellationRequested();
            Warp(actual, canvas, transform, percent);
            var wide = Pack(Bytes(canvas), padded, 0, padded);
            // 每个水平平移量一份按行打包的画布（列从Shift-dx开始，由整行位串右移得到）；垂直平移只改变取用的行。
            var columns = new ulong[2 * Shift + 1][];
            for (int dx = -Shift; dx <= Shift; dx++)
            {
                columns[dx + Shift] = Columns(wide, Shift - dx);
            }

            for (int dy = -Shift; dy <= Shift; dy++)
            {
                for (int dx = -Shift; dx <= Shift; dx++)
                {
                    var rows = columns[dx + Shift];
                    int overlap = 0,
                        shifted = 0;
                    for (int y = 0; y < Side; y++)
                    {
                        int source = (y - dy + Shift) * Words;
                        for (int w = 0; w < Words; w++)
                        {
                            ulong value = rows[source + w];
                            shifted += PopCount(value);
                            overlap += PopCount(value & referenceRows[y * Words + w]);
                        }
                    }

                    int total = shifted + referenceCount - overlap,
                        distance = dx * dx + dy * dy + (percent - 100) * (percent - 100);
                    double score = total == 0 ? 0 : overlap / (double)total;
                    if (
                        score > bestScore + 1e-9
                        || (Math.Abs(score - bestScore) <= 1e-9 && distance < bestDistance)
                    )
                    {
                        bestScore = score;
                        bestDistance = distance;
                        bestPercent = percent;
                        bestDx = dx;
                        bestDy = dy;
                    }
                }
            }
        }

        Warp(actual, canvas, transform, bestPercent);
        using var crop = new Mat(canvas, new Rect(Shift - bestDx, Shift - bestDy, Side, Side));
        return crop.Clone();
    }

    /// <summary>以画布中心为不动点缩放，并整体右下移<see cref = "Shift"/>像素画到加宽画布上（最近邻，边界补0）。</summary>
    private static void Warp(Mat actual, Mat canvas, Mat transform, int percent)
    {
        float scale = percent / 100f;
        transform.Set(0, 0, scale);
        transform.Set(1, 1, scale);
        transform.Set(0, 2, Side / 2 * (1 - scale) + Shift);
        transform.Set(1, 2, Side / 2 * (1 - scale) + Shift);
        Cv2.WarpAffine(
            actual,
            canvas,
            transform,
            new Size(Side + 2 * Shift, Side + 2 * Shift),
            InterpolationFlags.Nearest
        );
    }

    /// <summary>把每行从<paramref name = "left"/>列起的<paramref name = "count"/>个像素（不超过128）打包成位串（非0为1），每行<see cref = "Words"/>个64位字。</summary>
    private static ulong[] Pack(byte[] data, int stride, int left, int count)
    {
        int rows = data.Length / stride;
        var packed = new ulong[rows * Words];
        for (int y = 0; y < rows; y++)
        {
            int row = y * stride + left;
            for (int x = 0; x < count; x++)
            {
                if (data[row + x] != 0)
                {
                    packed[y * Words + (x >> 6)] |= 1UL << (x & 63);
                }
            }
        }

        return packed;
    }

    /// <summary>从整行位串中取第<paramref name = "left"/>列起的<see cref = "Side"/>列（左移出界的高位清零）。</summary>
    private static ulong[] Columns(ulong[] wide, int left)
    {
        const ulong high = (1UL << (Side - 64)) - 1;
        var packed = new ulong[wide.Length];
        for (int i = 0; i < wide.Length; i += Words)
        {
            ulong low = wide[i],
                top = wide[i + 1];
            packed[i] = left == 0 ? low : (low >> left) | (top << (64 - left));
            packed[i + 1] = (top >> left) & high;
        }

        return packed;
    }

    private static int PopCount(ulong value)
    {
        value -= (value >> 1) & 0x5555555555555555UL;
        value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
        value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
        return (int)((value * 0x0101010101010101UL) >> 56);
    }

    /// <summary>复制为紧密排列的字节数组。</summary>
    private static byte[] Bytes(Mat image)
    {
        using var copy = image.IsContinuous() ? null : image.Clone();
        var source = copy ?? image;
        var bytes = new byte[source.Rows * source.Cols * source.ElemSize()];
        Marshal.Copy(source.Data, bytes, 0, bytes.Length);
        return bytes;
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
        using var raw = CvPixels.Gray(frame);
        using var gray = new Mat();
        if (options.Binarization == EGlyphBinarization.Midpoint)
        {
            // 3×3中值去掉单像素噪点，避免噪点在阈值附近产生毛边；不改变笔画位置。
            Cv2.MedianBlur(raw, gray, 3);
        }
        else
        {
            raw.CopyTo(gray);
        }

        using var mask = new Mat();
        if (options.Binarization != EGlyphBinarization.Fixed)
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

            if (options.Binarization == EGlyphBinarization.Midpoint)
            {
                // 严格小于中点视为墨迹，与固定阈值语义一致。
                Cv2.Threshold(gray, mask, (low + high) / 2.0 - 1, 255, ThresholdTypes.BinaryInv);
            }
            else
            {
                Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
            }
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
