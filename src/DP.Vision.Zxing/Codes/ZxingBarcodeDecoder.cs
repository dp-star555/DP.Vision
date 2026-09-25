using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using ZXing;
using ZXing.Common;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.Zxing;

/// <summary>独立于OpenCV、HALCON和UI的托管ZXing解码器，不代表提供ISO印刷等级。</summary>
public sealed class ZxingBarcodeDecoder : DP.Vision.Algorithms.IBarcodeReader
{
    /// <inheritdoc/>
    public BarcodeReadResult Read(IImageSource frame, PixelRect bounds, CancellationToken token = default)
    {
        return new BarcodeReadResult(Decode(frame, bounds, token));
    }

    /// <inheritdoc/>
    private IReadOnlyList<BarcodeObservation> Decode(
        IImageSource frame,
        PixelRect bounds,
        CancellationToken token
    )
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (!bounds.Fits(frame))
        {
            throw new ArgumentException("Barcode ROI outside image.");
        }

        token.ThrowIfCancellationRequested();
        if (frame.Info.Layout != EPixelLayout.Gray8 && frame.Info.Layout != EPixelLayout.Bgr24)
        {
            throw new NotSupportedException("Reader supports Gray8 and Bgr24.");
        }

        var pixels = new byte[frame.Info.ByteLength];
        frame.CopyTo(0, pixels, 0, pixels.Length);
        var gray = new byte[bounds.Width * bounds.Height];
        int channels = frame.Info.Layout == EPixelLayout.Gray8 ? 1 : 3;
        for (int y = 0; y < bounds.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < bounds.Width; x++)
            {
                int offset = (y + bounds.Y) * frame.Info.Stride + (x + bounds.X) * channels;
                gray[y * bounds.Width + x] =
                    channels == 1
                        ? pixels[offset]
                        : (byte)(
                            (29 * pixels[offset] + 150 * pixels[offset + 1] + 77 * pixels[offset + 2] + 128)
                            >> 8
                        );
            }
        }

        var direct = Decode(gray, bounds.Width, bounds.Height, 1, "", bounds, token);
        if (direct.Count > 0)
        {
            return direct;
        }

        // 原图未读出时依次尝试有限的修复预处理；只接受唯一结果，并记录所用预处理，
        // 由质量检查报告“原图可读性余量不足”，而不是当作原图直接可读。
        foreach (var (name, scale, repair) in Repairs)
        {
            token.ThrowIfCancellationRequested();
            var (pixels2, width, height) = repair(gray, bounds.Width, bounds.Height);
            var repaired = Decode(pixels2, width, height, scale, name, bounds, token);
            if (repaired.Count == 1)
            {
                return repaired;
            }
        }

        return Array.Empty<BarcodeObservation>();
    }

    /// <summary>原图读不出时依次尝试的预处理：去除孤立斑点、墨迹外扩补小缺口、降采样合并细碎噪声。</summary>
    private static readonly (
        string Name,
        int Scale,
        Func<byte[], int, int, (byte[], int, int)> Repair
    )[] Repairs =
    {
        ("median5", 1, (g, w, h) => (Median(g, w, h, 2), w, h)),
        ("ink_grow3", 1, (g, w, h) => (Minimum(g, w, h, 1), w, h)),
        ("half", 2, Half),
    };

    private static IReadOnlyList<BarcodeObservation> Decode(
        byte[] gray,
        int width,
        int height,
        int scale,
        string preprocessing,
        PixelRect bounds,
        CancellationToken token
    )
    {
        var source = new RGBLuminanceSource(gray, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true },
        };
        var decoded = reader.DecodeMultiple(source);
        token.ThrowIfCancellationRequested();
        return (decoded ?? Array.Empty<Result>())
            .Select(r => new BarcodeObservation(
                r.Text,
                r.BarcodeFormat.ToString(),
                bounds,
                r.BarcodeFormat == BarcodeFormat.QR_CODE && decoded!.Length == 1
                    ? TryQrGrid(source, bounds, scale, r.Text, token)
                    : null,
                preprocessing
            ))
            .ToArray();
    }

    /// <summary>方形邻域中值滤波，半径r；边界按最近像素延伸。</summary>
    private static byte[] Median(byte[] gray, int width, int height, int r)
    {
        var output = new byte[gray.Length];
        var window = new byte[(2 * r + 1) * (2 * r + 1)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int n = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = Math.Min(height - 1, Math.Max(0, y + dy));
                    for (int dx = -r; dx <= r; dx++)
                    {
                        window[n++] = gray[yy * width + Math.Min(width - 1, Math.Max(0, x + dx))];
                    }
                }

                Array.Sort(window);
                output[y * width + x] = window[n / 2];
            }
        }

        return output;
    }

    /// <summary>方形邻域最小值（深色墨迹外扩r像素），用于补合细小白点和断裂。</summary>
    private static byte[] Minimum(byte[] gray, int width, int height, int r)
    {
        var output = new byte[gray.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = 255;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = Math.Min(height - 1, Math.Max(0, y + dy));
                    for (int dx = -r; dx <= r; dx++)
                    {
                        value = Math.Min(value, gray[yy * width + Math.Min(width - 1, Math.Max(0, x + dx))]);
                    }
                }

                output[y * width + x] = value;
            }
        }

        return output;
    }

    /// <summary>2×2平均降采样。</summary>
    private static (byte[], int, int) Half(byte[] gray, int width, int height)
    {
        int w = Math.Max(1, width / 2),
            h = Math.Max(1, height / 2);
        var output = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(width - 1, 2 * x),
                    y0 = Math.Min(height - 1, 2 * y),
                    x1 = Math.Min(width - 1, x0 + 1),
                    y1 = Math.Min(height - 1, y0 + 1);
                output[y * w + x] = (byte)(
                    (
                        gray[y0 * width + x0]
                        + gray[y0 * width + x1]
                        + gray[y1 * width + x0]
                        + gray[y1 * width + x1]
                        + 2
                    ) / 4
                );
            }
        }

        return (output, w, h);
    }

    private static BarcodeModuleGrid? TryQrGrid(
        LuminanceSource source,
        PixelRect bounds,
        int scale,
        string text,
        CancellationToken token
    )
    {
        try
        {
            return QrGrid(source, bounds, scale, text, token);
        }
        catch (ReaderException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (ArithmeticException)
        {
            return null;
        }
    }

    private static BarcodeModuleGrid? QrGrid(
        LuminanceSource source,
        PixelRect bounds,
        int scale,
        string text,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        var hints = new Dictionary<DecodeHintType, object> { { DecodeHintType.TRY_HARDER, true } };
        var bitmap = new BinaryBitmap(new HybridBinarizer(source));
        var candidates = new global::ZXing.Multi.QrCode.Internal.MultiDetector(
            bitmap.BlackMatrix
        ).detectMulti(hints);
        if (candidates != null && candidates.Length > 1)
        {
            return null;
        }

        var detection =
            candidates != null && candidates.Length == 1
                ? candidates[0]
                : new global::ZXing.QrCode.Internal.Detector(bitmap.BlackMatrix).detect(hints);
        if (detection == null || detection.Points.Length < 3)
        {
            return null;
        }

        int n = detection.Bits.Width;
        var observed = new bool[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                observed[y * n + x] = detection.Bits[x, y];
            }
        }

        var verified = new global::ZXing.QrCode.Internal.Decoder().decode(detection.Bits, hints);
        if (verified == null || !string.Equals(verified.Text, text, StringComparison.Ordinal))
        {
            return null;
        }

        // 功能规则采用标准方向，不能直接应用到尚未纠正的镜像网格。
        if (
            verified.Other is global::ZXing.QrCode.Internal.QRCodeDecoderMetaData metadata
            && metadata.IsMirrored
        )
        {
            return null;
        }

        var bl = detection.Points[0];
        var tl = detection.Points[1];
        var tr = detection.Points[2];
        float brX = tr.X - tl.X + bl.X,
            brY = tr.Y - tl.Y + bl.Y,
            gridBR = n - 3.5f;
        if (detection.Points.Length >= 4)
        {
            brX = detection.Points[3].X;
            brY = detection.Points[3].Y;
            gridBR = n - 6.5f;
        }

        var transform = PerspectiveTransform.quadrilateralToQuadrilateral(
            3.5f,
            3.5f,
            n - 3.5f,
            3.5f,
            gridBR,
            gridBR,
            3.5f,
            n - 3.5f,
            tl.X,
            tl.Y,
            tr.X,
            tr.Y,
            brX,
            brY,
            bl.X,
            bl.Y
        );
        var corners = new float[] { 0, 0, n, 0, n, n, 0, n };
        transform.transformPoints(corners);
        var original = new double[8];
        for (int i = 0; i < 8; i++)
        {
            // ZXing坐标以像素中心为整数；降采样读出时按比例换回原图像素边缘坐标。
            original[i] = scale * (corners[i] + .5) + (i % 2 == 0 ? bounds.X : bounds.Y);
        }

        token.ThrowIfCancellationRequested();
        return new BarcodeModuleGrid(n, original, observed);
    }
}
