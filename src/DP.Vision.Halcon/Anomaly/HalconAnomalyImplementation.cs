using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;

namespace DP.Vision.Halcon;

/// <summary>HALCON支持的异常模型方式；不将传统变差模型冒充深度学习。</summary>
public enum EHalconAnomalyMethod
{
    /// <summary>HALCON原生深度Anomaly Detection，仅正常图训练。</summary>
    AnomalyDetection = 0,
    /// <summary>位置相关的HALCON传统变差模型。</summary>
    Variation = 1,
}

/// <summary>HALCON厂商Adapter：登记不加载SDK/许可；原生模型由可释放运行实例拥有。</summary>
public sealed class HalconAnomalyImplementation : IAnomalyImplementation, IAnomalyTrainer, IVisionAlgorithmFactory
{
    /// <summary>深度异常检测稳定身份。</summary>
    public const string DeepId = "halcon.anomaly-detection";
    /// <summary>传统变差模型稳定身份。</summary>
    public const string VariationId = "halcon.variation";
    internal readonly EHalconAnomalyMethod Method;
    internal readonly string? InitialModel;
    internal readonly int Epochs;
    /// <summary>登记实现及训练依赖；CPU训练/推理，不隐式改用GPU。</summary>
    /// <param name="method">明确算法。</param>
    /// <param name="initialModel">深度训练的初始HALCON模型路径；null使用HALCONROOT/dl/initial_dl_anomaly_medium.hdl。推理不需要此初始模型。</param>
    /// <param name="maximumEpochs">深度训练最大轮数，1–1000。</param>
    public HalconAnomalyImplementation(EHalconAnomalyMethod method = EHalconAnomalyMethod.AnomalyDetection,
        string? initialModel = null, int maximumEpochs = 30)
    {
        if (!Enum.IsDefined(typeof(EHalconAnomalyMethod), method) || maximumEpochs < 1 || maximumEpochs > 1000)
            throw new ArgumentOutOfRangeException(nameof(method));
        Method = method; InitialModel = initialModel; Epochs = maximumEpochs;
    }
    /// <inheritdoc/>
    public Type ContractType => typeof(IAnomalyImplementation);
    /// <inheritdoc/>
    public IReadOnlyList<VisionAlgorithmDependency> GetDependencies(VisionAlgorithmConfiguration configuration) => Array.Empty<VisionAlgorithmDependency>();
    /// <inheritdoc/>
    public Task<VisionAlgorithmActivation> PrepareAsync(VisionAlgorithmConfiguration configuration,
        IReadOnlyDictionary<string, object> dependencies, CancellationToken cancellationToken)
        => VisionAlgorithmFactory<IAnomalyImplementation>.Stateless(() => this).PrepareAsync(configuration, dependencies, cancellationToken);
    /// <inheritdoc/>
    public string ImplementationId => Method == EHalconAnomalyMethod.Variation ? VariationId : DeepId;
    /// <inheritdoc/>
    public string DisplayName => Method == EHalconAnomalyMethod.Variation ? "HALCON · 变差模型" : "HALCON · 深度异常检测（CPU）";
    /// <inheritdoc/>
    public ILoadedAnomalyModel Load(AnomalyModelAsset asset, CancellationToken token = default)
    {
        if (asset == null || asset.ImplementationId != ImplementationId || asset.ModelFormat != (ImplementationId + ".v1"))
            throw new ArgumentException("HALCON实现、算法与资产格式不匹配。", nameof(asset));
        token.ThrowIfCancellationRequested(); return HalconAnomalyNative.Load(asset, Method, token);
    }
    /// <inheritdoc/>
    public AnomalyModelAsset Train(IReadOnlyList<IImageSource> good, IReadOnlyList<int> sources,
        AnomalyTrainingOptions options, CancellationToken token = default)
    {
        if (good == null || good.Count < 3 || sources == null || sources.Count != good.Count || options == null)
            throw new ArgumentException("HALCON训练需要至少3个独立正常来源（含独立标定来源）。");
        if (Method == EHalconAnomalyMethod.Variation && !options.PositionDependent)
            throw new NotSupportedException("HALCON变差模型只支持固定位置内容；内容可变请显式选择其他实现。");
        // 原生算法不接受伪Patch参数；只接受本实现声明的公共标定余量。
        if (options.Settings.Count != 0) throw new ArgumentException("HALCON原生算法不支持这些配置键，请使用明确的HALCON实现设置。");
        token.ThrowIfCancellationRequested(); return HalconAnomalyNative.Train(this, good, sources, options, token);
    }
}
