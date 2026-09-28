using System.Collections.Generic;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>
/// 按来源分组标定阈值的局部块异常训练：留一法每次排除同一来源（例如同一张标签图、同一次印刷）的全部样本。
/// 同一来源的重复样本几乎一样，按单个样本留一时它仍被自己的“孪生”样本解释，阈值会被压得过紧。
/// </summary>
public interface IGroupedPatchAnomalyTrainer
{
    /// <summary>用良品训练模型，阈值按来源留一标定；来源少于2个时按单个样本留一（与普通训练相同）。</summary>
    /// <param name = "good">良品裁图，至少1张。</param>
    /// <param name = "sources">与<paramref name = "good"/>一一对应的来源编号，相同编号为同一来源。</param>
    /// <param name = "options">块大小、记忆库容量与阈值余量。</param>
    /// <param name = "token">协作式取消标记。</param>
    PatchAnomalyModel Train(
        IReadOnlyList<IImageSource> good,
        IReadOnlyList<int> sources,
        PatchAnomalyOptions options,
        CancellationToken token = default
    );
}
