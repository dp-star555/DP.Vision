using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;

namespace DP.Vision.Halcon;

/// <summary>HALCON算法入口；发现阶段仅登记元数据，不调用SDK、许可或模型。</summary>
public sealed class HalconVisionAlgorithmModule : IVisionAlgorithmModule
{
    /// <inheritdoc/>
    public string ExtensionId => "dp.vision.halcon.algorithms";
    /// <inheritdoc/>
    public void Register(IVisionAlgorithmRegistration registrations)
    {
        if (registrations == null) throw new ArgumentNullException(nameof(registrations));
        foreach (var method in new[] { EHalconAnomalyMethod.AnomalyDetection, EHalconAnomalyMethod.Variation })
        {
            var implementation = new HalconAnomalyImplementation(method);
            registrations.Add(new VisionAlgorithmDescriptor(implementation.ImplementationId, "HALCON", "1", implementation,
                features: new[] { "cpu", "normal-only-training", "native-model-asset", "pixel-anomaly-map" }));
        }
        foreach (bool shape in new[] { false, true })
        {
            var factory = new HalconTemplateModelFactory(shape);
            registrations.Add(new VisionAlgorithmDescriptor(factory.ImplementationId, "HALCON", "1", factory,
                features: shape ? new[] { "translation", "rotation", "scale", "masked-template" } : new[] { "translation", "rotation", "masked-template" },
                parameters: new[] { new VisionAlgorithmParameter("templatePath", "模板资源", typeof(string), description: "HALCON原生模型版本的manifest.json。", isFilePath: true) }));
            registrations.Add(new VisionAlgorithmDescriptor(factory.BuilderImplementationId, "HALCON", "1",
                VisionAlgorithmFactory<IVisionTemplateBuilder>.Stateless(() => new HalconTemplateModelBuilder(shape), HalconTemplateNative.CheckEnvironment)));
        }
    }
}

/// <summary>HALCON灰度NCC或尺度形状模型的制作工厂。</summary>
public sealed class HalconTemplateModelBuilder : IVisionTemplateBuilder
{
    private readonly bool _shape;
    /// <summary>选择尺度形状或NCC。</summary>
    public HalconTemplateModelBuilder(bool shape) => _shape = shape;
    /// <inheritdoc/>
    public Task<VisionTemplateBuild> BuildAsync(VisionTemplateBuildRequest request, CancellationToken token = default)
        => Task.FromResult(HalconTemplateNative.Build(request, _shape, token));
}

