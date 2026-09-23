using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OpenCv;

/// <summary>原图上的QR模块内部印刷缺陷；实测多数颜色不是纠错后的模块真值。</summary>
public sealed class OpenCvQrPrintInspector : IQrQualityInspector
{
    /// <inheritdoc/>
    public bool RequiresDecodedStructure => true;

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
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (symbols == null)
        {
            throw new ArgumentNullException(nameof(symbols));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (!bounds.Fits(frame))
        {
            throw new ArgumentException("QR ROI outside image.");
        }

        token.ThrowIfCancellationRequested();
        var findings = new List<QualityFinding>();
        if (!options.Enabled)
        {
            return findings;
        }

        IReadOnlyList<QualityFinding> Review(string message)
        {
            return new[]
            {
                new QualityFinding("barcode_print_review", message, EQualityFindingKind.Blocker, bounds),
            };
        }

        if (symbols.Count != 1 || symbols[0].Format != "QR_CODE" || symbols[0].ModuleGrid == null)
        {
            return Review("未取得唯一QR的可靠模块网格；二维码内容可读不代表打印外观已检查。");
        }

        var grid = symbols[0].ModuleGrid!;
        int n = grid.Dimension;
        var corners = new Point2f[4];
        for (int i = 0; i < 4; i++)
        {
            float x = (float)(grid.Corners[i * 2] - .5),
                y = (float)(grid.Corners[i * 2 + 1] - .5);
            if (
                x < bounds.X - .6
                || y < bounds.Y - .6
                || x > bounds.X + bounds.Width - .4
                || y > bounds.Y + bounds.Height - .4
            )
            {
                return Review("QR模块网格超出ROI，可能裁切。");
            }

            corners[i] = new Point2f(x, y);
        }

        double minScale = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            minScale = Math.Min(
                minScale,
                Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) / n
            );
        }

        if (minScale < 4)
        {
            return Review("二维码每模块不足4原始像素，无法可靠区分模块边缘与墨迹缺陷。");
        }

        using var transform = Cv2.GetPerspectiveTransform(
            corners,
            new[] { new Point2f(0, 0), new Point2f(n, 0), new Point2f(n, n), new Point2f(0, n) }
        );
        var h = new double[9];
        for (int i = 0; i < 9; i++)
        {
            h[i] = transform.At<double>(i / 3, i % 3);
        }

        if (h.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
        {
            return Review("QR网格变换无效。");
        }

        int left = Math.Max(bounds.X, (int)Math.Floor(corners.Min(p => p.X))),
            top = Math.Max(bounds.Y, (int)Math.Floor(corners.Min(p => p.Y)));
        int right = Math.Min(bounds.X + bounds.Width, (int)Math.Ceiling(corners.Max(p => p.X))),
            bottom = Math.Min(bounds.Y + bounds.Height, (int)Math.Ceiling(corners.Max(p => p.Y)));
        if (right <= left || bottom <= top)
        {
            return Review("QR检查范围为空。");
        }

        using var raw = CvPixels.Mat(frame);
        using var roi = new Mat(raw, new Rect(left, top, right - left, bottom - top));
        using var gray = CvPixels.Gray(roi);
        Cv2.MinMaxLoc(gray, out double minimum, out double maximum);
        if (maximum - minimum < 40)
        {
            return Review("QR对比度不足。");
        }

        using var mask = new Mat();
        double threshold = Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        int w = mask.Cols,
            height = mask.Rows,
            cells = n * n;
        var pixels = new byte[w * height];
        System.Runtime.InteropServices.Marshal.Copy(mask.Data, pixels, 0, pixels.Length);
        var assigned = Enumerable.Repeat(-1, pixels.Length).ToArray();
        var total = new int[cells];
        var ink = new int[cells];
        double margin = Math.Min(.2, options.EdgeTolerance / minScale);
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                double ox = x + left,
                    oy = y + top,
                    z = h[6] * ox + h[7] * oy + h[8];
                if (Math.Abs(z) < 1e-10)
                {
                    continue;
                }

                double u = (h[0] * ox + h[1] * oy + h[2]) / z,
                    v = (h[3] * ox + h[4] * oy + h[5]) / z;
                if (u < 0 || v < 0 || u >= n || v >= n)
                {
                    continue;
                }

                int col = (int)u,
                    row = (int)v;
                double fx = u - col,
                    fy = v - row;
                if (fx < margin || fx > 1 - margin || fy < margin || fy > 1 - margin)
                {
                    continue;
                }

                int cell = row * n + col,
                    index = y * w + x;
                assigned[index] = cell;
                total[cell]++;
                if (pixels[index] > 0)
                {
                    ink[cell]++;
                }
            }
        }

        if (total.Any(t => t < 4))
        {
            return Review("部分QR模块有效像素不足；网格、裁切或分辨率不可靠。");
        }

        var black = new bool[cells];
        var ambiguous = new bool[cells];
        int disagree = 0,
            uncertain = 0;
        for (int i = 0; i < cells; i++)
        {
            double fraction = ink[i] / (double)total[i];
            black[i] = fraction >= .5;
            ambiguous[i] = fraction >= .4 && fraction <= .6;
            if (ambiguous[i])
            {
                uncertain++;
            }

            if (black[i] != grid.SampledModules[i])
            {
                disagree++;
            }
        }

        if (disagree > Math.Max(3, cells * .05))
        {
            return Review("模块多数像素与检测网格差异过大，可能网格偏移或严重损伤，未强行判断。");
        }

        // 只有固定功能模块具有独立预期状态；不能通过重新编码推断数据模块真值。
        var functional = QrFunctionalPattern.Create(n);
        int structural = 0;
        for (int i = 0; i < cells; i++)
        {
            if (functional[i].HasValue)
            {
                structural++;
                black[i] = functional[i]!.Value;
                if (ambiguous[i])
                {
                    ambiguous[i] = false;
                    uncertain--;
                }
            }
        }

        var count = new int[cells];
        var minX = Enumerable.Repeat(int.MaxValue, cells).ToArray();
        var minY = Enumerable.Repeat(int.MaxValue, cells).ToArray();
        var maxX = new int[cells];
        var maxY = new int[cells];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int index = y * w + x,
                    cell = assigned[index];
                if (cell < 0 || ambiguous[cell] || (pixels[index] > 0) == black[cell])
                {
                    continue;
                }

                count[cell]++;
                minX[cell] = Math.Min(minX[cell], x);
                minY[cell] = Math.Min(minY[cell], y);
                maxX[cell] = Math.Max(maxX[cell], x);
                maxY[cell] = Math.Max(maxY[cell], y);
            }
        }

        int defects = 0;
        for (int i = 0; i < cells; i++)
        {
            token.ThrowIfCancellationRequested();
            if (count[i] < options.MinimumArea || count[i] / (double)total[i] < options.MinimumFraction)
            {
                continue;
            }

            var box = new PixelRect(
                left + minX[i],
                top + minY[i],
                maxX[i] - minX[i] + 1,
                maxY[i] - minY[i] + 1
            );
            findings.Add(
                new QualityFinding(
                    black[i] ? "qr_missing_ink" : "qr_extra_ink",
                    $"QR模块({i / n},{i % n}){(black[i] ? "内部缺墨" : "内部多墨")}：{count[i]}原始px²，占模块内部检查像素{count[i] / (double)total[i]:P2}；{(functional[i].HasValue ? "固定结构规则参考" : "模块多数像素自参考")}，模块内聚合差异，非ISO评级。",
                    EQualityFindingKind.Defect,
                    box,
                    count[i]
                )
            );
            defects++;
            if (defects == 256)
            {
                findings.Add(
                    new QualityFinding(
                        "barcode_print_review",
                        "QR缺陷达到256条显示上限。",
                        EQualityFindingKind.Blocker,
                        bounds
                    )
                );
                break;
            }
        }

        findings.AddRange(
            QrQuietZoneInspection.Inspect(frame, bounds, n, transform, threshold, margin, options, token)
        );
        if (uncertain > 0)
        {
            findings.Add(
                new QualityFinding(
                    "qr_module_review",
                    $"{uncertain}个模块黑白占比接近，不能可靠判断原始模块状态。",
                    EQualityFindingKind.Blocker,
                    bounds
                )
            );
        }

        findings.Add(
            new QualityFinding(
                "qr_print_scope",
                $"QR {n}×{n}模块，{defects}处超阈值局部墨迹候选。其中{structural}个固定结构模块按QR规则检查，可检出其整模块翻转；数据区仍按多数像素自参考。不检验数据区完整模块翻转、纠错前后差异或ISO等级；静区启用={options.CheckQrQuietZone}，结果独立列出。",
                EQualityFindingKind.Information,
                bounds
            )
        );
        return findings.AsReadOnly();
    }
}
