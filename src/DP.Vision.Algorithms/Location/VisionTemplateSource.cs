using System;
using System.IO;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>中立样图快照格式，仅用于重建和制作预览。</summary>
public static class VisionTemplateSource
{
    /// <summary>捕获有界样图，调用结束后不保留运行帧租约。</summary>
    public static byte[] Encode(IImageSource image, CancellationToken token = default)
    {
        if (image.Info.ByteLength > 64 * 1024 * 1024) throw new ArgumentException("模板样图超过64MiB预算。");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(image.Info.Width); writer.Write(image.Info.Height); writer.Write((int)image.Info.Layout);
        var row = new byte[image.Info.Stride];
        for (int y = 0; y < image.Info.Height; y++) { token.ThrowIfCancellationRequested(); image.CopyTo(y * row.Length, row, 0, row.Length); writer.Write(row); }
        return stream.ToArray();
    }
    /// <summary>解码独立样图句柄，调用者释放。</summary>
    public static IImageSource Decode(byte[] bytes)
    {
        if (bytes.Length > 64 * 1024 * 1024 + 16) throw new InvalidDataException("样图大小超限。");
        using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("不支持的样图格式。");
        var info = new ImageInfo(reader.ReadInt32(), reader.ReadInt32(), (EPixelLayout)reader.ReadInt32());
        if (info.ByteLength > 64 * 1024 * 1024 || stream.Length - stream.Position != info.ByteLength) throw new InvalidDataException("样图尺寸或像素不一致。");
        return VisionImage.CopyFrom(info, reader.ReadBytes(info.ByteLength));
    }
}
