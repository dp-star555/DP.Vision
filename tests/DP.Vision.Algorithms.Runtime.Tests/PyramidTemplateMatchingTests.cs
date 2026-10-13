using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.Vision.Algorithms.Runtime.Tests;

/// <summary>OpenCV金字塔NCC与形状匹配：旋转/缩放精度、光照与极性、预算、区域约束和资源发布。</summary>
[TestClass]
public sealed class PyramidTemplateMatchingTests
{
    /// <summary>测试上下文，用于输出耗时。</summary>
    public TestContext TestContext { get; set; } = null!;

    private const double Degree = Math.PI / 180;

    /// <summary>两种引擎在整周角度和±10%尺度内找到旋转缩放后的目标，位置/角度/尺度达到亚像素精度。</summary>
    [TestMethod]
    [DataRow(EOpenCvPyramidTemplateKind.Ncc, 37.3, 1.04)]
    [DataRow(EOpenCvPyramidTemplateKind.Ncc, -151.7, .93)]
    [DataRow(EOpenCvPyramidTemplateKind.Shape, 37.3, 1.04)]
    [DataRow(EOpenCvPyramidTemplateKind.Shape, -151.7, .93)]
    public async Task FullRange_FindsRotatedScaledTarget_WithSubPixelAccuracy(EOpenCvPyramidTemplateKind kind, double degrees, double scale)
    {
        using var source = Label(320, 240, 7);
        var definition = Definition(320, 240, 90, 70, 140, 100);
        using var matcher = await Prepare(kind, source, definition);
        var center = new Point2d(310.3, 255.6);
        using var scene = Scene(source, definition, 640, 480, center, degrees * Degree, scale, 11);
        var watch = Stopwatch.StartNew();
        var result = matcher.Match(scene, new PixelBounds(0, 0, 640, 480), new TemplatePoseOptions(-Math.PI, Math.PI, .9, 1.1, .7, maximumWork: 200000000));
        TestContext.WriteLine($"{kind} {degrees}° {scale}: {watch.ElapsedMilliseconds} ms, {result.Summary}");
        Assert.IsTrue(result.Found, result.Summary);
        Assert.AreEqual(center.X, result.CenterX, .35); Assert.AreEqual(center.Y, result.CenterY, .35);
        Assert.AreEqual(degrees, result.AngleDegrees, .15); Assert.AreEqual(scale, result.Scale, .006);
        Assert.IsTrue(result.Score > .85, result.Summary);
    }

    /// <summary>形状匹配对线性光照变化不敏感；整体反色时只有忽略全局极性能找到。</summary>
    [TestMethod]
    public async Task Shape_IlluminationAndPolarity()
    {
        using var source = Label(320, 240, 3);
        var definition = Definition(320, 240, 90, 70, 140, 100);
        var center = new Point2d(200.5, 180.25);
        using var dim = Scene(source, definition, 400, 360, center, 12 * Degree, 1, 5, gain: .45, offset: 60);
        using var inverted = Scene(source, definition, 400, 360, center, 12 * Degree, 1, 5, gain: -1, offset: 255);
        var options = new TemplatePoseOptions(-30 * Degree, 30 * Degree, 1, 1, .75);
        using (var matcher = await Prepare(EOpenCvPyramidTemplateKind.Shape, source, definition, new Dictionary<string, string> { ["contrast"] = "20" }))
        {
            var lit = matcher.Match(dim, new PixelBounds(0, 0, 400, 360), options);
            Assert.IsTrue(lit.Found, lit.Summary); Assert.AreEqual(center.X, lit.CenterX, .35); Assert.AreEqual(12, lit.AngleDegrees, .15);
            Assert.IsFalse(matcher.Match(inverted, new PixelBounds(0, 0, 400, 360), options).Found);
        }
        using (var matcher = await Prepare(EOpenCvPyramidTemplateKind.Shape, source, definition, new Dictionary<string, string> { ["contrast"] = "20", ["metric"] = "ignore_global_polarity" }))
        {
            var result = matcher.Match(inverted, new PixelBounds(0, 0, 400, 360), options);
            Assert.IsTrue(result.Found, result.Summary); Assert.AreEqual(center.Y, result.CenterY, .35); Assert.AreEqual(12, result.AngleDegrees, .15);
        }
    }

