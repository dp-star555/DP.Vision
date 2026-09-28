using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>通用仿射图像采样；几何映射与像素插值分离，读码等算子不被迫使用重采样结果。</summary>
public static class AffineImageResampler
{
    /// <summary>用输出像素边界坐标到输入原图边界坐标的矩阵生成独立副本；最近邻、不静默填充越界像素。</summary>
    /// <param name="source">借用的原图租约。</param>
    /// <param name="outputToSource">输出坐标到输入坐标的可逆矩阵。</param>
    /// <param name="width">输出宽度。</param>
    /// <param name="height">输出高度。</param>
    /// <param name="token">协作式取消令牌。</param>
    /// <returns>调用方负责释放的独立图像租约。</returns>
    public static IImageSource Nearest(IImageSource source, CoordinateMatrix2D outputToSource,
        int width, int height, CancellationToken token = default)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (outputToSource == null) throw new ArgumentNullException(nameof(outputToSource));
        if (width < 1 || height < 1 || (long)width * height > 16000000)
            throw new ArgumentOutOfRangeException(nameof(width), "Affine output exceeds 16M pixels.");
        var info = source.Info;
        if (info.Layout != EPixelLayout.Gray8 && info.Layout != EPixelLayout.Bgr24)
            throw new NotSupportedException("Affine nearest-neighbor sampling supports Gray8 and Bgr24.");
        token.ThrowIfCancellationRequested();
        int channels = info.BytesPerPixel;
        var pixels = new byte[info.ByteLength];
        source.CopyTo(0, pixels, 0, pixels.Length);
        var output = new byte[checked(width * height * channels)];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                // 输入矩阵映射连续像素边界；采样点是输出像素中心。
                var point = outputToSource.Map(new Coordinate2D(x + .5, y + .5));
                if (point.X < 0 || point.Y < 0 || point.X >= info.Width || point.Y >= info.Height)
                    throw new ArgumentException("Affine output extends beyond the source image.", nameof(outputToSource));
                int sx = (int)Math.Floor(point.X), sy = (int)Math.Floor(point.Y);
                Buffer.BlockCopy(pixels, (sy * info.Width + sx) * channels, output,
                    (y * width + x) * channels, channels);
            }
        }
        token.ThrowIfCancellationRequested();
        return VisionImage.CopyFrom(new ImageInfo(width, height, info.Layout), output);
    }
}
