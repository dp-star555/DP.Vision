using System;
using DP.Vision;

namespace DP.Vision.Acquisition.Tests;

/// <summary>确定性中立测试图像工厂；像素值只由尺寸和种子决定，不依赖任何SDK或真实解码。</summary>
internal static class TestImages
{
    /// <summary>创建确定性Gray8图像。</summary>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="seed">像素种子，用于区分不同帧。</param>
    /// <returns>由调用方释放的图像源。</returns>
    public static IImageSource Gray8(int width = 4, int height = 3, byte seed = 1)
    {
        var info = new ImageInfo(width, height, EPixelLayout.Gray8);
        var pixels = new byte[info.ByteLength];
        for (int index = 0; index < pixels.Length; index++)
            pixels[index] = unchecked((byte)(seed + index));
        return VisionImage.CopyFrom(info, pixels);
    }
}
