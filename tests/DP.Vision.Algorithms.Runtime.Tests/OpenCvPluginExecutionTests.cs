using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Runtime.Tests;

/// <summary>真实模型在插件准备路径上加载，接口调用保持模型兼容规则。</summary>
[TestClass]
public sealed class OpenCvPluginExecutionTests
{
    /// <summary>两个实现共存，CNN不同初始化不误共享，文件变化不影响冻结快照。</summary>
    [TestMethod]
    public async Task PatchFactories_RealBackbone_SnapshotSharingAndModelCompatibility()
    {
        using var runtime = new VisionAlgorithmRuntime(VisionAlgorithmCatalog.Compose(new[] { new OpenCvVisionAlgorithmModule() }));
        var path = Path.Combine(Path.GetTempPath(), "backbone-" + Guid.NewGuid().ToString("N") + ".onnx");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "ppocrv4_det_backbone.onnx"), path);
        try
        {
            using var plan = await runtime.PrepareAsync(new[] { Request("hand", "opencv.patch"), Request("cnn", "opencv.cnn-patch", path), Request("shared", "opencv.cnn-patch", path), Request("other", "opencv.cnn-patch", path, "1.5") });
            var same = plan.Invoke<IPatchAnomalyDetector, object>("cnn", instance => instance);
            Assert.AreSame(same, plan.Invoke<IPatchAnomalyDetector, object>("shared", instance => instance));
            Assert.AreNotSame(same, plan.Invoke<IPatchAnomalyDetector, object>("other", instance => instance));
            File.WriteAllBytes(path, new byte[] { 0, 1, 2 });
            var pixels = Enumerable.Repeat((byte)235, 64 * 48).ToArray();
            for (var y = 8; y < 40; y++) for (var x = 12; x < 17; x++) pixels[y * 64 + x] = 20;
            using var image = VisionImage.CopyFrom(new ImageInfo(64, 48, EPixelLayout.Gray8), pixels);
            var options = new PatchAnomalyOptions(localRadius: 3);
            var handModel = plan.Invoke<IPatchAnomalyDetector, PatchAnomalyModel>("hand", detector => detector.Train(new[] { image }, options));
            var cnnModel = plan.Invoke<IPatchAnomalyDetector, PatchAnomalyModel>("cnn", detector => detector.Train(new[] { image }, options));
            Assert.AreNotEqual(handModel.FeatureSource, cnnModel.FeatureSource);
            using var mismatch = plan.Invoke<IPatchAnomalyDetector, PatchAnomalyResult>("cnn", detector => detector.Detect(image, handModel, options));
            Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, mismatch.Status);
            using var scaleMismatch = plan.Invoke<IPatchAnomalyDetector, PatchAnomalyResult>("other", detector => detector.Detect(image, cnnModel, options));
            Assert.AreEqual(EAlgorithmStatus.UnsupportedInput, scaleMismatch.Status);
            using var correct = plan.Invoke<IPatchAnomalyDetector, PatchAnomalyResult>("cnn", detector => detector.Detect(image, cnnModel, options));
            Assert.AreNotEqual(EAlgorithmStatus.UnsupportedInput, correct.Status);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("changed", "opencv.cnn-patch", path) }));
        }
        finally { File.Delete(path); }
    }

    private static VisionAlgorithmRequest Request(string key, string id, string? model = null, string scale = "2") =>
        new VisionAlgorithmRequest(key, typeof(IPatchAnomalyDetector), new VisionAlgorithmSelection { ImplementationId = id,
            Settings = model == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["backbonePath"] = model, ["scale"] = scale } });
}
