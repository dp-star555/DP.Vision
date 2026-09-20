using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

internal static class CvImages
{
    internal static Rect Rect(PixelBounds b)
    {
        return new Rect(b.X, b.Y, b.Width, b.Height);
    }

    internal static Mat Mat(IImageSource frame)
    {
        if (frame.Info.Layout != EPixelLayout.Gray8 && frame.Info.Layout != EPixelLayout.Bgr24)
        {
            throw new NotSupportedException("This implementation supports Gray8 and Bgr24.");
        }

        var bytes = new byte[frame.Info.ByteLength];
        frame.CopyTo(0, bytes, 0, bytes.Length);
        var mat = new Mat(
            frame.Info.Height,
            frame.Info.Width,
            frame.Info.Layout == EPixelLayout.Gray8 ? MatType.CV_8UC1 : MatType.CV_8UC3
        );
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

    internal static IImageSource Frame(Mat image)
    {
        return CvPixels.Buffer(image);
    }

    internal static Mat Gray(Mat image)
    {
        if (image.Channels() == 1)
        {
            return image.Clone();
        }

        var output = new Mat();
        try
        {
            Cv2.CvtColor(image, output, ColorConversionCodes.BGR2GRAY);
            return output;
        }
        catch
        {
            output.Dispose();
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
}
