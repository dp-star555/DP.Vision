using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OpenCv;

/// <summary>原图像素中的局部一维条纹/空隙缺陷；自参考不能证明绝对条宽或恢复整条高度的缺失。</summary>
public sealed partial class OpenCvBarcodePrintInspector : ILinearBarcodeQualityInspector
{
    /// <inheritdoc/>
    public bool RequiresDecodedStructure => false;

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

        using var raw = CvImages.Mat(frame);
        using var roi = new Mat(raw, CvImages.Rect(bounds));
        using var gray = CvImages.Gray(roi);
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
            defects = 0;
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

            int margin = Math.Min(options.EdgeTolerance, (end - start - 1) / 2);
            int left = start + margin,
                right = end - margin;
            if (right > left)
            {
                intervals++;
                using var actual = new Mat(image, new Rect(left, top, right - left, height));
                using var difference = new Mat();
                if (ink)
                {
                    Cv2.BitwiseNot(actual, difference);
                }
                else
                {
                    actual.CopyTo(difference);
                }

                using var attenuation = new Mat(height, right - left, MatType.CV_8UC1, Scalar.All(0));
                if (ink && options.DetectInkLoss)
                {
                    for (int col = left; col < right; col++)
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
                                difference.At<byte>(row, col - left) == 0
                                && intensities.At<byte>(top + row, col) - reference >= minimumLoss
                            )
                            {
                                difference.Set(row, col - left, (byte)255);
                                attenuation.Set(row, col - left, (byte)255);
                            }
                        }
                    }
                }

                using var labels = new Mat();
                using var stats = new Mat();
                using var centers = new Mat();
                int count = Cv2.ConnectedComponentsWithStats(
                    difference,
                    labels,
                    stats,
                    centers,
                    PixelConnectivity.Connectivity8,
                    MatType.CV_32S
                );
                var grayCounts = new int[count];
                if (ink && options.DetectInkLoss)
                {
                    for (int row = 0; row < height; row++)
                    {
                        for (int col = 0; col < right - left; col++)
                        {
                            if (attenuation.At<byte>(row, col) > 0)
                            {
                                grayCounts[labels.At<int>(row, col)]++;
                            }
                        }
                    }
                }

                for (int i = 1; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                    double fraction = area / (double)((right - left) * height);
                    if (area < options.MinimumArea || fraction < options.MinimumFraction)
                    {
                        continue;
                    }

                    int x = left + stats.At<int>(i, (int)ConnectedComponentsTypes.Left),
                        y = top + stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                        w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width),
                        h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                    var box = rotated
                        ? new PixelRect(bounds.X + y, bounds.Y + x, h, w)
                        : new PixelRect(bounds.X + x, bounds.Y + y, w, h);
                    findings.Add(
                        new QualityFinding(
                            ink
                                ? (grayCounts[i] > 0 ? "barcode_ink_loss" : "barcode_missing_ink")
                                : "barcode_extra_ink",
                            $"{(ink ? (grayCounts[i] > 0 ? "条内部缺墨/墨色损失" : "条内部缺墨/断条") : "空隙多墨/粘连")}：{area} 原始px²（其中{grayCounts[i]}px为二值化未识别的灰度损失）；占本条/空隙检查面积 {fraction:P2}；局部阈值判定，非ISO评级。",
                            EQualityFindingKind.Defect,
                            box,
                            area
                        )
                    );
                    defects++;
                    if (defects >= 256)
                    {
                        return findings.Concat(Review("缺陷数量达到256上限，其余缺陷未逐项列出。")).ToArray();
                    }
                }
            }

            start = end;
            ink = !ink;
        }

        findings.Add(
            new QualityFinding(
                "barcode_print_scope",
                $"已检查{intervals}个条/空隙区间，发现{defects}个超阈值墨迹缺陷；方向={(rotated ? "垂直" : "水平")}。排除主体上下各1像素及配置的条边容差；条内灰度损失检查={options.DetectInkLoss}；每列低分位墨色自参考不保证整条均匀变浅/缺失、绝对条宽、静区或ISO等级。",
                EQualityFindingKind.Information,
                bounds
            )
        );
        return findings.AsReadOnly();
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
