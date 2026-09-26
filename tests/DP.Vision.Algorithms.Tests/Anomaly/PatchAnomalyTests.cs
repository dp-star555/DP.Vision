using System;
using System.Linq;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>局部块异常检测：只用良品训练，未见过的良品通过，内部空洞与纸面斑点被定位。</summary>
[TestClass]
public sealed class PatchAnomalyTests
{
    internal const int Width = 160,
        Height = 48;

    /// <summary>若干“笔画”（竖、横、L形），整体平移shift像素，叠加确定性噪声。</summary>
    internal static byte[] Strokes(int shift, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[Width * Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(236 + random.Next(-6, 7));
        }

        void Paint(int x, int y, int w, int h)
        {
            for (int row = y; row < y + h; row++)
            {
                for (int col = x + shift; col < x + shift + w; col++)
                {
                    pixels[row * Width + col] = (byte)(30 + random.Next(-6, 7));
                }
            }
        }

        Paint(10, 8, 5, 32);
        Paint(30, 8, 20, 5);
        Paint(37, 8, 5, 32);
        Paint(62, 8, 5, 32);
        Paint(62, 35, 20, 5);
        Paint(95, 8, 5, 32);
        Paint(95, 21, 18, 5);
        Paint(113, 8, 5, 32);
        return pixels;
    }

    internal static IImageSource Image(byte[] pixels)
    {
        return VisionImage.CopyFrom(new ImageInfo(Width, Height, EPixelLayout.Gray8), pixels);
    }

    private static PatchAnomalyModel Train(PatchAnomalyOptions options)
    {
        var good = new[] { Image(Strokes(0, 1)), Image(Strokes(2, 2)), Image(Strokes(5, 3)) };
        try
        {
            return new OpenCvPatchAnomalyDetector().Train(good, options);
        }
        finally
        {
            foreach (var g in good)
            {
                g.Dispose();
            }
        }
    }

    private static QualityFinding[] Defects(PatchAnomalyResult result)
    {
        return result.Findings.Where(f => f.Kind == EQualityFindingKind.Defect).ToArray();
    }

    /// <summary>与位置无关模式：未参与训练的平移良品通过；纸面上与任何良品形态都不同的斑点被定位。</summary>
    [TestMethod]
    public void GoodPassesAndSpeckIsLocated()
    {
        var options = new PatchAnomalyOptions();
        var model = Train(options);
        var detector = new OpenCvPatchAnomalyDetector();

        using (var clean = Image(Strokes(3, 4)))
        using (var result = detector.Detect(clean, model, options))
        {
            Assert.IsTrue(result.Passed, string.Join(";", Defects(result).Select(f => f.Message)));
            Assert.AreEqual(Width, result.HeatMap!.Info.Width);
        }

        var speck = Strokes(3, 6);
        for (int y = 28; y < 32; y++)
        {
            for (int x = 140; x < 144; x++)
            {
                speck[y * Width + x] = 40;
            }
        }

        using (var image = Image(speck))
        using (var result = detector.Detect(image, model, options))
        {
            var box = Defects(result).Single().Bounds!.Value;
            Assert.IsTrue(box.X <= 143 && box.X + box.Width >= 140, box.ToString());
        }
    }

