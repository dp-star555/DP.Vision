using System.Runtime.InteropServices;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

internal static class CvPixels
{
    internal static bool Supports(IImageSource image)
    {
        return image.Info.Layout != EPixelLayout.Gray16;
    }

    internal static Mat Gray(IImageSource image)
    {
        var info = image.Info;
        var bytes = new byte[info.ByteLength];
        image.CopyTo(0, bytes, 0, bytes.Length);
        using var raw = new Mat(info.Height, info.Width, MatType.CV_8UC(info.BytesPerPixel));
        Marshal.Copy(bytes, 0, raw.Data, bytes.Length);
        if (info.Layout == EPixelLayout.Gray8)
        {
            return raw.Clone();
        }

        var result = new Mat();
        try
        {
            var mode =
                info.Layout == EPixelLayout.Bgr24 ? ColorConversionCodes.BGR2GRAY
                : info.Layout == EPixelLayout.Rgb24 ? ColorConversionCodes.RGB2GRAY
                : info.Layout == EPixelLayout.Bgra32 ? ColorConversionCodes.BGRA2GRAY
                : ColorConversionCodes.RGBA2GRAY;
            Cv2.CvtColor(raw, result, mode);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal static IImageSource Buffer(Mat image)
    {
        var info = new ImageInfo(
            image.Cols,
            image.Rows,
            image.Channels() == 1 ? EPixelLayout.Gray8 : EPixelLayout.Bgr24
        );
        var bytes = new byte[info.ByteLength];
        for (int y = 0; y < info.Height; y++)
        {
            Marshal.Copy(image.Ptr(y), bytes, y * info.Stride, info.Stride);
        }

        return VisionImage.CopyFrom(info, bytes);
    }

    internal static Mat Ink(Mat gray, int threshold)
    {
        var result = new Mat();
        try
        {
            Cv2.Threshold(gray, result, threshold - 1, 255, ThresholdTypes.BinaryInv);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
