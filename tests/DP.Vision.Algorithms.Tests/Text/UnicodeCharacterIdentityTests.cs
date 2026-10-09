using System;
using System.Linq;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>中立Unicode字形单位和真实分割的小标点保留回归。</summary>
[TestClass]
public sealed class UnicodeCharacterIdentityTests
{
    /// <summary>单行按Unicode标量计数，只忽略空格，不改变原有顺序与身份。</summary>
    [TestMethod]
    public void TokenizePreservesIdentityAndCountsSupplementaryCharacters()
    {
        Assert.IsTrue(CharacterIdentity.TryTokenizeLine("A：中\u3000𠮷:/a", out var tokens));
        CollectionAssert.AreEqual(new[] { "A", "：", "中", "𠮷", ":", "/", "a" }, tokens);
        Assert.IsTrue(
            CharacterIdentity.TryTokenizeLine(string.Concat(Enumerable.Repeat("𠮷", 128)), out var maximum)
        );
        Assert.AreEqual(128, maximum.Length);
        Assert.IsFalse(
            CharacterIdentity.TryTokenizeLine(string.Concat(Enumerable.Repeat("𠮷", 129)), out var excessive)
        );
        Assert.AreEqual(0, excessive.Length);
        Assert.IsTrue(CharacterIdentity.IsAlphanumeric('A'));
        Assert.IsFalse(
            CharacterIdentity.IsAlphanumeric('中'),
            "The old ASCII predicate retains its original meaning."
        );
    }

    /// <summary>非法序列不能返回部分字形，也不进行规范化或去除控制字符。</summary>
    [TestMethod]
    public void TokenizeRejectsControlsAndBrokenUnicodeAtomically()
    {
        foreach (
            var text in new[]
            {
                "",
                " ",
                "中\n文",
                "中\t文",
                "中\u200b文",
                "e\u0301",
                "\ud800",
                "\udc00",
                "A\ud800",
            }
        )
        {
            Assert.IsFalse(CharacterIdentity.TryTokenizeLine(text, out var tokens));
            Assert.AreEqual(0, tokens.Length);
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CharacterIdentity.TryTokenizeLine("中", out _, 0)
        );
    }

    /// <summary>2×2的句点不能因行ROI高而被当作字母数字噪点抹掉；不伪造不存在的标点。</summary>
    [TestMethod]
    public void TinyPunctuationIsKeptInTallLineRoi()
    {
        const int width = 180,
            height = 160;
        var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int y = 30; y < 100; y++)
        for (int x = 10; x < 38; x++)
            pixels[y * width + x] = 0;
        for (int y = 96; y < 98; y++)
        for (int x = 58; x < 60; x++)
            pixels[y * width + x] = 0;
        using var image = VisionImage.CopyFrom(new ImageInfo(width, height, EPixelLayout.Gray8), pixels);
        using var measured = new OpenCvCharacterSegmenter().Segment(
            image,
            new PixelBounds(0, 0, width, height),
            "中."
        );
        Assert.AreEqual("provisional", measured.Status, measured.Reason);
        CollectionAssert.AreEqual(
            new[] { "中", "." },
            measured.Characters.Select(c => c.Character).ToArray()
        );
        Assert.IsTrue(measured.Characters[1].Bounds.X >= 57);
    }
}
