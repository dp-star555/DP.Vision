using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Runtime.Tests;

/// <summary>真实模板制作、共享加载、资源完整性和坐标约束回归。</summary>
[TestClass]
public sealed class TemplateModelResourceTests
{
    /// <summary>资源共享、冻结快照和掩码内定位证据。</summary>
    [TestMethod]
    public async Task PublishedModel_SharedSnapshot_MasksAndReferenceCoordinates()
    {
        var root = Path.Combine(Path.GetTempPath(), "vision-template-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var source = Image(6, 6, (x, y) => (byte)(20 + x * 19 + y * 13));
            using var frame = new ImageFrame("sample", source);
            var definition = new VisionTemplateDefinition { SourceWidth = 6, SourceHeight = 6, X = 1, Y = 1, Width = 3, Height = 3, OriginX = 2, OriginY = 2, AxisAngleRadians = Math.PI / 2 };
            var mask = new RegionGeometry(new[] { new RegionRun(1, 1, 4), new RegionRun(2, 1, 3), new RegionRun(3, 1, 4) });
            var build = await new OpenCvTemplateModelBuilder("opencv.template-model").BuildAsync(new VisionTemplateBuildRequest(frame, definition, mask, new Dictionary<string, string>()));
            var templateId = Guid.NewGuid().ToString("N");
            var relative = VisionTemplateStore.Publish(root, templateId, build);
            var sameContentRevision = VisionTemplateStore.Publish(root, templateId, build);
            var manifestPath = Path.Combine(root, relative);
            using var runtime = new VisionAlgorithmRuntime(VisionAlgorithmCatalog.Compose(new[] { new OpenCvVisionAlgorithmModule() }));
            using var plan = await runtime.PrepareAsync(new[] { Request("a", relative), Request("b", sameContentRevision) }, new VisionAlgorithmResourceContext(root), CancellationToken.None);
            var instance = plan.Invoke<IPreparedVisionTemplateMatcher, object>("a", m => m);
            Assert.AreSame(instance, plan.Invoke<IPreparedVisionTemplateMatcher, object>("b", m => m));
            var bytes = new byte[10 * 10];
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) bytes[(y + 4) * 10 + x + 3] = (byte)(20 + (x + 1) * 19 + (y + 1) * 13);
            bytes[5 * 10 + 5] = 255; // 被模板排除的像素不能影响分数。
            using var sceneImage = VisionImage.CopyFrom(new ImageInfo(10, 10, EPixelLayout.Gray8), bytes); using var scene = new ImageFrame("scene", sceneImage);
            var result = plan.Invoke<IPreparedVisionTemplateMatcher, TemplatePoseResult>("a", m => m.Match(scene, new PixelBounds(0, 0, 10, 10), new TemplatePoseOptions(new[] { 0d }, new[] { 1d }, .999))
                .InReferenceCoordinates("reference", scene, m.Definition, m.ModelIdentity));
            Assert.IsTrue(result.Found); Assert.AreEqual(4d, result.CoordinateSystem!.LocalToImage.Tx, 1e-6); Assert.AreEqual(5d, result.CoordinateSystem.LocalToImage.Ty, 1e-6);
            Assert.AreEqual(0d, result.CoordinateSystem.LocalToImage.M11, 1e-6); Assert.AreEqual(1d, result.CoordinateSystem.LocalToImage.M21, 1e-6);
            var dataFile = Path.Combine(Path.GetDirectoryName(manifestPath)!, "variants/opencv-gray/model.bin"); File.WriteAllBytes(dataFile, new byte[] { 0 });
            var stillFound = plan.Invoke<IPreparedVisionTemplateMatcher, bool>("a", m => m.Match(scene, new PixelBounds(0, 0, 10, 10), new TemplatePoseOptions(new[] { 0d }, new[] { 1d }, .999)).Found);
            Assert.IsTrue(stillFound);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("broken", relative) }, new VisionAlgorithmResourceContext(root), CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>部署错误与取消不发布可用资源。</summary>
    [TestMethod]
    public async Task EngineMismatch_MissingAndCancelledPublish_DoNotProduceUsableResource()
    {
        var root = Path.Combine(Path.GetTempPath(), "vision-template-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var image = Image(3, 3, (x, y) => (byte)(x * 50 + y)); using var frame = new ImageFrame("sample", image);
            var d = new VisionTemplateDefinition { SourceWidth = 3, SourceHeight = 3, Width = 3, Height = 3 };
            var build = await new OpenCvTemplateModelBuilder("opencv.template-model").BuildAsync(new VisionTemplateBuildRequest(frame, d, null, new Dictionary<string, string>()));
            var path = VisionTemplateStore.Publish(root, Guid.NewGuid().ToString("N"), build);
            using var runtime = new VisionAlgorithmRuntime(VisionAlgorithmCatalog.Compose(new[] { new OpenCvVisionAlgorithmModule() }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("wrong", path, "opencv.template-pose-model") }, new VisionAlgorithmResourceContext(root), CancellationToken.None));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("missing", "missing/manifest.json") }, new VisionAlgorithmResourceContext(root), CancellationToken.None));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => VisionTemplateStore.Publish(root, Guid.NewGuid().ToString("N"), build, cancel.Token));
            Assert.AreEqual(1, Directory.GetFiles(root, "manifest.json", SearchOption.AllDirectories).Length);
            Assert.ThrowsExactly<InvalidDataException>(() => VisionTemplateStore.Publish(root, Guid.NewGuid().ToString("N"),
                new VisionTemplateBuild(build.ImplementationId, build.Format, d, build.Settings, new[] { new VisionTemplateArtifact("../outside.bin", new byte[] { 1 }) })));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>制作和运行范围不能被静默放宽。</summary>
    [TestMethod]
    public async Task PreparationRanges_AndEmptyMasks_AreEnforced()
    {
        using var image = Image(4, 4, (x, y) => (byte)(x * 50 + y)); using var frame = new ImageFrame("sample", image);
        var d = new VisionTemplateDefinition { SourceWidth = 4, SourceHeight = 4, Width = 4, Height = 4 };
        var builder = new OpenCvTemplateModelBuilder("opencv.template-pose-model");
        Assert.ThrowsExactly<ArgumentException>(() => builder.BuildAsync(new VisionTemplateBuildRequest(frame, d, new RegionGeometry(Array.Empty<RegionRun>()), new Dictionary<string, string>())));
        var build = await builder.BuildAsync(new VisionTemplateBuildRequest(frame, d, null, new Dictionary<string, string> { ["minimumAngle"] = "0", ["maximumAngle"] = "0", ["minimumScale"] = "1", ["maximumScale"] = "1" }));
        using var resource = await new OpenCvTemplateModelFactory(true).PreparePreviewAsync(build);
        var matcher = (IPreparedVisionTemplateMatcher)resource.Instance;
        Assert.ThrowsExactly<ArgumentException>(() => matcher.Match(frame, new PixelBounds(0, 0, 4, 4), new TemplatePoseOptions(new[] { .2 }, new[] { 1d })));
        var mask = new RegionGeometry(Array.Empty<RegionRun>());
        Assert.IsFalse(matcher.Match(frame, new PixelBounds(0, 0, 4, 4), new TemplatePoseOptions(new[] { 0d }, new[] { 1d }), mask).Found);
    }

    /// <summary>真实旋转模型在冻结资源上检出，并转换参考原点。</summary>
    [TestMethod]
    public async Task PoseModel_UsesRotatedPreparedPixels_AndReturnsReferenceTransform()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(3, 2, EPixelLayout.Gray8), new byte[] { 10, 60, 180, 220, 30, 120 });
        using var source = new ImageFrame("source", image);
        var definition = new VisionTemplateDefinition { SourceWidth = 3, SourceHeight = 2, Width = 3, Height = 2, ReferenceIdentity = "rotation-reference" };
        var build = await new OpenCvTemplateModelBuilder("opencv.template-pose-model").BuildAsync(new VisionTemplateBuildRequest(source, definition, null, new Dictionary<string, string>()));
        using var resource = await new OpenCvTemplateModelFactory(true).PreparePreviewAsync(build);
        var matcher = (IPreparedVisionTemplateMatcher)resource.Instance;
        var pixels = new byte[8 * 8]; byte[] rotated = { 220, 10, 30, 60, 120, 180 };
        for (int y = 0; y < 3; y++) for (int x = 0; x < 2; x++) pixels[(y + 2) * 8 + x + 3] = rotated[y * 2 + x];
        using var sceneImage = VisionImage.CopyFrom(new ImageInfo(8, 8, EPixelLayout.Gray8), pixels); using var scene = new ImageFrame("scene", sceneImage);
        var result = matcher.Match(scene, new PixelBounds(0, 0, 8, 8), new TemplatePoseOptions(new[] { Math.PI / 2 }, new[] { 1d }, .999))
            .InReferenceCoordinates("coordinate", scene, matcher.Definition, matcher.ModelIdentity);
        Assert.IsTrue(result.Found); Assert.AreEqual(Math.PI / 2, result.Transform!.AngleRadians, 1e-10);
        Assert.AreEqual(5d, result.CoordinateSystem!.LocalToImage.Tx, 1e-6); Assert.AreEqual(2d, result.CoordinateSystem.LocalToImage.Ty, 1e-6);
        var back = result.CoordinateSystem.ImageToLocal.Map(result.CoordinateSystem.LocalToImage.Map(new Coordinate2D(.2, .4)));
        Assert.AreEqual(.2, back.X, 1e-6); Assert.AreEqual(.4, back.Y, 1e-6);
    }

    private static IImageSource Image(int width, int height, Func<int, int, byte> value) => VisionImage.CopyFrom(new ImageInfo(width, height, EPixelLayout.Gray8),
        Enumerable.Range(0, width * height).Select(i => value(i % width, i / width)).ToArray());
    private static VisionAlgorithmRequest Request(string key, string path, string id = "opencv.template-model") => new VisionAlgorithmRequest(key, typeof(IPreparedVisionTemplateMatcher),
        new VisionAlgorithmSelection { ImplementationId = id, Settings = new Dictionary<string, string> { ["templatePath"] = path } });
}