/// <summary>引擎专用原生资源和预览；不在登记或属性读取时加载模型。</summary>
public sealed class HalconTemplateModelFactory : IVisionAlgorithmFactory, IVisionAlgorithmConfigurationValidator,
    IVisionTemplateFactoryDescription, IVisionTemplatePreviewFactory, IVisionAlgorithmResourceInspector, IVisionTemplateSearchValidator
{
    private readonly bool _shape;
    /// <summary>选择尺度形状或NCC。</summary>
    public HalconTemplateModelFactory(bool shape) => _shape = shape;
    /// <summary>匹配实现身份。</summary>
    public string ImplementationId => _shape ? "halcon.template-shape-model" : "halcon.template-ncc-model";
    internal string Format => _shape ? "halcon.scaled-shape.v1" : "halcon.ncc.v1";
    internal const string ModelFile = "variants/halcon/model.bin";
    /// <inheritdoc/>
    public string BuilderImplementationId => ImplementationId + ".build";
    /// <inheritdoc/>
    public string MethodDisplayName => _shape ? "尺度形状匹配" : "灰度NCC（不缩放）";
    /// <inheritdoc/>
    public Type ContractType => typeof(IPreparedVisionTemplateMatcher);
    /// <inheritdoc/>
    public IReadOnlyList<VisionAlgorithmParameter> BuildParameters
    {
        get
        {
            var list = new List<VisionAlgorithmParameter>
            {
                new VisionAlgorithmParameter("levels", "金字塔层数", typeof(int), "0", "0自动；1至6显式指定。", 0, 6),
                new VisionAlgorithmParameter("minimumAngle", "模型最小角度", typeof(double), "-0.35", "相对制作样图的顺时针旋转下限；修改后需重新生成模型。", -Math.PI, Math.PI) { DisplayRadiansAsDegrees = true },
                new VisionAlgorithmParameter("maximumAngle", "模型最大角度", typeof(double), "0.35", "相对制作样图的顺时针旋转上限；匹配节点的搜索区间须包含于该范围。", -Math.PI, Math.PI) { DisplayRadiansAsDegrees = true },
                new VisionAlgorithmParameter("angleStep", "模型角度步长", typeof(double), "0.01", "制作时的模型角度采样间隔；运行搜索区间由节点另行设置。", .001, .2) { DisplayRadiansAsDegrees = true },
                new VisionAlgorithmParameter("metric", "极性方式", typeof(string), "use_polarity", "use_polarity或ignore_global_polarity。")
            };
            if (_shape) list.AddRange(new[]
            {
                new VisionAlgorithmParameter("minimumScale", "模型最小尺度", typeof(double), "0.9", minimum: .1, maximum: 10),
                new VisionAlgorithmParameter("maximumScale", "模型最大尺度", typeof(double), "1.1", minimum: .1, maximum: 10),
                new VisionAlgorithmParameter("scaleStep", "模型尺度步长", typeof(double), "0.01", minimum: .001, maximum: .2),
                new VisionAlgorithmParameter("contrast", "制作对比度", typeof(int), "30", minimum: 1, maximum: 255),
                new VisionAlgorithmParameter("minimumContrast", "搜索最小对比度", typeof(int), "10", minimum: 1, maximum: 255)
            });
            return list;
        }
    }
    /// <inheritdoc/>
    public IReadOnlyList<VisionAlgorithmDependency> GetDependencies(VisionAlgorithmConfiguration configuration) => Array.Empty<VisionAlgorithmDependency>();
    /// <inheritdoc/>
    public IReadOnlyList<string> ValidateConfiguration(VisionAlgorithmConfiguration configuration)
        => VisionAlgorithmConfigurationRules.ValidateVersionOne(configuration, new[] { "templatePath" }, "templatePath");
    private void Check(VisionTemplateManifest manifest)
    {
        if (manifest.ImplementationId != ImplementationId || manifest.ModelFormat != Format)
            throw new InvalidDataException("模板与HALCON所选方式或格式不兼容，请使用相应引擎重新制作。");
        _ = HalconTemplateSettings.Parse(manifest.BuildSettings, _shape);
    }
    /// <inheritdoc/>
    public IReadOnlyList<string> InspectResources(VisionAlgorithmConfiguration configuration)
    {
        if (!configuration.Settings.TryGetValue("templatePath", out var path) || string.IsNullOrWhiteSpace(path)) return new[] { "缺少模板资源引用。" };
        Check(VisionTemplateStore.Inspect(path)); return VisionTemplateStore.InspectFiles(path);
    }
    /// <inheritdoc/>
    public IReadOnlyList<string> ValidateSearch(VisionTemplateDefinition definition, IReadOnlyDictionary<string, string> settings, PixelBounds search, TemplatePoseOptions options)
    {
        try { definition.Validate(); HalconTemplateSettings.Parse(settings, _shape).ValidateSearch(options, _shape); return Array.Empty<string>(); }
        catch (ArgumentException ex) { return new[] { ex.Message }; }
    }
    /// <inheritdoc/>
    public Task<VisionAlgorithmResource> PreparePreviewAsync(VisionTemplateBuild build, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var snapshot = VisionTemplateStore.CaptureBuild(build); Check(snapshot.Manifest);
        return Task.FromResult(new VisionAlgorithmResource(HalconTemplateNative.Load(snapshot, _shape, token)));
    }
    /// <inheritdoc/>
    public Task<VisionAlgorithmActivation> PrepareAsync(VisionAlgorithmConfiguration configuration, IReadOnlyDictionary<string, object> dependencies, CancellationToken token)
    {
        var issues = ValidateConfiguration(configuration); if (issues.Count != 0) throw new ArgumentException(string.Join("；", issues));
        var snapshot = VisionTemplateStore.Capture(configuration.Settings["templatePath"], token); Check(snapshot.Manifest);
        // 运行时PrepareAsync会等待缓存的CreateAsync完成，模型/许可失败仍在执行前暴露。
        // 原生解码放在缓存创建处，同一资源的多个节点不各自重复加载、释放一次模型。
        return Task.FromResult(new VisionAlgorithmActivation(snapshot.Identity, EVisionAlgorithmSharing.SharedSerial,
            cancellation => Task.FromResult(new VisionAlgorithmResource(HalconTemplateNative.Load(snapshot, _shape, cancellation)))));
    }
}

