using System;
using System.Linq;
using System.Runtime.InteropServices;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.Vision.Algorithms.Tests;

/// <summary>单字对齐搜索：每个缩放一次仿射加整像素裁取，与逐个缩放/平移做仿射的原始搜索结果一致。</summary>
[TestClass]
public sealed class GlyphAlignmentTests
{
    private const int Width = 64,
        Height = 80;

    private static readonly int[] Scales = { 100, 98, 102, 96, 104, 94, 106 };

    /// <summary>“L”形字，可选一个改变墨迹外接框的多墨点，使归一化后需要缩放和平移才能对齐。</summary>
    private static byte[] Glyph(int left, int right, int bottom, int speckX = -1, int speckY = -1)
    {
        var bytes = Enumerable.Repeat((byte)255, Width * Height).ToArray();
        for (int y = 6; y < 74; y++)
        {
            for (int x = 12; x < 56; x++)
            {
                if (x >= left && x < right || y >= bottom)
                {
                    bytes[y * Width + x] = 0;
                }
            }
        }

        if (speckX >= 0)
        {
            for (int y = speckY; y < speckY + 3; y++)
            {
                for (int x = speckX; x < speckX + 3; x++)
                {
                    bytes[y * Width + x] = 0;
                }
            }
        }

        return bytes;
    }

    /// <summary>比较结果中的实际字视图（反色的已对齐二值字）还原为墨迹=255的Mat。</summary>
    private static Mat Ink(IImageSource view)
    {
        var bytes = new byte[view.Info.ByteLength];
        view.CopyTo(0, bytes, 0, bytes.Length);
        var mat = new Mat(view.Info.Height, view.Info.Width, MatType.CV_8UC1);
        Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
        Cv2.BitwiseNot(mat, mat);
        return mat;
    }

    private static GlyphComparisonResult Compare(byte[] actual, byte[] reference)
    {
        using var a = VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), actual);
        using var r = VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), reference);
        return new OpenCvGlyphComparer().Compare(a, r, new GlyphComparisonOptions());
    }

    /// <summary>原始实现：每个缩放和±2像素平移各做一次最近邻仿射，按交并比（同分取离原位最近）选最佳。</summary>
    private static Mat BruteForce(Mat normalized, Mat reference)
    {
        var best = normalized.Clone();
        double bestScore = -1;
        int bestDistance = int.MaxValue;
        using var shifted = new Mat();
        using var intersection = new Mat();
        using var union = new Mat();
        using var transform = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        foreach (int percent in Scales)
        {
            float scale = percent / 100f;
            transform.Set(0, 0, scale);
            transform.Set(1, 1, scale);
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    transform.Set(0, 2, 56 * (1 - scale) + dx);
                    transform.Set(1, 2, 56 * (1 - scale) + dy);
                    Cv2.WarpAffine(
                        normalized,
                        shifted,
                        transform,
                        new Size(112, 112),
                        InterpolationFlags.Nearest
                    );
                    Cv2.BitwiseAnd(shifted, reference, intersection);
                    Cv2.BitwiseOr(shifted, reference, union);
                    int overlap = Cv2.CountNonZero(intersection),
                        total = Cv2.CountNonZero(union),
                        distance = dx * dx + dy * dy + (percent - 100) * (percent - 100);
                    double score = total == 0 ? 0 : overlap / (double)total;
                    if (
                        score > bestScore + 1e-9
                        || (Math.Abs(score - bestScore) <= 1e-9 && distance < bestDistance)
                    )
                    {
                        shifted.CopyTo(best);
                        bestScore = score;
                        bestDistance = distance;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>粗细、外接框和多墨点各不相同的字对：对齐结果与原始逐个仿射搜索逐像素一致，且确实用到了非原位对齐。</summary>
    [TestMethod]
    public void AlignmentMatchesPerShiftSearch()
    {
        var reference = Glyph(12, 24, 62);
        var actuals = new[]
        {
            Glyph(12, 24, 62),
            Glyph(13, 26, 60),
            Glyph(11, 22, 64),
            Glyph(12, 24, 62, 56, 30),
            Glyph(14, 25, 61, 8, 70),
            Glyph(12, 24, 62, 30, 2),
            Glyph(10, 27, 59, 58, 74),
        };
        int moved = 0;
        foreach (var actual in actuals)
        {
            // 自身比较的最佳对齐是原位，得到的实际字视图即归一化结果。
            using var self = Compare(actual, actual);
            using var normalized = Ink(self.Actual!);
            using var result = Compare(actual, reference);
            using var referenceInk = Ink(result.Reference!);
            using var expected = BruteForce(normalized, referenceInk);
            using var aligned = Ink(result.Actual!);
            using var difference = new Mat();
            Cv2.Absdiff(expected, aligned, difference);
            Assert.AreEqual(0, Cv2.CountNonZero(difference));
            using var change = new Mat();
            Cv2.Absdiff(normalized, aligned, change);
            if (Cv2.CountNonZero(change) > 0)
            {
                moved++;
            }
        }

        Assert.IsTrue(moved >= 3, "测试样本应覆盖非原位对齐：" + moved);
    }
}
