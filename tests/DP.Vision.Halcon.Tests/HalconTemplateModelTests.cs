using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using DP.Plugins;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>真实原生模型的制作、重载、坐标及精确ROI测试，不使用相机。</summary>
[TestClass]
public sealed class HalconTemplateModelTests
{
    private sealed class Registration : IVisionAlgorithmRegistration
    {
        internal readonly List<VisionAlgorithmDescriptor> Items = new List<VisionAlgorithmDescriptor>();
        public void Add(VisionAlgorithmDescriptor descriptor) => Items.Add(descriptor);
    }
    /// <summary>登记元数据无需SDK；NCC不宣称支持尺度。</summary>
    [TestMethod]
    public void ModuleRegistersPairedBuildersAndHonestFeatures()
    {
        var registration = new Registration(); new HalconVisionAlgorithmModule().Register(registration);
        Assert.AreEqual(4, registration.Items.Count);
        var ncc = registration.Items.Single(d => d.ImplementationId == "halcon.template-ncc-model");
        var shape = registration.Items.Single(d => d.ImplementationId == "halcon.template-shape-model");
        Assert.IsFalse(ncc.Features.Contains("scale")); Assert.IsTrue(shape.Features.Contains("scale"));
        var factory = (HalconTemplateModelFactory)ncc.Factory;
        Assert.IsTrue(factory.ValidateSearch(Definition(), Settings(), new PixelBounds(0, 0, 128, 96), new TemplatePoseOptions(0d, 0d, 1.1, 1.1)).Count != 0);
        Assert.ThrowsExactly<ArgumentException>(() => HalconTemplateSettings.Parse(new Dictionary<string, string> { ["metric"] = "bogus" }, true));
    }
#if !HALCON_SDK
    /// <summary>无SDK构建仍登记描述，但制作明确失败。</summary>
    [TestMethod]
    public async Task MissingSdkRejectsTemplateBuild()
    {
        using var source = Frame("sample", Sample());
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => new HalconTemplateModelBuilder(false).BuildAsync(
            new VisionTemplateBuildRequest(source, Definition(), null, Settings())));
    }
#endif
    /// <summary>按实际部署包发现算法入口，不要求宿主引用HALCON类型。</summary>
    [TestMethod]
    public void PluginPackageDiscoversAlgorithmsWithSharedContracts()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "DP.Vision.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
#if NET48
        const string target = "net48";
#else
        const string target = "net8.0-windows";
#endif
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var output = Path.Combine(repo.FullName, "src", "DP.Vision.Halcon", "bin", configuration, target);
        var root = Path.Combine(AppContext.BaseDirectory, "PluginTestRuns", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        foreach (var file in Directory.GetFiles(output, "*.dll").Where(p => Path.GetFileName(p) != "System.ValueTuple.dll"
            && (!Path.GetFileName(p).StartsWith("DP.Vision", StringComparison.Ordinal) || Path.GetFileName(p) == "DP.Vision.Halcon.dll"))) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        var deps = Path.Combine(output, "DP.Vision.Halcon.deps.json");
        if (File.Exists(deps)) File.Copy(deps, Path.Combine(root, Path.GetFileName(deps)));
        var session = new PluginLoadSession();
        session.RegisterSharedAssembly(typeof(DP.Vision.Acquisition.IVisionAcquisitionDriverModule).Assembly);
        var catalog = new VisionAlgorithmModuleLoader(session).Load(root);
        Assert.AreEqual(0, catalog.Diagnostics.Count, string.Join("；", catalog.Diagnostics.Select(d => d.Reason)));
        Assert.AreEqual(4, catalog.Implementations.Count);
        Assert.AreEqual(typeof(IPreparedVisionTemplateMatcher), catalog.GetRequired("halcon.template-ncc-model").ContractType);
        Assert.IsTrue(catalog.GetRequired("halcon.template-shape-model").Factory is IVisionTemplatePreviewFactory);
        var cameras = session.Discover<DP.Vision.Acquisition.IVisionAcquisitionDriverModule>(root);
        Assert.AreEqual(1, cameras.Modules.Count, string.Join("；", cameras.Failures.Select(d => d.Reason)));
        Assert.AreEqual(0, cameras.Failures.Count);
        // 加载会话持有包到宿主结束；已加载DLL不在这里尝试删除。
    }