    /// <summary>
    /// 实际规模：1620×2519搜索区、816×115细长模板、±90°/0.5°步长、0.95..1.05尺度在默认2亿预算内完成。
    /// 原逐像素平方差引擎估算约2.3×10^15次比较而被拒绝。
    /// </summary>
    [TestMethod]
    [DataRow(EOpenCvPyramidTemplateKind.Ncc)]
    [DataRow(EOpenCvPyramidTemplateKind.Shape)]
    public async Task LargeElongatedTemplate_FitsDefaultBudget(EOpenCvPyramidTemplateKind kind)
    {
        using var source = Label(1000, 300, 21);
        var definition = Definition(1000, 300, 92, 90, 816, 115);
        using var matcher = await Prepare(kind, source, definition);
        var center = new Point2d(812.4, 1203.7);
        using var scene = Scene(source, definition, 1620, 2519, center, -63.2 * Degree, 1.02, 9);
        var options = new TemplatePoseOptions(-Math.PI / 2, Math.PI / 2, .95, 1.05, .7, 200000000, .5 * Degree, .01);
        var factory = new OpenCvPyramidTemplateFactory(kind);
        Assert.AreEqual(0, factory.ValidateSearch(definition, new Dictionary<string, string>(), new PixelBounds(0, 0, 1620, 2519), options).Count);
        var watch = Stopwatch.StartNew();
        var result = matcher.Match(scene, new PixelBounds(0, 0, 1620, 2519), options);
        long first = watch.ElapsedMilliseconds; watch.Restart();
        var again = matcher.Match(scene, new PixelBounds(0, 0, 1620, 2519), options);
        TestContext.WriteLine($"{kind}: first {first} ms, cached {watch.ElapsedMilliseconds} ms, {result.Summary}");
        Assert.IsTrue(result.Found, result.Summary);
        Assert.AreEqual(center.X, result.CenterX, .5); Assert.AreEqual(center.Y, result.CenterY, .5);
        Assert.AreEqual(-63.2, result.AngleDegrees, .1); Assert.AreEqual(1.02, result.Scale, .005);
        Assert.AreEqual(result.CenterX, again.CenterX, 1e-9);
    }

    /// <summary>没有目标时不报告找到；预算过小时在校验和运行时都给出位姿假设数。</summary>
    [TestMethod]
    [DataRow(EOpenCvPyramidTemplateKind.Ncc)]
    [DataRow(EOpenCvPyramidTemplateKind.Shape)]
    public async Task Absent_And_Budget(EOpenCvPyramidTemplateKind kind)
    {
        using var source = Label(320, 240, 4);
        var definition = Definition(320, 240, 90, 70, 140, 100);
        using var matcher = await Prepare(kind, source, definition);
        using var other = Label(500, 400, 99);
        var empty = other;
        Assert.IsFalse(matcher.Match(empty, new PixelBounds(0, 0, 500, 400), new TemplatePoseOptions(-Math.PI, Math.PI, 1, 1, .8)).Found);
        var tight = new TemplatePoseOptions(-Math.PI, Math.PI, 1, 1, .8, maximumWork: 1000);
        var errors = new OpenCvPyramidTemplateFactory(kind).ValidateSearch(definition, new Dictionary<string, string>(), new PixelBounds(0, 0, 500, 400), tight);
        Assert.AreEqual(1, errors.Count); StringAssert.Contains(errors[0], "位姿假设");
        Assert.ThrowsExactly<InvalidOperationException>(() => matcher.Match(empty, new PixelBounds(0, 0, 500, 400), tight));
    }

