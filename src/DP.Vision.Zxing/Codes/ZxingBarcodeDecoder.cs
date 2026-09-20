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

        var source = new RGBLuminanceSource(
            gray,
            bounds.Width,
            bounds.Height,
            RGBLuminanceSource.BitmapFormat.Gray8
        );
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
                    ? TryQrGrid(source, bounds, r.Text, token)
                    : null
            ))
            .ToArray();
    }

    private static BarcodeModuleGrid? TryQrGrid(
        LuminanceSource source,
        PixelRect bounds,
        string text,
        CancellationToken token
    )
    {
        try
        {
            return QrGrid(source, bounds, text, token);
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
            original[i] = corners[i] + (i % 2 == 0 ? bounds.X : bounds.Y) + .5;
        }

        token.ThrowIfCancellationRequested();
        return new BarcodeModuleGrid(n, original, observed);
    }
}
