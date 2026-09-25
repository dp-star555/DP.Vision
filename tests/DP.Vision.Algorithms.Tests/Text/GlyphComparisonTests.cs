using System;
using System.Linq;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>单字比较的边缘带语义：轮廓波动不计入，深入笔画的缺陷完整计入。</summary>
[TestClass]
public sealed class GlyphComparisonTests
{
    private const int Width = 64,
        Height = 80;

    /// <summary>“L”形字：竖笔x∈[left,right)，底横y∈[bottom,74)。</summary>
    private static byte[] Glyph(int left = 12, int right = 24, int bottom = 62)
    {
        var bytes = Enumerable.Repeat((byte)255, Width * Height).ToArray();
        for (int y = 6; y < 74; y++)
        {
            for (int x = 12; x < 56; x++)
            {
                bool stem = x >= left && x < right;
                bool foot = y >= bottom;
                if (stem || foot)
                {
                    bytes[y * Width + x] = 0;
                }
            }
        }

        return bytes;
    }

    private static void Paint(byte[] bytes, int x0, int y0, int x1, int y1, byte value)
    {
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                bytes[y * Width + x] = value;
            }
        }
    }

    private static GlyphComparisonResult Compare(byte[] actual, byte[] reference)
    {
        using var a = VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), actual);
        using var r = VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), reference);
        return new OpenCvGlyphComparer().Compare(a, r, new GlyphComparisonOptions());
    }

    /// <summary>笔画中心的小空洞完整计入；旧的膨胀容差会把它整个抹掉。</summary>
    [TestMethod]
    public void InteriorVoidIsMeasuredDespiteTolerance()
    {
        var actual = Glyph();
        Paint(actual, 16, 30, 20, 34, 255);
        using var result = Compare(actual, Glyph());
        Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
        Assert.IsGreaterThan(10, result.Missing);
        Assert.AreEqual(0, result.Extra);
        Assert.IsGreaterThan(0d, result.Difference);
    }

    /// <summary>整体偏粗、偏细或单侧轮廓变化属于印刷波动，不产生差异。</summary>
    [TestMethod]
    [DataRow(11, 25, 61)]
    [DataRow(13, 23, 63)]
    [DataRow(12, 25, 62)]
    [DataRow(12, 23, 62)]
    public void OutlineVariationIsIgnored(int left, int right, int bottom)
    {
        using var result = Compare(Glyph(left, right, bottom), Glyph());
        Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
        Assert.AreEqual(0d, result.Difference);
    }

    /// <summary>贯穿笔画的细裂纹虽窄也要计入，不能当作贴边细条忽略。</summary>
    [TestMethod]
    public void CrackThroughStrokeIsMeasured()
    {
        var actual = Glyph();
        Paint(actual, 12, 36, 24, 38, 255);
        using var result = Compare(actual, Glyph());
        Assert.IsGreaterThan(0, result.Missing);
    }

    /// <summary>远离笔画的多墨点计入多墨。</summary>
    [TestMethod]
    public void DetachedExtraInkIsMeasured()
    {
        var actual = Glyph();
        Paint(actual, 36, 30, 42, 36, 0);
        using var result = Compare(actual, Glyph());
        Assert.IsGreaterThan(0, result.Extra);
    }

    /// <summary>深度比例只接受0–1，默认值保持兼容构造函数行为。</summary>
    [TestMethod]
    public void DepthRatioIsValidated()
    {
        Assert.AreEqual(GlyphComparisonOptions.DefaultDepthRatio, new GlyphComparisonOptions().DepthRatio);
        Assert.AreEqual(0d, new GlyphComparisonOptions(160, 2, EGlyphBinarization.Otsu, 0).DepthRatio);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new GlyphComparisonOptions(160, 2, EGlyphBinarization.Otsu, 1.5)
        );
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new GlyphComparisonOptions(160, 2, EGlyphBinarization.Otsu, double.NaN)
        );
    }
}