    /// <summary>模板必须整体落在搜索区域内；区域外的目标不被报告。</summary>
    [TestMethod]
    [DataRow(EOpenCvPyramidTemplateKind.Ncc)]
    [DataRow(EOpenCvPyramidTemplateKind.Shape)]
    public async Task Region_ExcludesTargetsOutsideIt(EOpenCvPyramidTemplateKind kind)
    {
        using var source = Label(320, 240, 5);
        var definition = Definition(320, 240, 90, 70, 140, 100);
        using var matcher = await Prepare(kind, source, definition);
        var center = new Point2d(150, 150);
        using var scene = Scene(source, definition, 600, 300, center, 0, 1, 3);
        var options = new TemplatePoseOptions(-10 * Degree, 10 * Degree, 1, 1, .8);
        var all = new RegionGeometry(Enumerable.Range(0, 300).Select(y => new RegionRun(y, 0, 600)).ToArray());
        Assert.IsTrue(matcher.Match(scene, new PixelBounds(0, 0, 600, 300), options, all).Found);
        // 区域只覆盖目标的左半边。
        var half = new RegionGeometry(Enumerable.Range(0, 300).Select(y => new RegionRun(y, 0, 150)).ToArray());
        Assert.IsFalse(matcher.Match(scene, new PixelBounds(0, 0, 600, 300), options, half).Found);
    }

    /// <summary>不用金字塔（levels=1）的平移定位，以及非矩形掩码：掩码外像素被场景改写也不影响结果。</summary>
    [TestMethod]
    [DataRow(EOpenCvPyramidTemplateKind.Ncc)]
    [DataRow(EOpenCvPyramidTemplateKind.Shape)]
    public async Task SingleLevel_Translation_WithEllipticMask(EOpenCvPyramidTemplateKind kind)
    {
        using var source = Label(320, 240, 8);
        var definition = Definition(320, 240, 90, 70, 140, 100);
        var runs = new List<RegionRun>();
        for (int y = 70; y < 170; y++)
        {
            double dy = (y + .5 - 120) / 50, half = dy * dy >= 1 ? 0 : 70 * Math.Sqrt(1 - dy * dy);
            if (half >= 1) runs.Add(new RegionRun(y, (int)Math.Ceiling(160 - half), (int)Math.Floor(160 + half)));
        }
        var factory = new OpenCvPyramidTemplateFactory(kind);
        var build = await new OpenCvPyramidTemplateBuilder(factory.ImplementationId, kind)
            .BuildAsync(new VisionTemplateBuildRequest(source, definition, new RegionGeometry(runs.ToArray()), new Dictionary<string, string> { ["levels"] = "1" }));
        using var resource = await factory.PreparePreviewAsync(build);
        var matcher = (IPreparedVisionTemplateMatcher)resource.Instance;
        var center = new Point2d(205, 133);
        using var scene = Scene(source, definition, 400, 300, center, 0, 1, 4);
        var result = matcher.Match(scene, new PixelBounds(0, 0, 400, 300), new TemplatePoseOptions(0, 0, 1, 1, .8));
        (matcher as IDisposable)?.Dispose();
        Assert.IsTrue(result.Found, result.Summary);
        Assert.AreEqual(center.X, result.CenterX, .25); Assert.AreEqual(center.Y, result.CenterY, .25); Assert.AreEqual(0, result.AngleDegrees, 1e-9);
    }

