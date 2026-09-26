using System;
using System.Linq;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>一维码印刷检查的边缘带语义及指示性扫描等级。</summary>
[TestClass]
public sealed class LinearQualityDepthTests
{
    private const int Width = 360,
        Height = 140;

    /// <summary>16根条，条宽barWidth、周期pitch，条高y∈[20,110)。</summary>
    private static byte[] Bars(int barWidth, int pitch)
    {
        var pixels = Enumerable.Repeat((byte)255, Width * Height).ToArray();
        for (int i = 0; i < 16; i++)
        {
            Paint(pixels, 20 + i * pitch, 20, barWidth, 90, 0);
        }

        return pixels;
    }

    private static void Paint(byte[] pixels, int x, int y, int w, int h, byte value)
    {
        for (int row = y; row < y + h; row++)
        {
            for (int col = x; col < x + w; col++)
            {
                pixels[row * Width + col] = value;
            }
        }
    }

    private static BarcodeQualityResult Inspect(byte[] pixels)
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), pixels);
        return new OpenCvBarcodePrintInspector().Inspect(
            image,
            new PixelBounds(0, 0, Width, Height),
            Array.Empty<BarcodeObservation>(),
            new BarcodePrintOptions()
        );
    }

    private static QualityFinding[] Defects(BarcodeQualityResult result)
    {
        return result.Findings.Where(f => f.Kind == EQualityFindingKind.Defect).ToArray();
    }

    /// <summary>条边毛刺和参差的条端只触及边缘带，属于印刷波动。</summary>
    [TestMethod]
    public void RaggedEdgesAndBarEndsAreIgnored()
    {
        var pixels = Bars(8, 16);
        Paint(pixels, 68, 40, 1, 6, 255);
        Paint(pixels, 92, 60, 1, 5, 0);
        Paint(pixels, 101, 20, 5, 1, 255);
        Paint(pixels, 116, 19, 4, 1, 0);
        var result = Inspect(pixels);
        Assert.IsEmpty(Defects(result), string.Join(";", Defects(result).Select(f => f.Message)));
    }

    /// <summary>条端区（条高10%）内从条端开始的缩短/渐淡是条长波动；超出条端区的缺失仍计入。</summary>
    [TestMethod]
    public void ShortBarEndsAreIgnoredButLongLossIsNot()
    {
        var shortened = Bars(8, 16);
        Paint(shortened, 84, 104, 8, 6, 255);
        Assert.IsEmpty(
            Defects(Inspect(shortened)),
            string.Join(";", Defects(Inspect(shortened)).Select(f => f.Message))
        );

        var faded = Bars(8, 16);
        Paint(faded, 84, 105, 8, 3, 120);
        Paint(faded, 84, 108, 8, 2, 200);
        Assert.IsEmpty(Defects(Inspect(faded)));

        // 渐淡的条端与沿条边一列的灰边相连：边缘列本就不计入，条端区只按内部列判断。
        var greyEdge = Bars(8, 16);
        Paint(greyEdge, 84, 88, 1, 22, 110);
        Paint(greyEdge, 85, 106, 7, 4, 120);
        Assert.IsEmpty(
            Defects(Inspect(greyEdge)),
            string.Join(";", Defects(Inspect(greyEdge)).Select(f => f.Message))
        );

        var lost = Bars(8, 16);
        Paint(lost, 84, 90, 8, 20, 255);
        Assert.AreEqual("barcode_missing_ink", Defects(Inspect(lost)).Single().Code);
    }

    /// <summary>深入条内部的空洞按完整面积计入，而不是只计入边缘带以内的部分。</summary>
    [TestMethod]
    public void InteriorVoidIsMeasuredInFull()
    {
        var pixels = Bars(8, 16);
        Paint(pixels, 69, 50, 6, 6, 255);
        var missing = Defects(Inspect(pixels)).Single();
        Assert.AreEqual("barcode_missing_ink", missing.Code);
        Assert.AreEqual(36, missing.AreaPixels);
        Assert.AreEqual(new PixelBounds(69, 50, 6, 6), missing.Bounds);
    }

    /// <summary>细条没有边缘带以外的内部：局部斑点不计入，横贯整条宽度的断裂计入。</summary>
    [TestMethod]
    public void ThinBarsReportOnlyBreaks()
    {
        var speck = Bars(2, 12);
        Paint(speck, 68, 50, 1, 5, 255);
        Assert.IsEmpty(Defects(Inspect(speck)));

        var broken = Bars(2, 12);
        Paint(broken, 68, 50, 2, 3, 255);
        var defect = Defects(Inspect(broken)).Single();
        Assert.AreEqual("barcode_missing_ink", defect.Code);
        StringAssert.Contains(defect.Message, "细条横贯断裂");
    }

    /// <summary>
    /// 灰度变浅只在去掉边缘带后内部仍有至少4像素的条上判定；更窄的条内部由成像模糊主导，
    /// 同样的变浅不计入，二值化后的真实缺墨仍计入。
    /// </summary>
    [TestMethod]
    public void GrayLossNeedsResolvableInterior()
    {
        var wide = Bars(8, 16);
        Paint(wide, 69, 50, 6, 6, 110);
        var loss = Defects(Inspect(wide)).Single();
        Assert.AreEqual("barcode_ink_loss", loss.Code);

        var narrow = Bars(4, 12);
        Paint(narrow, 69, 50, 2, 6, 110);
        Assert.IsEmpty(
            Defects(Inspect(narrow)),
            string.Join(";", Defects(Inspect(narrow)).Select(f => f.Message))
        );

        var voided = Bars(4, 12);
        Paint(voided, 69, 50, 2, 6, 255);
        Assert.AreEqual("barcode_missing_ink", Defects(Inspect(voided)).Single().Code);
    }

    /// <summary>干净条码得到A级指示等级；最窄元素过细时不评级而不是判F。</summary>
    [TestMethod]
    public void ScanGradeIsReportedOrWithheld()
    {
        var clean = Inspect(Bars(8, 16)).Findings.Single(f => f.Code == "barcode_scan_grade");
        Assert.AreEqual(EQualityFindingKind.Information, clean.Kind);
        StringAssert.Contains(clean.Message, "：A（");

        var thin = Inspect(Bars(2, 12)).Findings.Single(f => f.Code == "barcode_scan_grade");
        StringAssert.Contains(thin.Message, "未评定");
    }

    /// <summary>经修复预处理才读出的条码报告可读性余量不足。</summary>
    [TestMethod]
    public void AssistedDecodeIsReported()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), Bars(8, 16));
        var bounds = new PixelBounds(0, 0, Width, Height);
        var symbol = new BarcodeObservation("X", "CODE_128", bounds, null, "median5");
        var result = new OpenCvBarcodePrintInspector().Inspect(
            image,
            bounds,
            new[] { symbol },
            new BarcodePrintOptions()
        );
        var assisted = result.Findings.Single(f => f.Code == "barcode_decode_assisted");
        Assert.AreEqual(EQualityFindingKind.Defect, assisted.Kind);
        Assert.AreEqual("", new BarcodeObservation("X", "CODE_128", bounds).Preprocessing);
    }
}
