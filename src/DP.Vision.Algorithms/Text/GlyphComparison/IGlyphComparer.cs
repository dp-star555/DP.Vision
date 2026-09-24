using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>可替换的单字测量接口；不负责选择业务参考，也不决定整张标签的放行。</summary>
public interface IGlyphComparer
{
    /// <summary>借用像素进行只读测量，返回独立拥有的证据；不支持的布局或证据不足使用显式状态，取消则抛出异常。</summary>
    /// <param name = "actual">实际单字图块，调用期间借用，不修改或接管原租约。</param>
    /// <param name = "reference">独立参考图块，调用期间借用，不作为实际读数。</param>
    /// <param name = "options">归一化测量参数，阈值和容差含义与实现相关。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>调用方必须Dispose的比较结果。</returns>
    GlyphComparisonResult Compare(
        IImageSource actual,
        IImageSource reference,
        GlyphComparisonOptions options,
        CancellationToken token = default
    );
}
