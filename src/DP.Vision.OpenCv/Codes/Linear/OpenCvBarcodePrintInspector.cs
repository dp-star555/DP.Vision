using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OpenCv;

/// <summary>原图像素中的局部一维条纹/空隙缺陷：只触及边缘带的差异视为印刷波动，深入条/空隙内部的差异完整计入；自参考不能证明绝对条宽或恢复整条高度的缺失。</summary>
public sealed partial class OpenCvBarcodePrintInspector : ILinearBarcodeQualityInspector
{
    /// <inheritdoc/>
    public bool RequiresDecodedStructure => false;

    /// <summary>原图未能直接读出、经修复预处理才读出的码：内容可信，但原图可读性余量不足，作为印刷缺陷报告。</summary>
    internal static QualityFinding? DecodeAssisted(
        IReadOnlyList<BarcodeObservation> symbols,
        PixelRect bounds
    )
    {
        return symbols.Count == 1 && symbols[0].Preprocessing.Length > 0
            ? new QualityFinding(
                "barcode_decode_assisted",
                $"原图未能直接读出，经“{symbols[0].Preprocessing}”预处理后读出：内容可信，但可读性余量不足，现场扫描器可能读取失败。",
                EQualityFindingKind.Defect,
                bounds
            )
            : null;
    }

