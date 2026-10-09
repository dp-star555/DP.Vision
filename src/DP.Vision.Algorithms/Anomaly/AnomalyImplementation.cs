using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>异常推理实现；登记不加载模型/许可，原生对象只由返回的实例持有。</summary>
[VisionCapability("anomaly.model", "异常检测", "厂商模型资产运行")]
public interface IAnomalyImplementation
{
    /// <summary>稳定实现身份。</summary>
    string ImplementationId { get; }
    /// <summary>工作台显示名称。</summary>
    string DisplayName { get; }
    /// <summary>验证资产并加载一个可释放实例。CPU密集调用由宿主在后台执行，不在UI线程调用。</summary>
    /// <param name="asset">已捕获不可变资产。</param>
    /// <param name="token">取消；原生返回后再次检查，不销毁在途句柄。</param>
    ILoadedAnomalyModel Load(AnomalyModelAsset asset, CancellationToken token = default);
}

/// <summary>可选的良品训练能力；纯推理实现无需假实现训练。</summary>
public interface IAnomalyTrainer
{
    /// <summary>使用明确来源和参数训练完整模型资产。</summary>
    /// <param name="good">借用良品裁图，调用者拥有。</param>
    /// <param name="sources">独立来源标识；同原图重复字符须相同。</param>
    /// <param name="options">共用标定规则及由实现校验的参数。</param>
    /// <param name="token">协作式取消。</param>
    AnomalyModelAsset Train(IReadOnlyList<IImageSource> good, IReadOnlyList<int> sources,
        AnomalyTrainingOptions options, CancellationToken token = default);
}

/// <summary>加载后的原生模型：宿主持有生命周期，调用者借用，单实例默认串行执行。</summary>
public interface ILoadedAnomalyModel : IDisposable
{
    /// <summary>实际资产，不能替换成另一实现/版本。</summary>
    AnomalyModelAsset Asset { get; }
    /// <summary>在输入裁图上返回局部坐标证据；结果持有独立图像租约。</summary>
    /// <param name="image">借用输入图像，输入规格必须与资产匹配。</param>
    /// <param name="options">本次明确检测阈值及最小面积。</param>
    /// <param name="token">协作式取消。</param>
    PatchAnomalyResult Inspect(IImageSource image, AnomalyDetectionOptions options, CancellationToken token = default);
}

/// <summary>与厂商无关的检测判定参数；得分单位由资产定义，不跨模型继承。</summary>
public sealed class AnomalyDetectionOptions
{
    /// <summary>创建检测参数。</summary>
    /// <param name="threshold">覆盖阈值，null使用资产标定阈值。</param>
    /// <param name="minimumArea">输入坐标像素中的最小异常面积。</param>
    public AnomalyDetectionOptions(double? threshold = null, int minimumArea = 6)
    {
        if (threshold is double t && (!(t > 0) || double.IsInfinity(t)) || minimumArea < 1 || minimumArea > 100000)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        Threshold = threshold; MinimumArea = minimumArea;
    }
    /// <summary>本次明确阈值。</summary>
    public double? Threshold { get; }
    /// <summary>最小异常区域面积。</summary>
    public int MinimumArea { get; }
}

/// <summary>训练参数：独立的共同标定余量与实现专属配置；不强迫HALCON使用Patch参数。</summary>
public sealed class AnomalyTrainingOptions
{
    /// <summary>创建配置，不修改调用方字典。</summary>
    /// <param name="margin">相对良品标定分数的余量。</param>
    /// <param name="settings">实现专属且必须校验的设置。</param>
    /// <param name="positionDependent">要求固定位置/几何；false请求内容可变的局部异常能力，厂商不得忽略。</param>
    public AnomalyTrainingOptions(double margin = 1.5, IReadOnlyDictionary<string, string>? settings = null, bool positionDependent = true)
    {
        if (double.IsNaN(margin) || margin < 1 || margin > 5) throw new ArgumentOutOfRangeException(nameof(margin));
        Margin = margin; PositionDependent = positionDependent;
        Settings = new ReadOnlyDictionary<string, string>((settings ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }
    /// <summary>标定余量。</summary>
    public double Margin { get; }
    /// <summary>请求的固定位置/局部内容能力。</summary>
    public bool PositionDependent { get; }
    /// <summary>专属设置。</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }
}

/// <summary>不可变实现目录：只登记工厂，不创建原生对象；同身份重复或未知实现明确拒绝。</summary>
public sealed class AnomalyImplementationRegistry
{
    private readonly IReadOnlyDictionary<string, IAnomalyImplementation> _implementations;
    /// <summary>创建已冻结目录。</summary>
    /// <param name="implementations">实现工厂，由宿主负责其依赖所有权。</param>
    public AnomalyImplementationRegistry(IEnumerable<IAnomalyImplementation> implementations)
    {
        var entries = new Dictionary<string, IAnomalyImplementation>(StringComparer.Ordinal);
        foreach (var item in implementations ?? throw new ArgumentNullException(nameof(implementations)))
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ImplementationId) || entries.ContainsKey(item.ImplementationId))
                throw new ArgumentException("异常实现为空或身份重复。", nameof(implementations));
            entries.Add(item.ImplementationId, item);
        }
        _implementations = new ReadOnlyDictionary<string, IAnomalyImplementation>(entries);
        Implementations = Array.AsReadOnly(entries.Values.OrderBy(e => e.ImplementationId, StringComparer.Ordinal).ToArray());
    }
    /// <summary>已登记实现，用于能力选择。</summary>
    public IReadOnlyList<IAnomalyImplementation> Implementations { get; }
    /// <summary>查找明确实现，不回退。</summary>
    /// <param name="id">完整实现身份。</param>
    public IAnomalyImplementation Resolve(string id) => _implementations.TryGetValue(id, out var value) ? value
        : throw new NotSupportedException("未登记异常检测实现：" + id);
    /// <summary>非异常式能力检查。</summary>
    /// <param name="id">实现身份。</param>
    public bool Contains(string id) => _implementations.ContainsKey(id);
}
