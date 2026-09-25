using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>CNN骨干网络特征的局部块异常检测，以及模型格式、特征来源与并发的兼容性。</summary>
[TestClass]
public sealed class CnnPatchAnomalyTests
{
    private const int Width = PatchAnomalyTests.Width;

    private static string Backbone =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ppocrv4_det_backbone.onnx");

    private static byte[] Voided(int seed)
    {
        var pixels = PatchAnomalyTests.Strokes(1, seed);
        for (int y = 20; y < 25; y++)
        {
            for (int x = 63; x < 66; x++)
            {
                pixels[y * Width + x] = 236;
            }
        }

        return pixels;
    }

    private static PatchAnomalyModel Train(
        OpenCvCnnPatchAnomalyDetector detector,
        PatchAnomalyOptions options
    )
    {
        var good = new[]
        {
            PatchAnomalyTests.Image(PatchAnomalyTests.Strokes(0, 1)),
            PatchAnomalyTests.Image(PatchAnomalyTests.Strokes(1, 2)),
            PatchAnomalyTests.Image(PatchAnomalyTests.Strokes(2, 3)),
        };
        try
        {
            return detector.Train(good, options);
        }
        finally
        {
            foreach (var g in good)
            {
                g.Dispose();
            }
        }
    }

    /// <summary>位置相关CNN特征：良品通过，笔画内空洞被定位；模型记录骨干网络哈希。</summary>
    [TestMethod]
    public void CnnLocalModePassesGoodAndLocatesVoid()
    {
        using var detector = new OpenCvCnnPatchAnomalyDetector(Backbone);
        var options = new PatchAnomalyOptions(localRadius: 3);
        var model = PatchAnomalyModel.FromBytes(Train(detector, options).ToBytes());
        StringAssert.StartsWith(model.FeatureSource, "cnn:dfcac0b905b3ecb7@2");

        using (var clean = PatchAnomalyTests.Image(PatchAnomalyTests.Strokes(1, 4)))
        using (var result = detector.Detect(clean, model, options))
        {
            Assert.IsTrue(result.Passed, string.Join(";", result.Findings.Select(f => f.Message)));
        }

        using var voided = PatchAnomalyTests.Image(Voided(5));
        using var found = detector.Detect(voided, model, options);
        var box = found.Findings.Single(f => f.Kind == EQualityFindingKind.Defect).Bounds!.Value;
        Assert.IsTrue(
            box.X <= 66 && box.X + box.Width >= 63 && box.Y <= 25 && box.Y + box.Height >= 20,
            box.ToString()
        );
    }

    /// <summary>兼容性：手工特征模型与CNN模型互不接受，拒绝而不是给出无意义得分。</summary>
    [TestMethod]
    public void FeatureSourcesAreNotInterchangeable()
    {
        using var cnn = new OpenCvCnnPatchAnomalyDetector(Backbone);
        var handcrafted = new OpenCvPatchAnomalyDetector();
        var options = new PatchAnomalyOptions(localRadius: 3);
        var cnnModel = Train(cnn, options);
        var good = new[] { PatchAnomalyTests.Image(PatchAnomalyTests.Strokes(0, 1)) };
        var handModel = handcrafted.Train(good, options);
        good[0].Dispose();
        using var image = PatchAnomalyTests.Image(PatchAnomalyTests.Strokes(1, 4));
        using (var r = handcrafted.Detect(image, cnnModel, options))
        {
            Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, r.Status);
        }

        using (var r = cnn.Detect(image, handModel, options))
        {
            Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, r.Status);
        }

        using var other = new OpenCvCnnPatchAnomalyDetector(Backbone, 1.5);
        using (var r = other.Detect(image, cnnModel, options))
        {
            Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, r.Status);
        }
    }

    /// <summary>兼容性：同一检测器并发检测（骨干网络前向串行化）结果与串行一致。</summary>
    [TestMethod]
    public void ConcurrentDetectionIsDeterministic()
    {
        using var detector = new OpenCvCnnPatchAnomalyDetector(Backbone);
        var options = new PatchAnomalyOptions(localRadius: 3);
        var model = Train(detector, options);
        var pixels = Voided(6);
        double expected;
        using (var image = PatchAnomalyTests.Image(pixels))
        using (var result = detector.Detect(image, model, options))
        {
            expected = result.MaximumScore;
        }

        var scores = new double[8];
        Parallel.For(
            0,
            scores.Length,
            i =>
            {
                using var image = PatchAnomalyTests.Image(pixels);
                using var result = detector.Detect(image, model, options);
                scores[i] = result.MaximumScore;
            }
        );
        CollectionAssert.AreEqual(Enumerable.Repeat(expected, scores.Length).ToArray(), scores);
    }

    /// <summary>兼容性：极小裁图与彩色裁图不抛异常；缺失或非两输出的骨干网络在构造时明确拒绝。</summary>
    [TestMethod]
    public void EdgeInputsAreHandled()
    {
        using var detector = new OpenCvCnnPatchAnomalyDetector(Backbone);
        var options = new PatchAnomalyOptions();
        var model = Train(detector, options);
        using (
            var tiny = VisionImage.CopyFrom(
                new ImageInfo(6, 5, EPixelLayout.Gray8),
                Enumerable.Repeat((byte)230, 30).ToArray()
            )
        )
        using (var result = detector.Detect(tiny, model, options))
        {
            Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
            Assert.IsTrue(result.Passed);
        }

        var gray = PatchAnomalyTests.Strokes(1, 7);
        var bgr = gray.SelectMany(v => new[] { v, v, v }).ToArray();
        using (var color = VisionImage.CopyFrom(new ImageInfo(Width, 48, EPixelLayout.Bgr24), bgr))
        using (var result = detector.Detect(color, model, options))
        {
            Assert.AreEqual(EAlgorithmStatus.Completed, result.Status);
        }

        Assert.ThrowsExactly<FileNotFoundException>(() => new OpenCvCnnPatchAnomalyDetector("missing.onnx"));
    }
}