internal sealed class HalconTemplateSettings
{
    internal int Levels, Contrast, MinimumContrast;
    internal double MinimumAngle, MaximumAngle, AngleStep, MinimumScale, MaximumScale, ScaleStep;
    internal string Metric = "use_polarity";
    internal static HalconTemplateSettings Parse(IReadOnlyDictionary<string, string> values, bool shape)
    {
        var allowed = new HalconTemplateModelFactory(shape).BuildParameters.Select(p => p.Id).ToArray();
        if (values.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("未知的HALCON制作参数。");
        double Read(string key, double fallback)
        {
            if (!values.TryGetValue(key, out var text)) return fallback;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new ArgumentException("HALCON制作参数不是有效数字：" + key);
            return value;
        }
        double levels = Read("levels", 0), contrast = Read("contrast", 30), minContrast = Read("minimumContrast", 10);
        var result = new HalconTemplateSettings { Levels = (int)levels, Contrast = (int)contrast, MinimumContrast = (int)minContrast,
            MinimumAngle = Read("minimumAngle", -.35), MaximumAngle = Read("maximumAngle", .35), AngleStep = Read("angleStep", .01),
            MinimumScale = shape ? Read("minimumScale", .9) : 1, MaximumScale = shape ? Read("maximumScale", 1.1) : 1, ScaleStep = Read("scaleStep", .01),
            Metric = values.TryGetValue("metric", out var metric) ? metric : "use_polarity" };
        if (new[] { levels, contrast, minContrast, result.MinimumAngle, result.MaximumAngle, result.AngleStep, result.MinimumScale, result.MaximumScale, result.ScaleStep }.Any(v => double.IsNaN(v) || double.IsInfinity(v))
            || levels != result.Levels || levels < 0 || levels > 6 || contrast != result.Contrast || minContrast != result.MinimumContrast
            || contrast < 1 || contrast > 255 || minContrast < 1 || minContrast > 255
            || result.MinimumAngle < -Math.PI || result.MaximumAngle > Math.PI || result.MinimumAngle > result.MaximumAngle
            || result.AngleStep < .001 || result.AngleStep > .2 || result.ScaleStep < .001 || result.ScaleStep > .2
            || result.MinimumScale < .1 || result.MaximumScale > 10 || result.MinimumScale > result.MaximumScale
            || !new[] { "use_polarity", "ignore_global_polarity" }.Contains(result.Metric)) throw new ArgumentException("HALCON模型制作参数或极性方式无效。");
        // 限制原生模型离散化规模；扩大范围时应同时调整采样步长。
        if ((1 + (result.MaximumAngle - result.MinimumAngle) / result.AngleStep) * (1 + (result.MaximumScale - result.MinimumScale) / result.ScaleStep) > 100000)
            throw new ArgumentException("HALCON模型姿态采样过多，请缩小范围或增大步长。");
        return result;
    }
    internal void ValidateSearch(TemplatePoseOptions options, bool shape)
    {
        foreach (var interval in options.AngleIntervals())
            if (interval.Minimum < MinimumAngle - 1e-9 || interval.Maximum > MaximumAngle + 1e-9)
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "搜索角度超出HALCON模型制作范围：搜索 [{0:G6}°, {1:G6}°]，已保存模型范围 [{2:G6}°, {3:G6}°]。请调整制作范围，重新生成并应用模板；仅修改搜索范围不会扩大模型范围。",
                    interval.Minimum * 180 / Math.PI, interval.Maximum * 180 / Math.PI, MinimumAngle * 180 / Math.PI, MaximumAngle * 180 / Math.PI));
        if (!shape && (Math.Abs(options.MinimumScale - 1) > 1e-9 || Math.Abs(options.MaximumScale - 1) > 1e-9))
            throw new ArgumentException("HALCON NCC模型不支持尺度变化，请将尺度范围设为1至1，或选择形状匹配。");
        if (options.MinimumScale < MinimumScale - 1e-9 || options.MaximumScale > MaximumScale + 1e-9)
            throw new ArgumentException("搜索尺度超出HALCON模型制作范围。");
    }
}
