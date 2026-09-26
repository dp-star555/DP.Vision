using System.Collections.Generic;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>
/// 仅用良品训练的局部块异常检测（PatchCore式）：把良品图的局部块存入记忆库，
/// 检测时每个块到最近良品块的距离即异常得分。块与位置无关，可变内容（序列号等）只要笔画形态在良品中出现过即可。
/// </summary>
public interface IPatchAnomalyDetector
{
    /// <summary>用良品图训练模型并按留一法（或增强）标定阈值。</summary>
    /// <param name = "good">同一ROI或同类区域的良品裁图，至少1张。</param>
    /// <param name = "options">块大小、记忆库容量与阈值余量。</param>
    /// <param name = "token">协作式取消标记。</param>
    PatchAnomalyModel Train(
        IReadOnlyList<IImageSource> good,
        PatchAnomalyOptions options,
        CancellationToken token = default
    );

    /// <summary>检测一张裁图。</summary>
    /// <param name = "image">待检裁图，尺度需与训练图一致。</param>
    /// <param name = "model">训练得到的模型。</param>
    /// <param name = "options">检测步长、显式阈值与最小面积。</param>
    /// <param name = "token">协作式取消标记。</param>
    PatchAnomalyResult Detect(
        IImageSource image,
        PatchAnomalyModel model,
        PatchAnomalyOptions options,
        CancellationToken token = default
    );
}
