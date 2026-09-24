using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OpenCv;

internal static class QrQuietZoneInspection
{
    internal static IReadOnlyList<QualityFinding> Inspect(
        IImageSource frame,
        PixelRect bounds,
        int dimension,
        Mat toGrid,
        double threshold,
        double margin,
        BarcodePrintOptions options,
        CancellationToken token
    )
    {
        var findings = new List<QualityFinding>();
        if (!options.CheckQrQuietZone)
        {
            return findings;
        }

        IReadOnlyList<QualityFinding> Review()
        {
            return new[]
            {
                new QualityFinding(
                    "qr_quiet_zone_review",
                    "QR静区检查已开启，但ROI未完整包含四周4模块留白；请扩大ROI，未将缺失范围当作合格。",
                    EQualityFindingKind.Blocker,
                    bounds
                ),
            };
        }

        using var inverse = new Mat();
        Cv2.Invert(toGrid, inverse);
        var outer = new Point2d[4];
        var moduleCorners = new[]
        {
            new Point2d(-4, -4),
            new Point2d(dimension + 4, -4),
            new Point2d(dimension + 4, dimension + 4),
            new Point2d(-4, dimension + 4),
        };
        for (int i = 0; i < 4; i++)
        {
            double u = moduleCorners[i].X,
                v = moduleCorners[i].Y,
                z = inverse.At<double>(2, 0) * u + inverse.At<double>(2, 1) * v + inverse.At<double>(2, 2);
            if (Math.Abs(z) < 1e-10)
            {
                return Review();
            }

            double x =
                    (inverse.At<double>(0, 0) * u + inverse.At<double>(0, 1) * v + inverse.At<double>(0, 2))
                    / z,
                y =
                    (inverse.At<double>(1, 0) * u + inverse.At<double>(1, 1) * v + inverse.At<double>(1, 2))
                    / z;
            if (
                double.IsNaN(x)
                || double.IsNaN(y)
                || double.IsInfinity(x)
                || double.IsInfinity(y)
                || x < bounds.X - .01
                || y < bounds.Y - .01
                || x > bounds.X + bounds.Width + .01
                || y > bounds.Y + bounds.Height + .01
            )
            {
                return Review();
            }

            outer[i] = new Point2d(x, y);
        }

        int left = Math.Max(bounds.X, (int)Math.Floor(outer.Min(p => p.X))),
            top = Math.Max(bounds.Y, (int)Math.Floor(outer.Min(p => p.Y))),
            right = Math.Min(bounds.X + bounds.Width, (int)Math.Ceiling(outer.Max(p => p.X))),
            bottom = Math.Min(bounds.Y + bounds.Height, (int)Math.Ceiling(outer.Max(p => p.Y)));
        using var raw = CvPixels.Mat(frame);
        using var roi = new Mat(raw, new Rect(left, top, right - left, bottom - top));
        using var gray = CvPixels.Gray(roi);
        using var difference = new Mat();
        Cv2.Threshold(gray, difference, threshold, 255, ThresholdTypes.BinaryInv);
        var h = new double[9];
        for (int i = 0; i < 9; i++)
        {
            h[i] = toGrid.At<double>(i / 3, i % 3);
        }

        int w = difference.Cols,
            height = difference.Rows;
        var pixels = new byte[w * height];
        System.Runtime.InteropServices.Marshal.Copy(difference.Data, pixels, 0, pixels.Length);
        int checkedPixels = 0;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                double ox = x + left,
                    oy = y + top,
                    z = h[6] * ox + h[7] * oy + h[8];
                int index = y * w + x;
                if (Math.Abs(z) < 1e-10)
                {
                    pixels[index] = 0;
                    continue;
                }

                double u = (h[0] * ox + h[1] * oy + h[2]) / z,
                    v = (h[3] * ox + h[4] * oy + h[5]) / z;
                bool quiet =
                    u >= -4
                    && v >= -4
                    && u < dimension + 4
                    && v < dimension + 4
                    && (u < -margin || v < -margin || u >= dimension + margin || v >= dimension + margin);
                if (!quiet)
                {
                    pixels[index] = 0;
                }
                else
                {
                    checkedPixels++;
                }
            }
        }

        if (checkedPixels == 0)
        {
            return Review();
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, difference.Data, pixels.Length);
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
        for (int i = 1; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < options.MinimumArea)
            {
                continue;
            }

            var box = new PixelRect(
                left + stats.At<int>(i, (int)ConnectedComponentsTypes.Left),
                top + stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Height)
            );
            findings.Add(
                new QualityFinding(
                    "qr_quiet_zone_ink",
                    $"QR四模块静区墨迹：{area}原始px²；按连通域面积判断，不以整个静区比例稀释。",
                    EQualityFindingKind.Defect,
                    box,
                    area
                )
            );
            if (findings.Count == 256)
            {
                findings.Add(
                    new QualityFinding(
                        "qr_quiet_zone_review",
                        "静区缺陷达到256条显示上限。",
                        EQualityFindingKind.Blocker,
                        bounds
                    )
                );
                break;
            }
        }

        findings.Add(
            new QualityFinding(
                "qr_quiet_zone_scope",
                $"已检查QR四周4模块静区，{checkedPixels}原始像素；码体边界按配置容差排除，非ISO评级。",
                EQualityFindingKind.Information,
                bounds
            )
        );
        return findings.AsReadOnly();
    }
}
