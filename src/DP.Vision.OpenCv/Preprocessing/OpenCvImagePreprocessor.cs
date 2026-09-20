using System;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>真实OpenCV整图预处理；输入像素预算1600万，不进行隐藏格式或坐标转换。</summary>
public sealed class OpenCvImagePreprocessor : IImagePreprocessor
{
    /// <inheritdoc/>
    public IImageSource Process(IImageSource image, ImagePreprocessingOptions options, CancellationToken token = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (options == null) throw new ArgumentNullException(nameof(options));
        token.ThrowIfCancellationRequested();
        var info = image.Info;
        if ((long)info.Width * info.Height > 16777216) throw new ArgumentException("Image exceeds preprocessing pixel budget.");
        if (options.Operation == EImagePreprocessing.Grayscale)
        {
            if (info.Layout == EPixelLayout.Gray16) throw new NotSupportedException("Use explicit Gray16ToGray8 gain/offset.");
            using var gray = CvPixels.Gray(image);
            token.ThrowIfCancellationRequested();
            return CvPixels.Buffer(gray);
        }
        bool sixteen = options.Operation == EImagePreprocessing.Gray16ToGray8;
        if (info.Layout != (sixteen ? EPixelLayout.Gray16 : EPixelLayout.Gray8))
            throw new NotSupportedException("Preprocessing requires the explicitly selected grayscale depth.");
        var bytes = new byte[info.ByteLength]; image.CopyTo(0, bytes, 0, bytes.Length);
        using var input = new Mat(info.Height, info.Width, sixteen ? MatType.CV_16UC1 : MatType.CV_8UC1);
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        using var output = new Mat();
        token.ThrowIfCancellationRequested();
        switch (options.Operation)
        {
            case EImagePreprocessing.Invert: Cv2.BitwiseNot(input, output); break;
            case EImagePreprocessing.Gaussian:
                Cv2.GaussianBlur(input, output, new Size(options.KernelSize, options.KernelSize), options.Sigma, options.Sigma, BorderTypes.Reflect101); break;
            case EImagePreprocessing.Median: Cv2.MedianBlur(input, output, options.KernelSize); break;
            default: input.ConvertTo(output, MatType.CV_8UC1, options.Gain, options.Offset); break;
        }
        token.ThrowIfCancellationRequested();
        return CvPixels.Buffer(output);
    }
}