    /// <summary>
    /// 位置相关模式：横贯笔画的断裂看起来像两个正常笔画端点、笔画内小空洞像笔画边缘，与位置无关的记忆库会把它们“解释”掉；
    /// 只与良品同位置比较时二者都被定位。裁图尺寸与训练不一致时拒绝。
    /// </summary>
    [TestMethod]
    public void LocalModeLocatesBrokenStrokeAndRequiresSameSize()
    {
        var options = new PatchAnomalyOptions(localRadius: 3);
        var good = new[] { Image(Strokes(0, 1)), Image(Strokes(1, 2)), Image(Strokes(2, 3)) };
        var model = new OpenCvPatchAnomalyDetector().Train(good, options);
        foreach (var g in good)
        {
            g.Dispose();
        }

        Assert.AreEqual(3, model.Radius);
        var restored = PatchAnomalyModel.FromBytes(model.ToBytes());
        Assert.AreEqual(Width, restored.Width);
        var detector = new OpenCvPatchAnomalyDetector();
        using (var clean = Image(Strokes(1, 4)))
        using (var result = detector.Detect(clean, restored, options))
        {
            Assert.IsTrue(result.Passed, string.Join(";", Defects(result).Select(f => f.Message)));
        }

        var broken = Strokes(1, 5);
        for (int y = 22; y < 26; y++)
        {
            for (int x = 38; x < 43; x++)
            {
                broken[y * Width + x] = 236;
            }
        }

        using (var image = Image(broken))
        using (var result = detector.Detect(image, restored, options))
        {
            var box = Defects(result).Single().Bounds!.Value;
            Assert.IsTrue(
                box.X <= 42 && box.X + box.Width >= 38 && box.Y <= 25 && box.Y + box.Height >= 22,
                box.ToString()
            );
        }

        var voided = Strokes(1, 6);
        for (int y = 20; y < 24; y++)
        {
            for (int x = 63; x < 66; x++)
            {
                voided[y * Width + x] = 236;
            }
        }

        using (var image = Image(voided))
        using (var result = detector.Detect(image, restored, options))
        {
            var box = Defects(result).Single().Bounds!.Value;
            Assert.IsTrue(
                box.X <= 65 && box.X + box.Width >= 63 && box.Y <= 23 && box.Y + box.Height >= 20,
                box.ToString()
            );
        }

        using var small = VisionImage.CopyFrom(
            new ImageInfo(Width - 8, Height, EPixelLayout.Gray8),
            new byte[(Width - 8) * Height]
        );
        using var mismatch = detector.Detect(small, restored, options);
        Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, mismatch.Status);
    }

    /// <summary>
    /// 位置相关模式：良品全部同一位置（训练时对齐得很准），检测时整体平移1像素（奇数）或2像素的良品仍须通过；
    /// 最大得分只统计边缘带以外的块，与报告的异常区域一致。
    /// </summary>
    [TestMethod]
    public void LocalModeAbsorbsOddPixelShifts()
    {
        var options = new PatchAnomalyOptions(localRadius: 3);
        var good = new[] { Image(Strokes(2, 1)), Image(Strokes(2, 2)), Image(Strokes(2, 3)) };
        var model = new OpenCvPatchAnomalyDetector().Train(good, options);
        foreach (var g in good)
        {
            g.Dispose();
        }

        var detector = new OpenCvPatchAnomalyDetector();
        foreach (int shift in new[] { 1, 2, 3, 4 })
        {
            using var image = Image(Strokes(shift, 10 + shift));
            using var result = detector.Detect(image, model, options);
            Assert.IsTrue(
                result.Passed && result.MaximumScore < result.Threshold,
                $"shift {shift - 2}: {result.MaximumScore:F3}/{result.Threshold:F3} "
                    + string.Join(";", Defects(result).Select(f => f.Message))
            );
        }
    }

    /// <summary>兼容性：版本1（没有特征来源字段）的模型文件仍可读取，视为手工特征；未知版本明确拒绝。</summary>
    [TestMethod]
    public void VersionOneModelsRemainReadable()
    {
        var memory = Enumerable.Range(0, 128 * 3).Select(i => (float)(i % 7)).ToArray();
        byte[] v1;
        using (var stream = new System.IO.MemoryStream())
        {
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write(0x41505044);
                writer.Write(1);
                writer.Write(8);
                writer.Write(128);
                writer.Write(.5);
                writer.Write(2);
                writer.Write("v1");
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(memory.Length);
                foreach (float v in memory)
                {
                    writer.Write(v);
                }
            }

            v1 = stream.ToArray();
        }

        var model = PatchAnomalyModel.FromBytes(v1);
        Assert.AreEqual(PatchAnomalyModel.Handcrafted, model.FeatureSource);
        Assert.AreEqual(3, model.Count);
        CollectionAssert.AreEqual(memory, model.CopyMemory());
        v1[4] = 9;
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => PatchAnomalyModel.FromBytes(v1));
    }

    /// <summary>模型序列化往返后参数与记忆库不变；块大小不一致时拒绝而不是误判。</summary>
    [TestMethod]
    public void ModelRoundTripsAndRejectsMismatchedPatchSize()
    {
        var model = Train(new PatchAnomalyOptions(memorySize: 512));
        var restored = PatchAnomalyModel.FromBytes(model.ToBytes());
        Assert.AreEqual(model.Count, restored.Count);
        Assert.AreEqual(model.Threshold, restored.Threshold);
        CollectionAssert.AreEqual(model.CopyMemory(), restored.CopyMemory());

        using var image = Image(Strokes(3, 4));
        using var result = new OpenCvPatchAnomalyDetector().Detect(
            image,
            restored,
            new PatchAnomalyOptions(patchSize: 6)
        );
        Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, result.Status);
        Assert.IsFalse(result.Passed);
    }
}
