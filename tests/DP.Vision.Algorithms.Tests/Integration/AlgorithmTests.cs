using System;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>原生契约、证据所有权及测量回归测试。</summary>
[TestClass]
public sealed class AlgorithmTests
{
    private static IImageSource Gray(byte[] bytes, int width = 16)
    {
        return VisionImage.CopyFrom(new ImageInfo(width, bytes.Length / width, EPixelLayout.Gray8), bytes);
    }

    private static byte[] White()
    {
        return Enumerable.Repeat((byte)255, 256).ToArray();
    }

    private static byte[] Glyph()
    {
        var bytes = White();
        for (int y = 3; y < 13; y++)
        {
            for (int x = 3; x < 6; x++)
            {
                bytes[y * 16 + x] = 0;
            }
        }

        for (int y = 10; y < 13; y++)
        {
            for (int x = 3; x < 13; x++)
            {
                bytes[y * 16 + x] = 0;
            }
        }

        return bytes;
    }

    /// <summary>相同图像比较保持输入字节不变。</summary>
    [TestMethod]
    public void EqualGlyphsCompleteAndInputsRemainUnchanged()
    {
        var pixels = Glyph();
        using var a = Gray(pixels);
        using var r = Gray(pixels);
        using var result = new OpenCvGlyphComparer().Compare(a, r, new GlyphComparisonOptions());
        Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
        Assert.AreEqual(0d, result.Difference);
        Assert.AreEqual(112, result.Actual!.Info.Width);
        var after = new byte[pixels.Length];
        a.CopyTo(0, after, 0, after.Length);
        CollectionAssert.AreEqual(pixels, after);
    }

    /// <summary>缺墨产生已完成且非零的测量结果。</summary>
    [TestMethod]
    public void MissingGlyphHasMeasuredDefects()
    {
        using var a = Gray(White());
        using var r = Gray(Glyph());
        using var result = new OpenCvGlyphComparer().Compare(a, r, new GlyphComparisonOptions(tolerance: 0));
        Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
        Assert.AreEqual(1d, result.Difference);
        Assert.IsGreaterThan(0, result.Missing);
    }

    /// <summary>参考图没有墨迹时阻断测量。</summary>
    [TestMethod]
    public void EmptyReferenceIsNotSuccessfulZeroDifference()
    {
        using var image = Gray(White());
        using var result = new OpenCvGlyphComparer().Compare(image, image, new GlyphComparisonOptions());
        Assert.AreEqual(EAlgorithmStatus.InsufficientEvidence, result.Status);
        Assert.AreEqual("empty_reference", result.ReasonCode);
        Assert.IsNotNull(result.Delta);
    }

    /// <summary>保留历史灰度分位保护规则。</summary>
    [TestMethod]
    public void OtsuPreservesLowContrastGuard()
    {
        var bytes = Enumerable.Repeat((byte)105, 256).ToArray();
        for (int i = 0; i < 128; i++)
        {
            bytes[i] = 100;
        }

        using var image = Gray(bytes);
        using var result = new OpenCvGlyphComparer().Compare(image, image, new GlyphComparisonOptions());
        Assert.AreEqual(EAlgorithmStatus.InsufficientEvidence, result.Status);
    }

    /// <summary>独立证据租约可在生产者释放后继续有效。</summary>
    [TestMethod]
    public void EvidenceRetainOutlivesResultAndInput()
    {
        var image = Gray(Glyph());
        var result = new OpenCvGlyphComparer().Compare(image, image, new GlyphComparisonOptions());
        using var evidence = result.Actual!.Retain();
        image.Dispose();
        result.Dispose();
        result.Dispose();
        var bytes = new byte[evidence.Info.ByteLength];
        evidence.CopyTo(0, bytes, 0, bytes.Length);
        Assert.IsTrue(bytes.Any(b => b == 0));
        Assert.ThrowsExactly<ObjectDisposedException>(() => result.Actual.CopyTo(0, bytes, 0, 1));
    }