    /// <inheritdoc/>
    public BarcodeQualityResult Inspect(
        IImageSource frame,
        PixelRect bounds,
        IReadOnlyList<BarcodeObservation> symbols,
        BarcodePrintOptions options,
        CancellationToken token = default
    )
    {
        if (frame == null || options == null || symbols == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        token.ThrowIfCancellationRequested();
        if (frame.Info.Layout != EPixelLayout.Gray8 && frame.Info.Layout != EPixelLayout.Bgr24)
        {
            return new BarcodeQualityResult(
                options.Enabled,
                new[]
                {
                    new QualityFinding(
                        "pixel_layout_unsupported",
                        "This implementation requires Gray8 or Bgr24.",
                        EQualityFindingKind.Blocker,
                        bounds
                    ),
                }
            );
        }

        return new BarcodeQualityResult(options.Enabled, InspectCore(frame, bounds, symbols, options, token));
    }

    /// <inheritdoc/>
    private IReadOnlyList<QualityFinding> InspectCore(
        IImageSource frame,
        PixelRect bounds,
        IReadOnlyList<BarcodeObservation> symbols,
        BarcodePrintOptions options,
        CancellationToken token
    )
    {
        if (frame == null || symbols == null || options == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (!bounds.Fits(frame))
        {
            throw new ArgumentException("Barcode ROI outside image.");
        }

        token.ThrowIfCancellationRequested();
        var findings = new List<QualityFinding>();
        if (!options.Enabled)
        {
            return findings;
        }

        IReadOnlyList<QualityFinding> Review(string reason)
        {
            return new[]
            {
                new QualityFinding("barcode_print_review", reason, EQualityFindingKind.Blocker, bounds),
            };
        }

        if (symbols.Count > 1)
        {
            return Review("多个条码符号，未确定独立打印检查范围，请分别框选。");
        }

        if (symbols.Count == 1 && symbols[0].Format == "QR_CODE")
        {
            return Review("QR input requires the separate QR quality implementation.");
        }

        if (
            symbols.Count == 1
            && !new[]
            {
                "CODE_128",
                "CODE_39",
                "CODE_93",
                "EAN_13",
                "EAN_8",
                "UPC_A",
                "UPC_E",
                "ITF",
                "CODABAR",
            }.Contains(symbols[0].Format)
        )
        {
            return Review("当前打印缺陷检查支持一维条码与QR Code；DataMatrix及其他码制外观未检查。");
        }

        if (DecodeAssisted(symbols, bounds) is { } assisted)
        {
            findings.Add(assisted);
        }

        using var raw = CvPixels.Mat(frame);
        using var roi = new Mat(raw, CvPixels.Rect(bounds));
        using var gray = CvPixels.Gray(roi);
        Cv2.MinMaxLoc(gray, out double min, out double max);
        if (max - min < 40)
        {
            return Review("条码ROI对比度不足，无法可靠分离墨迹。");
        }

        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var transposed = new Mat();
        Cv2.Transpose(mask, transposed);
        var horizontal = FindBand(mask, token);
        var vertical = FindBand(transposed, token);
        if (horizontal == null && vertical == null)
        {
            return Review(
                "未定位到可靠打印检查结构；无有效解码时不能确认码制，请明确选择一维条码或二维码ROI。"
            );
        }

        if (horizontal != null && vertical != null)
        {
            return Review("存在多个方向的条纹候选，打印几何不确定。");
        }

        bool rotated = horizontal == null;
        var image = rotated ? transposed : mask;
        var band = horizontal ?? vertical!;
        using var transposedGray = new Mat();
        if (rotated)
        {
            Cv2.Transpose(gray, transposedGray);
        }

        var intensities = rotated ? transposedGray : gray;
        int top = band.Top + 1,
            bottom = band.Bottom - 1,
            height = bottom - top;
        var profile = band.Profile;
        int first = Array.FindIndex(profile, v => v),
            last = Array.FindLastIndex(profile, v => v);
        if (first < 1 || last >= image.Cols - 1)
        {
            return Review("条纹到达ROI左右边界，可能裁切；请包含完整条码及空白边缘。");
        }

        int intervals = 0,
            defects = 0,
            thinElements = 0,
            grayUnmeasured = 0,
            endVariations = 0;
        int edge = options.EdgeTolerance;
        bool ink = true;
        int start = first;
        while (start <= last)
        {
            token.ThrowIfCancellationRequested();
            int end = start + 1;
            while (end <= last && profile[end] == ink)
            {
                end++;
            }

            int width = end - start;
            // 细条/细空隙（半宽不超过边缘带）没有“边缘带以外的内部”：只有横贯整条宽度的断裂才能与边缘波动区分。
            bool thin = width / 2.0 < edge + 1;
            if (thin)
            {
                thinElements++;
            }

            intervals++;
            using var actual = new Mat(image, new Rect(start, top, width, height));
            using var difference = new Mat();
            if (ink)
            {
                Cv2.BitwiseNot(actual, difference);
            }
            else
            {
                actual.CopyTo(difference);
            }

            using var attenuation = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
            // 去掉两侧边缘带后的内部不足MinimumGradedElement（同指示性扫描等级的最小元素要求）时，
            // 内部反射率由成像模糊和像素相位主导，灰度起伏无法与印刷变浅区分：只按二值化缺墨/断裂判定。
            bool grayMeasurable = width - 2 * edge >= MinimumGradedElement;
            if (!grayMeasurable && ink)
            {
                grayUnmeasured++;
            }

            if (ink && options.DetectInkLoss && grayMeasurable)
            {
                for (int col = start; col < end; col++)
                {
                    token.ThrowIfCancellationRequested();
                    var values = new byte[height];
                    for (int row = 0; row < height; row++)
                    {
                        values[row] = intensities.At<byte>(top + row, col);
                    }

                    Array.Sort(values);
                    double reference = values[(height - 1) / 10],
                        contrast = max - reference;
                    if (contrast < 40)
                    {
                        continue;
                    }

                    double minimumLoss = Math.Max(20, contrast * options.MinimumInkLoss);
                    for (int row = 0; row < height; row++)
                    {
                        if (
                            difference.At<byte>(row, col - start) == 0
                            && intensities.At<byte>(top + row, col) - reference >= minimumLoss
                        )
                        {
                            difference.Set(row, col - start, (byte)255);
                            attenuation.Set(row, col - start, (byte)255);
                        }
                    }
                }
            }

            // 深度：到本条/空隙左右边缘及上下端的距离。只触及边缘带（EdgeTolerance）的差异视为印刷波动。
            int Depth(int row, int col)
            {
                return Math.Min(Math.Min(col + 1, width - col), Math.Min(row + 1, height - row));
            }

            using var labels = new Mat();
            int count = Cv2.ConnectedComponents(
                difference,
                labels,
                PixelConnectivity.Connectivity8,
                MatType.CV_32S
            );
            var deepest = new int[count];
            var nearestEnd = Enumerable.Repeat(int.MaxValue, count).ToArray();
            var farthestEnd = new int[count];
            var spanRows = new Dictionary<(int Label, int Row), int>();
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int label = labels.At<int>(row, col);
                    if (label == 0)
                    {
                        continue;
                    }

                    deepest[label] = Math.Max(deepest[label], Depth(row, col));
                    int fromEnd = Math.Min(row + 1, height - row);
                    nearestEnd[label] = Math.Min(nearestEnd[label], fromEnd);
                    // 两侧边缘带内的像素本就不计入，条端区只按条内部列判断（细条没有内部列，按全部像素）。
                    if (thin || Math.Min(col + 1, width - col) > edge)
                    {
                        farthestEnd[label] = Math.Max(farthestEnd[label], fromEnd);
                    }
                    if (thin && Math.Min(row + 1, height - row) > edge)
                    {
                        spanRows.TryGetValue((label, row), out int n);
                        spanRows[(label, row)] = n + 1;
                    }
                }
            }

            // 条端区：条高的EndZoneFraction（至少边缘带）。从条端开始且完全落在条端区内的差异是条长/条端模糊的波动
            // （各条端点不齐、端部渐淡），不影响扫描，不计入；超出条端区或不接触条端的缺陷仍按完整面积计入。
            int endZone = Math.Max(edge, (int)Math.Round(height * EndZoneFraction));
            var significant = new bool[count];
            for (int i = 1; i < count; i++)
            {
                if (nearestEnd[i] <= edge + 1 && farthestEnd[i] <= endZone)
                {
                    endVariations++;
                    continue;
                }

                significant[i] = thin
                    ? spanRows.Any(e => e.Key.Label == i && e.Value == width)
                    : deepest[i] > edge;
            }

            // 计入范围：宽条保留超出边缘带的全部像素，并在其附近恢复边缘带宽度，不把相连的整条毛边一起计入；
            // 细条断裂整体计入。
            using var core = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int label = labels.At<int>(row, col);
                    if (label > 0 && significant[label] && (thin || Depth(row, col) > edge))
                    {
                        core.Set(row, col, (byte)255);
                    }
                }
            }

