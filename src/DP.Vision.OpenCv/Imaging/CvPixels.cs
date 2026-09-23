using System;
using System.Runtime.InteropServices;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>中立图像与OpenCV Mat之间的像素搬运及常用灰度转换。</summary>
internal static class CvPixels
{
    internal static bool Supports(IImageSource image)
    {
        return image.Info.Layout != EPixelLayout.Gray16;
    }

    internal static Rect Rect(PixelBounds b)
    {
        return new Rect(b.X, b.Y, b.Width, b.Height);
    }

    /// <summary>复制为同布局Mat；仅支持Gray8与Bgr24。</summary>
    internal static Mat Mat(IImageSource frame)
    {
        if (frame.Info.Layout != EPixelLayout.Gray8 && frame.Info.Layout != EPixelLayout.Bgr24)
        {
            throw new NotSupportedException("This implementation supports Gray8 and Bgr24.");
        }

        return Raw(frame);
    }

    /// <summary>复制并转换为Gray8 Mat；调用方须先用Supports排除Gray16。</summary>
    internal static Mat Gray(IImageSource image)
    {
        var info = image.Info;
        var raw = Raw(image);
        if (info.Layout == EPixelLayout.Gray8)
        {
            return raw;
        }

        using (raw)
        {
            var mode =
                info.Layout == EPixelLayout.Bgr24 ? ColorConversionCodes.BGR2GRAY
                : info.Layout == EPixelLayout.Rgb24 ? ColorConversionCodes.RGB2GRAY
                : info.Layout == EPixelLayout.Bgra32 ? ColorConversionCodes.BGRA2GRAY
                : ColorConversionCodes.RGBA2GRAY;
            return Convert(raw, mode);
        }
    }

    /// <summary>Gray8原样克隆，其余按BGR转灰度。</summary>
    internal static Mat Gray(Mat image)
    {
        return image.Channels() == 1 ? image.Clone() : Convert(image, ColorConversionCodes.BGR2GRAY);
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

    internal static Mat Otsu(Mat image)
    {
        using var gray = Gray(image);
        var histogram = new int[256];
        for (int y = 0; y < gray.Rows; y++)
        {
            for (int x = 0; x < gray.Cols; x++)
            {
                histogram[gray.At<byte>(y, x)]++;
            }
        }

        long n = (long)gray.Rows * gray.Cols,
            sum = 0;
        int low = 0,
            high = 255;
        for (int i = 0; i < 256; i++)
        {
            sum += histogram[i];
            if (sum > Math.Floor((n - 1) * .02))
            {
                low = i;
                break;
            }
        }

        sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += histogram[i];
            if (sum > Math.Floor((n - 1) * .98))
            {
                high = i;
                break;
            }
        }

        if (high - low < 12)
        {
            return new Mat(gray.Rows, gray.Cols, MatType.CV_8UC1, Scalar.All(0));
        }

        var output = new Mat();
        try
        {
            Cv2.Threshold(gray, output, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>把紧密排列的原像素复制进新的8位Mat，通道数等于每像素字节数。</summary>
    private static Mat Raw(IImageSource image)
    {
        var info = image.Info;
        var bytes = new byte[info.ByteLength];
        image.CopyTo(0, bytes, 0, bytes.Length);
        var mat = new Mat(info.Height, info.Width, MatType.CV_8UC(info.BytesPerPixel));
        try
        {
            Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
            return mat;
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }

    private static Mat Convert(Mat image, ColorConversionCodes mode)
    {
        var output = new Mat();
        try
        {
            Cv2.CvtColor(image, output, mode);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }
}
