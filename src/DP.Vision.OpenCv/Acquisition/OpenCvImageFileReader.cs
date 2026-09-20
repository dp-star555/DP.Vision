using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>OpenCV单图文件读取；保留8位灰度/BGR/BGRA及16位灰度，不静默降位深。只读取首张图像。</summary>
public sealed class OpenCvImageFileReader : IImageFileReader
{
    private readonly int _maximumEncodedBytes;

    /// <summary>创建有编码文件大小限制的读取器；解码后的尺寸另受ImageInfo限制。</summary>
    /// <param name="maximumEncodedBytes">允许的最大文件字节数；不代表原生解码器的峰值内存限制。</param>
    public OpenCvImageFileReader(int maximumEncodedBytes = 64 * 1024 * 1024)
    {
        if (maximumEncodedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
        _maximumEncodedBytes = maximumEncodedBytes;
    }

    /// <inheritdoc/>
    public async Task<IImageSource> ReadAsync(string path, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Image path required.", nameof(path));
        token.ThrowIfCancellationRequested();
        byte[] encoded;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            if (file.Length == 0 || file.Length > _maximumEncodedBytes)
                throw new InvalidDataException("Image file is empty or exceeds the encoded byte limit.");
            encoded = new byte[checked((int)file.Length)];
            int position = 0;
            while (position < encoded.Length)
            {
                int read = await file.ReadAsync(encoded, position, encoded.Length - position, token)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException();
                position += read;
            }
        }
        token.ThrowIfCancellationRequested();
        using var decoded = Cv2.ImDecode(encoded, ImreadModes.Unchanged);
        if (decoded.Empty())
            throw new InvalidDataException("Unsupported or corrupt image file.");
        EPixelLayout layout;
        if (decoded.Type() == MatType.CV_8UC1)
            layout = EPixelLayout.Gray8;
        else if (decoded.Type() == MatType.CV_8UC3)
            layout = EPixelLayout.Bgr24;
        else if (decoded.Type() == MatType.CV_8UC4)
            layout = EPixelLayout.Bgra32;
        else if (decoded.Type() == MatType.CV_16UC1)
            layout = EPixelLayout.Gray16;
        else
            throw new NotSupportedException("Decoded pixel layout is not supported without conversion.");
        var info = new ImageInfo(decoded.Cols, decoded.Rows, layout);
        var pixels = new byte[info.ByteLength];
        for (int row = 0; row < info.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(decoded.Ptr(row), pixels, row * info.Stride, info.Stride);
        }
        token.ThrowIfCancellationRequested();
        return VisionImage.CopyFrom(info, pixels);
    }
}
