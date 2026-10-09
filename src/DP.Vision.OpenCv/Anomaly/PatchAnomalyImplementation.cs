using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;

namespace DP.Vision.OpenCv;

/// <summary>现有Patch算法的唯一兼容Adapter；DPPA解析仅留在本实现，不再由业务缓存解释。</summary>
public sealed class PatchAnomalyImplementation : IAnomalyImplementation, IAnomalyTrainer
{
    /// <summary>原有Patch模型格式。</summary>
    public const string Format = "patch.dppa.v2";
    private readonly IPatchAnomalyDetector _detector;
    /// <summary>创建手工特征实现。</summary>
    public PatchAnomalyImplementation() : this(PatchAnomalyModel.Handcrafted, new OpenCvPatchAnomalyDetector()) { }
    /// <summary>适配已有手工/CNN算法；算法依赖由宿主拥有。</summary>
    /// <param name="id">精确特征/实现身份。</param>
    /// <param name="detector">借用旧算法。</param>
    public PatchAnomalyImplementation(string id, IPatchAnomalyDetector detector)
    { ImplementationId = id ?? throw new ArgumentNullException(nameof(id)); _detector = detector ?? throw new ArgumentNullException(nameof(detector)); }
    /// <inheritdoc/>
    public string ImplementationId { get; }
    /// <inheritdoc/>
    public string DisplayName => ImplementationId == PatchAnomalyModel.Handcrafted ? "OpenCV · 手工块模型（兼容）" : "OpenCV · CNN块模型（兼容）";
    /// <summary>将已有模型封装为新资产；旧库仍可直接保留DPPA字节。</summary>
    /// <param name="model">旧模型。</param>
    /// <param name="width">实际制作宽度。</param>
    /// <param name="height">实际制作高度。</param>
    public static AnomalyModelAsset Capture(PatchAnomalyModel model, int width, int height) => new AnomalyModelAsset(
        model.FeatureSource, Format, width, height, model.Threshold, model.TrainingImages, model.Calibration,
        new Dictionary<string, byte[]> { ["model.dppa"] = model.ToBytes() });
    /// <summary>读取旧模型；只能用于明确声明的兼容格式。</summary>
    /// <param name="asset">兼容模型资产。</param>
    public static PatchAnomalyModel ReadLegacy(AnomalyModelAsset asset)
    {
        if (asset.ModelFormat != Format) throw new InvalidDataException("非Patch资产不能作为DPPA解析。");
        var model = PatchAnomalyModel.FromBytes(asset.Read("model.dppa"));
        if (model.FeatureSource != asset.ImplementationId) throw new InvalidDataException("Patch资产来源不匹配。");
        return model;
    }
    /// <inheritdoc/>
    public ILoadedAnomalyModel Load(AnomalyModelAsset asset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (asset.ImplementationId != ImplementationId) throw new InvalidDataException("Patch实现与资产身份不匹配。");
        return new PatchAnomalyRuntime(asset, ReadLegacy(asset), _detector);
    }
    /// <inheritdoc/>
    public AnomalyModelAsset Train(IReadOnlyList<IImageSource> good, IReadOnlyList<int> sources, AnomalyTrainingOptions options, CancellationToken token = default)
    {
        if (good == null || good.Count == 0 || sources == null || sources.Count != good.Count) throw new ArgumentException("训练来源清单无效。");
        foreach (var key in options.Settings.Keys) if (key != "local_radius" && key != "memory_size" && key != "patch_size") throw new ArgumentException("未知Patch配置：" + key);
        int Read(string name, int fallback) => options.Settings.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
        int radius = Read("local_radius", 0);
        var settings = new PatchAnomalyOptions(patchSize: Read("patch_size", 8), memorySize: Read("memory_size", 6000), thresholdMargin: options.Margin, localRadius: radius == 0 ? null : (int?)radius);
        var model = _detector is IGroupedPatchAnomalyTrainer grouped ? grouped.Train(good, sources, settings, token) : _detector.Train(good, settings, token);
        if (model.FeatureSource != ImplementationId) throw new InvalidDataException("训练输出来源不匹配。");
        return Capture(model, good[0].Info.Width, good[0].Info.Height);
    }
}

/// <summary>旧Patch运行实例：原生算法仅借用，模型及其派生缓存随此实例退役。</summary>
public sealed class PatchAnomalyRuntime : ILoadedAnomalyModel
{
    private bool _disposed;
    internal PatchAnomalyRuntime(AnomalyModelAsset asset, PatchAnomalyModel model, IPatchAnomalyDetector detector)
    { Asset = asset; Model = model; Detector = detector; }
    /// <inheritdoc/>
    public AnomalyModelAsset Asset { get; }
    /// <summary>兼容模型，用于已有缺墨能力；其它实现没有此数据。</summary>
    public PatchAnomalyModel Model { get; }
    /// <summary>借用旧检测器。</summary>
    public IPatchAnomalyDetector Detector { get; }
    /// <inheritdoc/>
    public PatchAnomalyResult Inspect(IImageSource image, AnomalyDetectionOptions options, CancellationToken token = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PatchAnomalyRuntime));
        return Detector.Detect(image, Model, new PatchAnomalyOptions(patchSize: Model.PatchSize,
            threshold: options.Threshold, minimumArea: options.MinimumArea, localRadius: Model.Radius > 0 ? Model.Radius : (int?)null), token);
    }
    /// <inheritdoc/>
    public void Dispose() { _disposed = true; }
}
