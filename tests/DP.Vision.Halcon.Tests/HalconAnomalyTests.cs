using System;
using System.Linq;
using DP.Vision.Algorithms;
using DP.Vision.Halcon;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>HALCON真实原生模型通过公共资产/运行实例契约训练、保存、重载与检测。</summary>
[TestClass]
public sealed class HalconAnomalyTests
{
    private static IImageSource Image(bool damaged = false, int seed = 0)
    {
        var pixels = Enumerable.Range(0, 64 * 64).Select(i => (byte)(240 + (seed == 0 ? 0 : (i * 17 + seed) % 5 - 2))).ToArray();
        for (int y = 16; y < 48; y++) for (int x = 20; x < 44; x++) pixels[y * 64 + x] = 20;
        if (damaged) for (int y = 24; y < 40; y++) for (int x = 24; x < 40; x++) pixels[y * 64 + x] = 240;
        return VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), pixels);
    }
    /// <summary>传统变差模型不经DPPA解释，缺笔有原图局部区域与热力图。</summary>
    [TestMethod]
    public void NativeVariationRoundTripDetectsMissingInk()
    {
#if HALCON_SDK
        Check(EHalconAnomalyMethod.Variation);
#else
        Assert.Inconclusive("未装配HALCON SDK。");
#endif
    }
    /// <summary>HALCON深度模型真实CPU训练，只用正常来源，独立来源标定，保存原生hdl后重载。</summary>
    [TestMethod]
    public void NativeDeepAnomalyCpuTrainingAndInference()
    {
#if HALCON_SDK
        Check(EHalconAnomalyMethod.AnomalyDetection);
#else
        Assert.Inconclusive("未装配HALCON SDK。");
#endif
    }
    private static void Check(EHalconAnomalyMethod method)
    {
        var implementation = new HalconAnomalyImplementation(method, maximumEpochs: 5);
        using var a = Image(seed: 1); using var b = Image(seed: 2); using var c = Image(seed: 3); using var damaged = Image(true);
        var asset = implementation.Train(new[] { a, b, c }, new[] { 0, 1, 2 }, new AnomalyTrainingOptions());
        Assert.IsTrue(asset.Calibration.Contains("独立标定来源"));
        var restored = AnomalyModelAsset.FromBytes(asset.ToBytes());
        using var runtime = implementation.Load(restored);
        using var good = runtime.Inspect(c, new AnomalyDetectionOptions());
        Assert.IsTrue(good.Passed, string.Join(";", good.Findings.Select(f => f.Message)));
        using var bad = runtime.Inspect(damaged, new AnomalyDetectionOptions());
        Assert.IsFalse(bad.Passed, $"good={good.MaximumScore:F6}, bad={bad.MaximumScore:F6}, threshold={bad.Threshold:F6}; {asset.Calibration}");
        Assert.IsTrue(bad.Findings.Any(f => f.Kind == EQualityFindingKind.Defect && f.Bounds.HasValue));
        Assert.IsNotNull(bad.HeatMap);
        Assert.ThrowsExactly<ArgumentException>(() => new HalconAnomalyImplementation(method).Load(
            new AnomalyModelAsset("other", "other.v1", 64, 64, 1, 3, "", new System.Collections.Generic.Dictionary<string, byte[]> { ["model.bin"] = new byte[] { 1 } })));
    }
}
