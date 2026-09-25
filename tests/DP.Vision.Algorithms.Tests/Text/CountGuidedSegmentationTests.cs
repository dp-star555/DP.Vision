using System.Linq;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>投影分组与字符数不一致时，按字符数与字宽先验求最优切割。</summary>
[TestClass]
public sealed class CountGuidedSegmentationTests
{
    private const int Width = 160,
        Height = 40;

    private static byte[] Blank()
    {
        return Enumerable.Repeat((byte)255, Width * Height).ToArray();
    }

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

    /// <summary>实心块字：x∈[left,left+12)，y∈[8,32)。</summary>
    private static void Block(byte[] pixels, int left)
    {
        Paint(pixels, left, 8, 12, 24);
    }

    private static CharacterSegmentation Segment(byte[] pixels, string text)
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), pixels);
        return new OpenCvCharacterSegmenter().Segment(image, new PixelBounds(0, 0, Width, Height), text);
    }

    /// <summary>两字之间的细连接在最薄处切开，其余字不受影响。</summary>
    [TestMethod]
    public void ThinBridgeIsCut()
    {
        var pixels = Blank();
        for (int i = 0; i < 5; i++)
        {
            Block(pixels, 10 + i * 20);
        }

        Paint(pixels, 22, 18, 8, 2);
        using var result = Segment(pixels, "ABCDE");
        Assert.AreEqual("count_guided_cuts", result.Basis, result.Reason);
        Assert.AreEqual("provisional", result.Status);
        Assert.HasCount(5, result.Characters);
        Assert.IsTrue(result.Characters[0].Bounds.X + result.Characters[0].Bounds.Width <= 30);
        Assert.IsTrue(result.Characters[1].Bounds.X >= 22);
    }

    /// <summary>左右断成两截的字按整体保留，不会拆成两个字。</summary>
    [TestMethod]
    public void BrokenGlyphIsKeptWhole()
    {
        var pixels = Blank();
        for (int i = 0; i < 4; i++)
        {
            Block(pixels, 10 + i * 20);
        }

        // 第五个字竖向断开成两半，中间有一列空白。
        Paint(pixels, 90, 8, 5, 24);
        Paint(pixels, 97, 8, 5, 24);
        using var result = Segment(pixels, "ABCDE");
        Assert.AreEqual("count_guided_cuts", result.Basis, result.Reason);
        Assert.HasCount(5, result.Characters);
        var last = result.Characters[4].Bounds;
        Assert.IsTrue(last.X <= 90 && last.X + last.Width >= 102, last.ToString());
        var previous = result.Characters[3].Bounds;
        Assert.IsTrue(previous.X + previous.Width <= 90, previous.ToString());
    }

    /// <summary>宽连接无法在可接受的穿墨下切开时拒绝，不强行凑数。</summary>
    [TestMethod]
    public void BroadJoinIsRefused()
    {
        var pixels = Blank();
        for (int i = 0; i < 5; i++)
        {
            Block(pixels, 10 + i * 20);
        }

        Paint(pixels, 22, 8, 8, 24);
        using var result = Segment(pixels, "ABCDE");
        Assert.IsEmpty(result.Characters, result.Reason);
    }
}
