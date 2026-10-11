using System.Linq;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>投影分组与字符数不一致、连通域分组一致时，按连通域归属分字并擦除伸入本字范围的邻字墨迹。</summary>
[TestClass]
public sealed class ComponentOwnershipSegmentationTests
{
    private const int Width = 80,
        Height = 40;

    private static void Paint(byte[] pixels, int x, int y, int w, int h)
    {
        for (int row = y; row < y + h; row++)
        {
            for (int col = x; col < x + w; col++)
            {
                pixels[row * Width + col] = 0;
            }
        }
    }

    /// <summary>第二个字的下横伸到第一个字下方（列重叠但不相连）：三个字各自成组，邻字墨迹记入擦除数并涂成背景。</summary>
    [TestMethod]
    public void OverlappingNeighborInkIsRemoved()
    {
        var pixels = Enumerable.Repeat((byte)255, Width * Height).ToArray();
        Paint(pixels, 10, 8, 12, 20);
        Paint(pixels, 24, 8, 12, 24);
        Paint(pixels, 14, 29, 10, 2);
        Paint(pixels, 50, 8, 12, 24);
        using var image = VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), pixels);
        using var result = new OpenCvCharacterSegmenter().Segment(
            image,
            new PixelBounds(0, 0, Width, Height),
            "ABC"
        );

        Assert.AreEqual("connected_component_ownership", result.Basis, result.Reason);
        Assert.AreEqual("provisional", result.Status);
        Assert.HasCount(3, result.Characters);
        var first = result.Characters[0];
        Assert.AreEqual(new PixelBounds(9, 6, 14, 24), first.Bounds);
        Assert.AreEqual(9, first.NeighborInkRemoved);
        var patch = new byte[first.Patch.Info.ByteLength];
        first.Patch.CopyTo(0, patch, 0, patch.Length);
        int stride = first.Patch.Info.Stride;
        Assert.AreEqual(0, patch[(10 - 6) * stride + (12 - 9)], "本字墨迹保留");
        Assert.AreEqual(255, patch[(29 - 6) * stride + (16 - 9)], "邻字下横涂成背景");
        // 第二个字的范围（含下横）覆盖第一个字的9列×20行墨迹，第三个字范围内没有邻字墨迹。
        Assert.AreEqual(180, result.Characters[1].NeighborInkRemoved);
        Assert.AreEqual(0, result.Characters[2].NeighborInkRemoved);
    }
}