            using (
                var grow = Cv2.GetStructuringElement(
                    MorphShapes.Rect,
                    new Size(2 * (edge + 1) + 1, 2 * (edge + 1) + 1)
                )
            )
            {
                Cv2.Dilate(core, core, grow);
            }

            var area = new int[count];
            var grayCounts = new int[count];
            var minX = Enumerable.Repeat(int.MaxValue, count).ToArray();
            var minY = Enumerable.Repeat(int.MaxValue, count).ToArray();
            var maxX = new int[count];
            var maxY = new int[count];
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int label = labels.At<int>(row, col);
                    if (label == 0 || !significant[label] || core.At<byte>(row, col) == 0)
                    {
                        continue;
                    }

                    area[label]++;
                    if (attenuation.At<byte>(row, col) > 0)
                    {
                        grayCounts[label]++;
                    }

                    minX[label] = Math.Min(minX[label], col);
                    minY[label] = Math.Min(minY[label], row);
                    maxX[label] = Math.Max(maxX[label], col);
                    maxY[label] = Math.Max(maxY[label], row);
                }
            }

            for (int i = 1; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                double fraction = area[i] / (double)(width * height);
                if (!significant[i] || area[i] < options.MinimumArea || fraction < options.MinimumFraction)
                {
                    continue;
                }

                int x = start + minX[i],
                    y = top + minY[i],
                    w = maxX[i] - minX[i] + 1,
                    h = maxY[i] - minY[i] + 1;
                var box = rotated
                    ? new PixelRect(bounds.X + y, bounds.Y + x, h, w)
                    : new PixelRect(bounds.X + x, bounds.Y + y, w, h);
                findings.Add(
                    new QualityFinding(
                        ink
                            ? (grayCounts[i] > 0 ? "barcode_ink_loss" : "barcode_missing_ink")
                            : "barcode_extra_ink",
                        $"{(ink ? (grayCounts[i] > 0 ? "条内部缺墨/墨色损失" : "条内部缺墨/断条") : "空隙多墨/粘连")}{(thin ? "（细条横贯断裂）" : "")}：{area[i]} 原始px²（其中{grayCounts[i]}px为二值化未识别的灰度损失）；占本条/空隙面积 {fraction:P2}；局部阈值判定，非ISO评级。",
                        EQualityFindingKind.Defect,
                        box,
                        area[i]
                    )
                );
                defects++;
                if (defects >= 256)
                {
                    return findings.Concat(Review("缺陷数量达到256上限，其余缺陷未逐项列出。")).ToArray();
                }
            }

            start = end;
            ink = !ink;
        }

        if (ScanGrade(intensities, profile, first, last, band.Top, band.Bottom, token) is { } grade)
        {
            var bandBox = rotated
                ? new PixelRect(
                    bounds.X + band.Top,
                    bounds.Y + first,
                    band.Bottom - band.Top,
                    last - first + 1
                )
                : new PixelRect(
                    bounds.X + first,
                    bounds.Y + band.Top,
                    last - first + 1,
                    band.Bottom - band.Top
                );
            findings.Add(
                new QualityFinding(
                    grade.Letter == 'F' ? "barcode_scan_grade_fail" : "barcode_scan_grade",
                    grade.Message,
                    grade.Letter == 'F' ? EQualityFindingKind.Defect : EQualityFindingKind.Information,
                    bandBox
                )
            );
        }

        findings.Add(
            new QualityFinding(
                "barcode_print_scope",
                $"已检查{intervals}个条/空隙区间，发现{defects}个超阈值墨迹缺陷；方向={(rotated ? "垂直" : "水平")}。只触及条/空隙边缘带（{edge}像素）的差异视为印刷波动；从条端开始且不超过条高{EndZoneFraction:P0}的差异视为条端长短/渐淡（本次{endVariations}处），不计入；深入内部的缺墨/多墨按完整面积计入。{thinElements}个细条/细空隙没有边缘带以外的内部，只检查二值化后横贯整条宽度的断裂。条内灰度损失检查={options.DetectInkLoss}，其中{grayUnmeasured}个条去掉边缘带后内部窄于{MinimumGradedElement}像素，灰度起伏无法与成像模糊区分，只按二值化缺墨判定；每列低分位墨色自参考不保证整条均匀变浅/缺失、绝对条宽、静区或ISO等级。",
                EQualityFindingKind.Information,
                bounds
            )
        );
        return findings.AsReadOnly();
    }

    /// <summary>
    /// 参照ISO/IEC 15416扫描反射率曲线的指示性分级：在条高内均匀取10条扫描线，按约0.8倍最窄元素宽度的测量孔径平均，
    /// 计算符号反差SC、最小反射率、最小边缘反差EC、调制比MOD与缺陷度（元素内反射率起伏ERN/SC）。
    /// 每条线取各参数最低等级，符号等级取各线平均。灰度未经反射率标定，只作可读性余量指示，不是ISO认证等级。
    /// </summary>
    private static (char Letter, string Message)? ScanGrade(
        Mat gray,
        bool[] profile,
        int first,
        int last,
        int bandTop,
        int bandBottom,
        CancellationToken token
    )
    {
        var runs = new List<int>();
        for (int x = first, s = first; x <= last + 1; x++)
        {
            if (x > last || profile[x] != profile[s])
            {
                runs.Add(x - s);
                s = x;
            }
        }

        if (runs.Count < 5)
        {
            return null;
        }

        runs.Sort();
        double narrow = Math.Max(1, runs[runs.Count / 10]);
        if (narrow < MinimumGradedElement)
        {
            return (
                '-',
                $"扫描等级未评定：最窄元素约{narrow:0}px，低于{MinimumGradedElement}px，成像分辨率不足以区分印刷与模糊造成的反射率变化；请提高分辨率或只参考读码结果与局部缺陷。"
            );
        }

        int aperture = Math.Max(1, (int)Math.Round(.8 * narrow)),
            half = aperture / 2;
        // 扫描线两端各取最多10倍最窄元素宽度的静区，受ROI限制。
        int quiet = (int)Math.Ceiling(10 * narrow),
            from = Math.Max(0, first - quiet),
            to = Math.Min(gray.Cols - 1, last + quiet);
        int expectedBars = 0;
        for (int x = first; x <= last; x++)
        {
            if (profile[x] && (x == first || !profile[x - 1]))
            {
                expectedBars++;
            }
        }

        double total = 0;
        int lines = 0;
        double worstSc = 100,
            worstMod = 1,
            worstDefects = 0,
            worstEc = 100,
            worstRmin = 0;
        int edgeFailures = 0;
        for (int k = 0; k < 10; k++)
        {
            token.ThrowIfCancellationRequested();
            int row = bandTop + (int)Math.Round((bandBottom - bandTop) * (.1 + .8 * k / 9.0));
            var r = new double[to - from + 1];
            for (int x = from; x <= to; x++)
            {
                double sum = 0;
                int n = 0;
                for (int dy = -half; dy <= half; dy++)
                {
                    int yy = Math.Min(gray.Rows - 1, Math.Max(0, row + dy));
                    for (int dx = -half; dx <= half; dx++)
                    {
                        int xx = Math.Min(gray.Cols - 1, Math.Max(0, x + dx));
                        sum += gray.At<byte>(yy, xx);
                        n++;
                    }
                }

                r[x - from] = sum / n / 255.0 * 100;
            }

            double rmax = r.Max(),
                rmin = r.Min(),
                sc = rmax - rmin,
                gt = rmin + sc / 2;
            // 以全局阈值GT分割元素，记录每个元素的极值及元素内起伏。
            var extremes = new List<double>();
            var ern = new List<double>();
            int bars = 0;
            for (int i = 0; i < r.Length; )
            {
                bool isBar = r[i] < gt;
                int j = i;
                double lo = r[i],
                    hi = r[i];
                while (j < r.Length && r[j] < gt == isBar)
                {
                    lo = Math.Min(lo, r[j]);
                    hi = Math.Max(hi, r[j]);
                    j++;
                }

                bool quietZone = i == 0 || j == r.Length;
                extremes.Add(isBar ? lo : hi);
                if (!quietZone)
                {
                    // 元素内起伏只看元素内部的局部峰谷，不含两侧边缘过渡：条内取最高内部峰值，空隙内取最低内部谷值。
                    double inner = isBar ? lo : hi;
                    for (int m = i + 1; m < j - 1; m++)
                    {
                        if (isBar && r[m] >= r[m - 1] && r[m] >= r[m + 1])
                        {
                            inner = Math.Max(inner, r[m]);
                        }
                        else if (!isBar && r[m] <= r[m - 1] && r[m] <= r[m + 1])
                        {
                            inner = Math.Min(inner, r[m]);
                        }
                    }

                    ern.Add(isBar ? inner - lo : hi - inner);
                }

                if (isBar)
                {
                    bars++;
                }

                i = j;
            }

            double ecMin = 100;
            for (int i = 1; i < extremes.Count; i++)
            {
                ecMin = Math.Min(ecMin, Math.Abs(extremes[i] - extremes[i - 1]));
            }

            double mod = sc <= 0 ? 0 : ecMin / sc,
                defects = sc <= 0 || ern.Count == 0 ? 1 : ern.Max() / sc;
            bool edgesOk = bars == expectedBars;
            int grade = Math.Min(
                Math.Min(Grade(sc, 70, 55, 40, 20, true), rmin <= .5 * rmax ? 4 : 0),
                Math.Min(
                    Math.Min(ecMin >= 15 ? 4 : 0, Grade(mod, .70, .60, .50, .40, true)),
                    Math.Min(Grade(defects, .15, .20, .25, .30, false), edgesOk ? 4 : 0)
                )
            );
            total += grade;
            lines++;
            worstSc = Math.Min(worstSc, sc);
            worstMod = Math.Min(worstMod, mod);
            worstDefects = Math.Max(worstDefects, defects);
            worstEc = Math.Min(worstEc, ecMin);
            worstRmin = Math.Max(worstRmin, rmax <= 0 ? 1 : rmin / rmax);
            if (!edgesOk)
            {
                edgeFailures++;
            }
        }

        double average = total / lines;
        char letter =
            average >= 3.5 ? 'A'
            : average >= 2.5 ? 'B'
            : average >= 1.5 ? 'C'
            : average >= .5 ? 'D'
            : 'F';
        return (
            letter,
            $"扫描等级（指示性，参照ISO/IEC 15416，灰度未标定）：{letter}（{average:0.0}/4，10条扫描线，测量孔径{aperture}px≈0.8×最窄元素{narrow:0}px）。"
                + $"最差值：符号反差{worstSc:0}%、最小边缘反差{worstEc:0}%、调制比{worstMod:0.00}、缺陷度{worstDefects:0.00}（元素内起伏/符号反差，A≤0.15、C≤0.25、F>0.30）、Rmin/Rmax {worstRmin:0.00}；"
                + $"{edgeFailures}条扫描线条数与结构不符。孔径会平滑小于约0.8倍最窄元素的斑点，局部缺陷另见逐项结果。"
        );
    }

    /// <summary>评定扫描等级所需的最窄元素像素数；更细时模糊主导反射率曲线，等级无意义。</summary>
    private const int MinimumGradedElement = 4;

    /// <summary>条端区占条高的比例：从条端开始且不超过此深度的缺墨/变浅视为条端长度波动。</summary>
    private const double EndZoneFraction = .1;

    private static int Grade(double value, double a, double b, double c, double d, bool higherIsBetter)
    {
        return higherIsBetter
            ? value >= a
                ? 4
                : value >= b
                    ? 3
                    : value >= c
                        ? 2
                        : value >= d
                            ? 1
                            : 0
            : value <= a
                ? 4
                : value <= b
                    ? 3
                    : value <= c
                        ? 2
                        : value <= d
                            ? 1
                            : 0;
    }

    private static Band? FindBand(Mat image, CancellationToken token)
    {
        if (image.Cols < 40 || image.Rows < 12)
        {
            return null;
        }

        var rows = new List<int>();
        for (int y = 0; y < image.Rows; y++)
        {
            token.ThrowIfCancellationRequested();
            int transitions = 0,
                ink = 0;
            for (int x = 0; x < image.Cols; x++)
            {
                if (image.At<byte>(y, x) > 0)
                {
                    ink++;
                }

                if (x > 0 && image.At<byte>(y, x) != image.At<byte>(y, x - 1))
                {
                    transitions++;
                }
            }

            if (transitions >= 24 && ink > image.Cols * .08 && ink < image.Cols * .85)
            {
                rows.Add(y);
            }
        }

        if (rows.Count < 10)
        {
            return null;
        }

        var groups = new List<List<int>>();
        foreach (int y in rows)
        {
            bool separate = groups.Count == 0;
            if (!separate)
            {
                int previous = groups[groups.Count - 1].Last(),
                    gap = y - previous;
                separate = gap > Math.Max(3, image.Rows / 6);
                if (!separate && gap > 1)
                {
                    int different = 0;
                    for (int x = 0; x < image.Cols; x++)
                    {
                        if (image.At<byte>(y, x) != image.At<byte>(previous, x))
                        {
                            different++;
                        }
                    }

                    separate = different > image.Cols * .15;
                }
            }

            if (separate)
            {
                groups.Add(new List<int>());
            }

            groups[groups.Count - 1].Add(y);
        }

        var candidates = new List<Band>();
        foreach (var group in groups.Where(g => g.Count >= 10))
        {
            int top = group[0],
                bottom = group.Last() + 1,
                height = bottom - top;
            if (height < 12 || group.Count < height * .65)
            {
                continue;
            }

            var profile = new bool[image.Cols];
            int uncertain = 0,
                runs = 0;
            for (int x = 0; x < image.Cols; x++)
            {
                int n = 0;
                for (int y = top; y < bottom; y++)
                {
                    if (image.At<byte>(y, x) > 0)
                    {
                        n++;
                    }
                }

                double p = n / (double)height;
                profile[x] = p >= .5;
                if (p > .2 && p < .8)
                {
                    uncertain++;
                }

                if (profile[x] && (x == 0 || !profile[x - 1]))
                {
                    runs++;
                }
            }

            if (runs >= 12 && uncertain <= image.Cols * .15)
            {
                candidates.Add(new Band(top, bottom, profile));
            }
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