    /// <summary>发布、清单检查与运行时准备；引擎不匹配的模型被拒绝，模型参数在制作时校验。</summary>
    [TestMethod]
    public async Task Publish_Prepare_AndValidateSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "vision-pyramid-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var source = Label(320, 240, 6);
            var definition = Definition(320, 240, 90, 70, 140, 100);
            var build = await new OpenCvPyramidTemplateBuilder("opencv.template-shape-model", EOpenCvPyramidTemplateKind.Shape)
                .BuildAsync(new VisionTemplateBuildRequest(source, definition, null, new Dictionary<string, string> { ["levels"] = "3" }));
            var path = VisionTemplateStore.Publish(root, Guid.NewGuid().ToString("N"), build);
            using var runtime = new VisionAlgorithmRuntime(VisionAlgorithmCatalog.Compose(new[] { new OpenCvVisionAlgorithmModule() }));
            using (var plan = await runtime.PrepareAsync(new[] { Request("shape", path, "opencv.template-shape-model") }, new VisionAlgorithmResourceContext(root), CancellationToken.None))
            {
                using var scene = Scene(source, definition, 400, 300, new Point2d(200, 150), 5 * Degree, 1, 2);
                var result = plan.Invoke<IPreparedVisionTemplateMatcher, TemplatePoseResult>("shape", m => m.Match(scene, new PixelBounds(0, 0, 400, 300), new TemplatePoseOptions(-Math.PI, Math.PI, 1, 1, .8)));
                Assert.IsTrue(result.Found, result.Summary); Assert.IsTrue(result.ModelContours.Count > 0);
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("ncc", path, "opencv.template-ncc-model") }, new VisionAlgorithmResourceContext(root), CancellationToken.None));
            var builder = new OpenCvPyramidTemplateBuilder("opencv.template-ncc-model", EOpenCvPyramidTemplateKind.Ncc);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => builder.BuildAsync(new VisionTemplateBuildRequest(source, definition, null, new Dictionary<string, string> { ["contrast"] = "10" })));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => builder.BuildAsync(new VisionTemplateBuildRequest(source, definition, null, new Dictionary<string, string> { ["levels"] = "9" })));
            using var flatImage = VisionImage.CopyFrom(new ImageInfo(64, 64, EPixelLayout.Gray8), Enumerable.Repeat((byte)128, 64 * 64).ToArray());
            using var flat = new ImageFrame("flat", flatImage);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => builder.BuildAsync(new VisionTemplateBuildRequest(flat, Definition(64, 64, 8, 8, 40, 40), null, new Dictionary<string, string>())));
            var catalog = VisionAlgorithmCatalog.Compose(new[] { new OpenCvVisionAlgorithmModule() });
            Assert.IsTrue(catalog.Implementations.Any(d => d.ImplementationId == "opencv.template-ncc-model" && d.Features.Contains("rotation")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static VisionAlgorithmRequest Request(string id, string path, string implementation) =>
        new VisionAlgorithmRequest(id, typeof(IPreparedVisionTemplateMatcher),
            new VisionAlgorithmSelection { ImplementationId = implementation, Settings = new Dictionary<string, string> { ["templatePath"] = path } });

    private static VisionTemplateDefinition Definition(int sw, int sh, int x, int y, int w, int h) =>
        new VisionTemplateDefinition { SourceWidth = sw, SourceHeight = sh, X = x, Y = y, Width = w, Height = h, OriginX = x + w / 2d, OriginY = y + h / 2d };

    private static async Task<Prepared> Prepare(EOpenCvPyramidTemplateKind kind, ImageFrame source, VisionTemplateDefinition definition, Dictionary<string, string>? settings = null)
    {
        var factory = new OpenCvPyramidTemplateFactory(kind);
        var build = await new OpenCvPyramidTemplateBuilder(factory.ImplementationId, kind).BuildAsync(new VisionTemplateBuildRequest(source, definition, null, settings ?? new Dictionary<string, string>()));
        var resource = await factory.PreparePreviewAsync(build);
        return new Prepared((IPreparedVisionTemplateMatcher)resource.Instance);
    }

    /// <summary>测试持有的匹配器，结束时释放原生模板数据。</summary>
    private sealed class Prepared(IPreparedVisionTemplateMatcher inner) : IPreparedVisionTemplateMatcher, IDisposable
    {
        public VisionTemplateDefinition Definition => inner.Definition;
        public string ModelIdentity => inner.ModelIdentity;
        public TemplatePoseResult Match(ImageFrame frame, PixelBounds search, TemplatePoseOptions options, RegionGeometry? region = null, CancellationToken token = default) =>
            inner.Match(frame, search, options, region, token);
        public void Dispose() => (inner as IDisposable)?.Dispose();
    }

    /// <summary>类似标签的纹理：浅底上的随机文字、色块和线条，略微模糊。</summary>
    private static ImageFrame Label(int width, int height, int seed)
    {
        var random = new Random(seed);
        using var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(200));
        for (int i = 0; i < width * height / 2500; i++)
        {
            var p = new OpenCvSharp.Point(random.Next(width), random.Next(height));
            switch (random.Next(3))
            {
                case 0: Cv2.PutText(mat, "DP" + random.Next(1000), p, HersheyFonts.HersheySimplex, .4 + random.NextDouble(), Scalar.All(random.Next(10, 90)), 1 + random.Next(2)); break;
                case 1: Cv2.Rectangle(mat, new Rect(p.X, p.Y, 5 + random.Next(30), 5 + random.Next(20)), Scalar.All(random.Next(0, 255)), -1); break;
                default: Cv2.Line(mat, p, new OpenCvSharp.Point(p.X + random.Next(-40, 40), p.Y + random.Next(-40, 40)), Scalar.All(random.Next(0, 120)), 1 + random.Next(3)); break;
            }
        }
        Cv2.GaussianBlur(mat, mat, new OpenCvSharp.Size(3, 3), .8);
        return Frame("label-" + seed, mat);
    }

    /// <summary>把样图按模板中心旋转/缩放后放到新场景的center处（边界坐标），其余为随机噪声背景。</summary>
    private static ImageFrame Scene(ImageFrame source, VisionTemplateDefinition d, int width, int height, Point2d center, double angle, double scale, int seed,
        double gain = 1, double offset = 0)
    {
        using var src = Mat(source);
        double c = scale * Math.Cos(angle), s = scale * Math.Sin(angle);
        double px = d.X + d.Width / 2d - .5, py = d.Y + d.Height / 2d - .5, qx = center.X - .5, qy = center.Y - .5;
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, c); matrix.Set(0, 1, -s); matrix.Set(0, 2, qx - (c * px - s * py));
        matrix.Set(1, 0, s); matrix.Set(1, 1, c); matrix.Set(1, 2, qy - (s * px + c * py));
        using var warped = new Mat(); Cv2.WarpAffine(src, warped, matrix, new OpenCvSharp.Size(width, height), InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.All(0));
        using var ones = new Mat(src.Size(), MatType.CV_8UC1, Scalar.All(255)); using var cover = new Mat();
        Cv2.WarpAffine(ones, cover, matrix, new OpenCvSharp.Size(width, height), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        using var background = Label(width, height, 1000 + seed);
        using var scene = Mat(background);
        warped.CopyTo(scene, cover);
        using var noise = new Mat(height, width, MatType.CV_8UC1); Cv2.Randn(noise, Scalar.All(0), Scalar.All(3));
        using var signed = new Mat(); scene.ConvertTo(signed, MatType.CV_16S, gain, offset);
        using var n16 = new Mat(); noise.ConvertTo(n16, MatType.CV_16S); Cv2.Add(signed, n16, signed);
        using var result = new Mat(); signed.ConvertTo(result, MatType.CV_8U);
        return Frame("scene-" + seed, result);
    }

    private static Mat Mat(ImageFrame frame)
    {
        var info = frame.Image.Info; var bytes = new byte[info.Width * info.Height];
        frame.Image.CopyTo(0, bytes, 0, bytes.Length);
        var mat = new Mat(info.Height, info.Width, MatType.CV_8UC1); Marshal.Copy(bytes, 0, mat.Data, bytes.Length); return mat;
    }

    private static ImageFrame Frame(string id, Mat mat)
    {
        var bytes = new byte[mat.Rows * mat.Cols];
        for (int y = 0; y < mat.Rows; y++) Marshal.Copy(mat.Ptr(y), bytes, y * mat.Cols, mat.Cols);
        using var image = VisionImage.CopyFrom(new ImageInfo(mat.Cols, mat.Rows, EPixelLayout.Gray8), bytes);
        return new ImageFrame(id, image);
    }
}