#if HALCON_SDK
    /// <summary>原生搜索区间覆盖邻近旋转，不使用采样引擎的步长。</summary>
    [TestMethod]
    [DataRow(93d)]
    [DataRow(-93d)]
    public async Task ShapeModelAngleRangeFindsDriftThatFixedAngleMisses(double degrees)
    {
        var original = new byte[128 * 96]; Paste(original, 12, 16, 0, 2);
        using var source = Frame("sample", original);
        var settings = new Dictionary<string, string>
        {
            ["levels"] = "1",
            ["minimumAngle"] = (-Math.PI).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ["maximumAngle"] = Math.PI.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ["angleStep"] = (Math.PI / 180).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        };
        var definition = new VisionTemplateDefinition { SourceWidth = 128, SourceHeight = 96, X = 12, Y = 16, Width = 64, Height = 64,
            OriginX = 20, OriginY = 24, ReferenceIdentity = "fixture" };
        var build = await new HalconTemplateModelBuilder(true).BuildAsync(new VisionTemplateBuildRequest(source, definition, null, settings));
        using var resource = await new HalconTemplateModelFactory(true).PreparePreviewAsync(build);
        var matcher = (IPreparedVisionTemplateMatcher)resource.Instance;
        double angle = degrees * Math.PI / 180, c = Math.Cos(angle), s = Math.Sin(angle);
        var scene = new byte[original.Length];
        for (int y = 0; y < 96; y++) for (int x = 0; x < 128; x++)
        {
            double dx = x + .5 - 76, dy = y + .5 - 48;
            double sx = 44 + c * dx + s * dy - .5, sy = 48 - s * dx + c * dy - .5;
            int left = (int)Math.Floor(sx), top = (int)Math.Floor(sy);
            if (left < 0 || left + 1 >= 128 || top < 0 || top + 1 >= 96) continue;
            double fx = sx - left, fy = sy - top;
            scene[y * 128 + x] = (byte)Math.Round((1 - fy) * ((1 - fx) * original[top * 128 + left] + fx * original[top * 128 + left + 1])
                + fy * ((1 - fx) * original[(top + 1) * 128 + left] + fx * original[(top + 1) * 128 + left + 1]));
        }
        using var frame = Frame("rotated-93", scene);
        // 单层金字塔和较高分数可明确暴露固定角度不覆盖邻近姿态；不依赖多层细化的偶然容差。
        double centerAngle = Math.Sign(degrees) * Math.PI / 2;
        var fixedAngle = matcher.Match(frame, new PixelBounds(0, 0, 128, 96), new TemplatePoseOptions(centerAngle, centerAngle, 1d, 1d, .95));
        var actualAngle = matcher.Match(frame, new PixelBounds(0, 0, 128, 96), new TemplatePoseOptions(angle, angle, 1d, 1d, .95));
        Assert.IsFalse(fixedAngle.Found);
        Assert.IsTrue(actualAngle.Found);
        Assert.AreEqual(angle, actualAngle.Transform!.AngleRadians, .01);
        var range = matcher.Match(frame, new PixelBounds(0, 0, 128, 96),
            new TemplatePoseOptions(centerAngle - 5 * Math.PI / 180, centerAngle + 5 * Math.PI / 180, .9, 1.1, .95,
                angleStepRadians: Math.PI, scaleStep: 1));
        Assert.IsTrue(range.Found, "应由HALCON原生区间找到±93°，不能只试区间端点或按OpenCV步长采样。");
        Assert.AreEqual(angle, range.Transform!.AngleRadians, .01);
    }

    /// <summary>界面默认自动金字塔制作后能在制作区域自检。</summary>
    [TestMethod]
    public async Task DefaultBuildParametersSupportRoiSelfTest()
    {
        using var source = Frame("sample", Sample());
        foreach (bool shape in new[] { false, true })
        {
            var build = await new HalconTemplateModelBuilder(shape).BuildAsync(new VisionTemplateBuildRequest(source, Definition(), null, new Dictionary<string, string>()));
            using var resource = await new HalconTemplateModelFactory(shape).PreparePreviewAsync(build);
            var result = ((IPreparedVisionTemplateMatcher)resource.Instance).Match(source, new PixelBounds(12, 20, 32, 32), new TemplatePoseOptions(0d, 0d, 1d, 1d, .9));
            Assert.IsTrue(result.Found, shape ? "形状" : "NCC");
            Assert.AreEqual(28, result.Transform!.Center.X, .1); Assert.AreEqual(36, result.Transform.Center.Y, .1);
        }
    }

    /// <summary>原生版本按内容共享并冻结，运行时不重新读取磁盘，损坏资源在准备时失败。</summary>
    [TestMethod]
    public async Task PublishedHalconModelSharesFrozenResourceAndFailsBeforeExecutionWhenBroken()
    {
        var root = Path.Combine(Path.GetTempPath(), "halcon-template-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var source = Frame("sample", Sample());
            var build = await new HalconTemplateModelBuilder(false).BuildAsync(new VisionTemplateBuildRequest(source, Definition(), null, Settings()));
            var reference = VisionTemplateStore.Publish(root, Guid.NewGuid().ToString("N"), build);
            using var runtime = new VisionAlgorithmRuntime(VisionAlgorithmCatalog.Compose(new[] { new HalconVisionAlgorithmModule() }));
            VisionAlgorithmRequest Request(string key) => new VisionAlgorithmRequest(key, typeof(IPreparedVisionTemplateMatcher), new VisionAlgorithmSelection
                { ImplementationId = build.ImplementationId, Settings = new Dictionary<string, string> { ["templatePath"] = reference } });
            using var plan = await runtime.PrepareAsync(new[] { Request("a"), Request("b") }, new VisionAlgorithmResourceContext(root), CancellationToken.None);
            Assert.AreSame(plan.Invoke<IPreparedVisionTemplateMatcher, object>("a", m => m), plan.Invoke<IPreparedVisionTemplateMatcher, object>("b", m => m));
            var path = Path.Combine(Path.GetDirectoryName(Path.Combine(root, reference))!, HalconTemplateModelFactory.ModelFile);
            File.WriteAllBytes(path, new byte[] { 0 });
            Assert.IsTrue(plan.Invoke<IPreparedVisionTemplateMatcher, bool>("a", m => m.Match(source, new PixelBounds(12, 20, 32, 32), new TemplatePoseOptions(0d, 0d, 1d, 1d, .95)).Found));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.PrepareAsync(new[] { Request("broken") }, new VisionAlgorithmResourceContext(root), CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>HALCON NCC旋转也必须按仿射像素原点转换。</summary>
    [TestMethod]
    public async Task NccRotationUsesCommonCoordinates()
    {
        using var source = Frame("sample", Sample());
        var settings = Settings(); settings["minimumAngle"] = "0"; settings["maximumAngle"] = (100 * Math.PI / 180).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var build = await new HalconTemplateModelBuilder(false).BuildAsync(new VisionTemplateBuildRequest(source, Definition(), null, settings));
        using var resource = await new HalconTemplateModelFactory(false).PreparePreviewAsync(build);
        var scene = new byte[128 * 96]; Paste(scene, 60, 30, 1, 1); using var frame = Frame("rotated", scene);
        var result = ((IPreparedVisionTemplateMatcher)resource.Instance).Match(frame, new PixelBounds(0, 0, 128, 96), new TemplatePoseOptions(80 * Math.PI / 180, 100 * Math.PI / 180, 1d, 1d, .9));
        Assert.IsTrue(result.Found); Assert.AreEqual(76, result.Transform!.Center.X, .01); Assert.AreEqual(46, result.Transform.Center.Y, .01);
    }

    /// <summary>ROI裁剪、非对称掩码及业务原点在模型重载后仍然一致。</summary>
    [TestMethod]
    public async Task NccModelRoundTripPreservesRoiAndReferenceOrigin()
    {
        using var source = Frame("sample", Sample());
        var mask = new RegionGeometry(Enumerable.Range(20, 32).Select(y => new RegionRun(y, 16, 44)));
        var build = await new HalconTemplateModelBuilder(false).BuildAsync(new VisionTemplateBuildRequest(source, Definition(), mask, Settings()));
        Assert.AreEqual("halcon.ncc.v1", build.Format);
        using var resource = await new HalconTemplateModelFactory(false).PreparePreviewAsync(build);
        var matcher = (IPreparedVisionTemplateMatcher)resource.Instance;
        var scene = new byte[128 * 96]; Paste(scene, 70, 40, 0, 1);
        using var frame = Frame("scene", scene);
        var result = matcher.Match(frame, new PixelBounds(0, 0, 128, 96), new TemplatePoseOptions(0d, 0d, 1d, 1d, .95));
        Assert.IsTrue(result.Found); Assert.AreEqual(86, result.Transform!.Center.X, .01); Assert.AreEqual(56, result.Transform.Center.Y, .01);
        var origin = result.Transform.ToImage(new Coordinate2D(Definition().OriginX - 12, Definition().OriginY - 20));
        Assert.AreEqual(78, origin.X, .01); Assert.AreEqual(44, origin.Y, .01);
        // 改动ROI外样图不应改变原生模型：在灰度匹配中它没有参与制作。
        using var decodedMask = VisionTemplateSource.Decode(build.Files.Single(f => f.Name == "source/mask.bin").Content);
        var bytes = new byte[128 * 96]; decodedMask.CopyTo(0, bytes, 0, bytes.Length);
        Assert.AreEqual(0, bytes[20 * 128 + 12]); Assert.AreEqual(255, bytes[20 * 128 + 16]);
    }
    /// <summary>原点落在ROI内但有效模板跨孔洞时拒绝；不能漏掉次优合法位置。</summary>
    [TestMethod]
    public async Task SearchMaskRejectsCrossingTemplateButFindsOtherValidCandidate()
    {
        using var source = Frame("sample", Sample());
        var build = await new HalconTemplateModelBuilder(false).BuildAsync(new VisionTemplateBuildRequest(source, Definition(), null, Settings()));
        using var resource = await new HalconTemplateModelFactory(false).PreparePreviewAsync(build);
        var matcher = (IPreparedVisionTemplateMatcher)resource.Instance;
        var scene = new byte[128 * 96]; Paste(scene, 12, 20, 0, 1); Paste(scene, 70, 40, 0, 1);
        using var frame = Frame("scene", scene);
        var full = new RegionGeometry(Enumerable.Range(0, 96).Select(y => new RegionRun(y, 0, 128)));
        var hole = new RegionGeometry(Enumerable.Range(20, 4).Select(y => new RegionRun(y, 12, 16)));
        var mask = full.Subtract(hole);
        var result = matcher.Match(frame, new PixelBounds(0, 0, 128, 96), new TemplatePoseOptions(0d, 0d, 1d, 1d, .95), mask);
        Assert.IsTrue(result.Found); Assert.AreEqual(86, result.Transform!.Center.X, .01);
        var rejected = matcher.Match(frame, new PixelBounds(12, 20, 32, 32), new TemplatePoseOptions(0d, 0d, 1d, 1d, .95), mask);
        Assert.IsFalse(rejected.Found);
    }
    /// <summary>原生角度正负与统一像素边界坐标不发生漂移。</summary>
    [TestMethod]
    public async Task ShapeModelRotationAndScaleUseCommonCoordinates()
    {
        using var source = Frame("sample", Sample());
        var settings = Settings(); settings["minimumAngle"] = "0"; settings["maximumAngle"] = (Math.PI / 2).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        settings["minimumScale"] = "1"; settings["maximumScale"] = "2.2"; settings["scaleStep"] = "0.1";
        var build = await new HalconTemplateModelBuilder(true).BuildAsync(new VisionTemplateBuildRequest(source, Definition(), null, settings));
        using var resource = await new HalconTemplateModelFactory(true).PreparePreviewAsync(build);
        var matcher = (IPreparedVisionTemplateMatcher)resource.Instance;
        var scene = new byte[128 * 96]; Paste(scene, 60, 30, 1, 1);
        using var rotated = Frame("rotated", scene);
        var result = matcher.Match(rotated, new PixelBounds(0, 0, 128, 96), new TemplatePoseOptions(Math.PI / 2, Math.PI / 2, 1d, 1d, .7));
        Assert.IsTrue(result.Found); Assert.AreEqual(Math.PI / 2, result.Transform!.AngleRadians, .011);
        Assert.AreEqual(76, result.Transform.Center.X, .1); Assert.AreEqual(46, result.Transform.Center.Y, .1);
        var transformed = result.Transform.ToImage(new Coordinate2D(8, 4));
        Assert.AreEqual(88, transformed.X, .2); Assert.AreEqual(38, transformed.Y, .2);
        var scaledBytes = new byte[128 * 96]; Paste(scaledBytes, 40, 20, 0, 2);
        using var scaled = Frame("scaled", scaledBytes);
        var scaleResult = matcher.Match(scaled, new PixelBounds(0, 0, 128, 96), new TemplatePoseOptions(0d, 0d, 1.8, 2.2, .7, scaleStep: 1));
        Assert.IsTrue(scaleResult.Found); Assert.AreEqual(2, scaleResult.Transform!.Scale, .02);
        Assert.AreEqual(72, scaleResult.Transform.Center.X, 1); Assert.AreEqual(52, scaleResult.Transform.Center.Y, 1);
    }
    /// <summary>取消不产生可执行资源；不同引擎文件不能误加载。</summary>
    [TestMethod]
    public async Task RejectsCancellationAndForeignModel()
    {
        using var source = Frame("sample", Sample());
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new HalconTemplateModelBuilder(false).BuildAsync(
            new VisionTemplateBuildRequest(source, Definition(), null, Settings()), new CancellationToken(true)));
        var build = await new HalconTemplateModelBuilder(false).BuildAsync(new VisionTemplateBuildRequest(source, Definition(), null, Settings()));
        await Assert.ThrowsExactlyAsync<System.IO.InvalidDataException>(() => new HalconTemplateModelFactory(true).PreparePreviewAsync(build));
    }
#endif
    private static Dictionary<string, string> Settings() => new Dictionary<string, string> { ["levels"] = "1" };
    private static VisionTemplateDefinition Definition() => new VisionTemplateDefinition { SourceWidth = 128, SourceHeight = 96, X = 12, Y = 20, Width = 32, Height = 32, OriginX = 20, OriginY = 24, ReferenceIdentity = "fixture" };
    private static ImageFrame Frame(string id, byte[] bytes)
    { using var image = VisionImage.CopyFrom(new ImageInfo(128, 96, EPixelLayout.Gray8), bytes); return new ImageFrame(id, image); }
    private static byte[] Sample() { var bytes = new byte[128 * 96]; Paste(bytes, 12, 20, 0, 1); return bytes; }
    private static void Paste(byte[] bytes, int left, int top, int rotation, int scale)
    {
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            byte value = (byte)((x >= 5 && x <= 10 && y >= 4 && y <= 27 || y >= 22 && y <= 27 && x >= 5 && x <= 25) ? 230
                : (x - 23) * (x - 23) + (y - 9) * (y - 9) < 20 ? 180 : 30);
            int xx = rotation == 0 ? x : 31 - y, yy = rotation == 0 ? y : x;
            for (int dy = 0; dy < scale; dy++) for (int dx = 0; dx < scale; dx++) bytes[(top + yy * scale + dy) * 128 + left + xx * scale + dx] = value;
        }
    }
}
