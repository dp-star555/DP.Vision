using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>中立的文字组合策略及整ROI质量证据契约测试。</summary>
[TestClass]
public sealed partial class TextQualityTests
{
    /// <summary>三个组合接口均实际执行，匹配缺失不能被误认为完成。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ComposedMatcherControlsCoverage(bool missing)
    {
        var pixels = Enumerable.Repeat((byte)255, 256).ToArray();
        for (int y = 3; y < 13; y++)
        {
            for (int x = 4; x < 9; x++)
            {
                pixels[y * 16 + x] = 0;
            }
        }

        using var image = VisionImage.CopyFrom(new ImageInfo(16, 16, EPixelLayout.Gray8), pixels);
        var matcher = new Matcher { Missing = missing };
        var inspector = new TextQualityInspector(
            new OpenCvCharacterSegmenter(),
            matcher,
            new OpenCvGlyphComparer()
        );
        using var result = inspector.Inspect(
            new TextQualityRequest(
                image,
                new PixelBounds(0, 0, 16, 16),
                "A",
                true,
                new Dictionary<string, GlyphTemplate>
                {
                    { "A", new GlyphTemplate(image, EGlyphBinarization.Otsu) },
                },
                160,
                0,
                .18
            )
        );
        Assert.AreEqual(1, matcher.Calls);
        Assert.AreEqual(!missing, result.Completed);
        Assert.AreEqual(!missing, result.Passed);
        Assert.AreEqual(1, result.Glyphs.Count);
        Assert.AreEqual(missing ? "missing_template" : "compared", result.Glyphs[0].Status);
    }

    /// <summary>整ROI策略可以报告真实区域证据，无需伪造分割字符。</summary>
    [TestMethod]
    [DataRow(EQualityFindingKind.Information, true, true)]
    [DataRow(EQualityFindingKind.Defect, true, false)]
    [DataRow(EQualityFindingKind.Blocker, false, false)]
    public void IndependentStrategyPreservesStatus(EQualityFindingKind kind, bool completed, bool passed)
    {
        using var result = new TextQualityResult(
            true,
            new[] { new QualityFinding("measurement", "test", kind) }
        );
        Assert.AreEqual(completed, result.Completed);
        Assert.AreEqual(passed, result.Passed);
        Assert.IsNull(result.Segmentation);
        Assert.AreEqual(0, result.Glyphs.Count);
    }

    /// <summary>没有发现记录不能覆盖未完成状态。</summary>
    [TestMethod]
    public void EmptyIncompleteStrategyCannotPass()
    {
        using var result = new TextQualityResult(false, Array.Empty<QualityFinding>());
        Assert.IsFalse(result.Passed);
    }
}