    /// <summary>不支持的位深不能静默损失精度。</summary>
    [TestMethod]
    public void Gray16IsExplicitlyUnsupportedWithoutInventedImages()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(16, 16, EPixelLayout.Gray16), new byte[512]);
        using var glyph = new OpenCvGlyphComparer().Compare(image, image, new GlyphComparisonOptions());
        var ink = new OpenCvInkInspector().Inspect(image, new PointD(), new InkInspectionOptions());
        Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, glyph.Status);
        Assert.IsNull(glyph.Actual);
        Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, ink.Status);
        Assert.IsFalse(ink.Passed);
    }

    /// <summary>支持的彩色布局保持单色测量结果一致。</summary>
    [TestMethod]
    [DataRow(EPixelLayout.Bgr24)]
    [DataRow(EPixelLayout.Rgb24)]
    [DataRow(EPixelLayout.Bgra32)]
    [DataRow(EPixelLayout.Rgba32)]
    public void EightBitColorLayoutsProduceSameGlyph(EPixelLayout layout)
    {
        var gray = Glyph();
        var info = new ImageInfo(16, 16, layout);
        var pixels = new byte[info.ByteLength];
        for (int i = 0; i < gray.Length; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                pixels[i * info.BytesPerPixel + c] = gray[i];
            }

            if (info.BytesPerPixel == 4)
            {
                pixels[i * 4 + 3] = 255;
            }
        }

        using var color = VisionImage.CopyFrom(info, pixels);
        using var reference = Gray(gray);
        using var result = new OpenCvGlyphComparer().Compare(color, reference, new GlyphComparisonOptions());
        Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
        Assert.AreEqual(0d, result.Difference);
    }

    /// <summary>多个连通域保留原图坐标位置。</summary>
    [TestMethod]
    public void BlankReturnsAllComponentsInOriginalCoordinates()
    {
        var pixels = White();
        pixels[2 * 16 + 3] = 0;
        pixels[10 * 16 + 12] = 0;
        using var image = Gray(pixels);
        var result = new OpenCvInkInspector().Inspect(
            image,
            new PointD(100, 200),
            new InkInspectionOptions(minimumArea: 1)
        );
        Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
        Assert.IsFalse(result.Passed);
        Assert.AreEqual(2, result.Defects.Count);
        Assert.IsTrue(result.Defects.Any(d => d.Bounds.X == 103 && d.Bounds.Y == 202 && d.Area == 1));
        Assert.IsTrue(result.Defects.Any(d => d.Bounds.X == 112 && d.Bounds.Y == 210));
    }

    /// <summary>空检查范围属于未完成，不是无缺陷通过。</summary>
    [TestMethod]
    public void FullyExcludedScopeDoesNotPass()
    {
        using var image = Gray(White());
        using var mask = Gray(new byte[256]);
        var result = new OpenCvInkInspector().Inspect(image, new PointD(), new InkInspectionOptions(), mask);
        Assert.AreEqual(0, result.Defects.Count);
        Assert.AreEqual(EAlgorithmStatus.InsufficientEvidence, result.Status);
        Assert.IsFalse(result.Passed);
    }

    /// <summary>两个方向的差异都保留可见证据。</summary>
    [TestMethod]
    public void FixedReturnsMissingAndExtraSeparately()
    {
        var actual = White();
        var reference = White();
        actual[10 * 16 + 10] = 0;
        reference[3 * 16 + 3] = 0;
        using var a = Gray(actual);
        using var r = Gray(reference);
        var result = new OpenCvInkInspector().Inspect(
            a,
            r,
            new PointD(20, 40),
            new InkInspectionOptions(tolerance: 0, minimumArea: 1)
        );
        Assert.AreEqual(2, result.Defects.Count);
        Assert.IsTrue(result.Defects.Any(d => d.Code == "missing_ink" && d.Bounds.X == 23));
        Assert.IsTrue(result.Defects.Any(d => d.Code == "extra_ink" && d.Bounds.X == 30));
    }

    /// <summary>掩码膨胀不能造成空范围仍判成功。</summary>
    [TestMethod]
    public void ToleranceHaloCanBlockAllFixedMeasurements()
    {
        var mask = new byte[256];
        mask[8 * 16 + 8] = 255;
        using var image = Gray(White());
        using var allowed = Gray(mask);
        var result = new OpenCvInkInspector().Inspect(
            image,
            image,
            new PointD(),
            new InkInspectionOptions(),
            allowed
        );
        Assert.AreEqual("empty_effective_scope", result.ReasonCode);
        Assert.IsFalse(result.Passed);
    }

    /// <summary>强制检查几何和二值掩码不变量。</summary>
    [TestMethod]
    public void NonBinaryMaskAndReferenceSizeMismatchAreRejected()
    {
        using var image = Gray(White());
        using var invalidMask = Gray(Enumerable.Repeat((byte)100, 256).ToArray());
        using var small = Gray(new byte[16]);
        var algorithm = new OpenCvInkInspector();
        Assert.ThrowsExactly<ArgumentException>(() =>
            algorithm.Inspect(image, new PointD(), new InkInspectionOptions(), invalidMask)
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            algorithm.Inspect(image, small, new PointD(), new InkInspectionOptions())
        );
    }

    /// <summary>取消保持显式传播。</summary>
    [TestMethod]
    public void CancellationIsNotConvertedToSuccess()
    {
        using var image = Gray(Glyph());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new OpenCvGlyphComparer().Compare(image, image, new GlyphComparisonOptions(), cancellation.Token)
        );
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new OpenCvInkInspector().Inspect(
                image,
                new PointD(),
                new InkInspectionOptions(),
                token: cancellation.Token
            )
        );
    }

    /// <summary>结果构造函数拒绝有歧义的空成功结果。</summary>
    [TestMethod]
    public void CompletedResultRequiresEvidenceAndFailuresRequireReasons()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new GlyphComparisonResult(EAlgorithmStatus.Completed, "", 0, 0, 0)
        );
        Assert.ThrowsExactly<ArgumentException>(() =>
            new InkInspectionResult(EAlgorithmStatus.InsufficientEvidence, "", "", Array.Empty<InkDefect>())
        );
    }

    /// <summary>契约不能引入反向依赖。</summary>
    [TestMethod]
    public void NeutralAssembliesDoNotReferenceBusinessOrVendorAssemblies()
    {
        foreach (var assembly in new[] { typeof(IImageSource).Assembly, typeof(IGlyphComparer).Assembly })
        {
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                Assert.IsFalse(
                    name.Name!.Contains("LabelInspection")
                        || name.Name.Contains("OpenCv")
                        || name.Name.Contains("Halcon")
                );
            }
        }
    }
}
