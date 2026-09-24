using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>可替换的已对齐固定图案检查接口，独立于标签配方和模板存储。</summary>
public interface IFixedQualityInspector
{
    /// <summary>比较同尺寸借用图块；可选二值Gray8掩码同时作用于两图，排除像素周围的容差扩展区不测量。不修改或保留输入；取消及无效参数均抛出异常。</summary>
    /// <param name = "actual">借用的实际图块。</param>
    /// <param name = "reference">借用的同尺寸已对齐参考图块。</param>
    /// <param name = "origin">图块左上边缘在原图中的坐标，单位为原图像素。</param>
    /// <param name = "options">阈值、原图容差和最小连通面积。</param>
    /// <param name = "allowedMask">同时作用于两图的同尺寸二值Gray8掩码，null表示不额外排除。</param>
    /// <param name = "token">协作式取消标记。</param>
    InkInspectionResult Inspect(
        IImageSource actual,
        IImageSource reference,
        PointD origin,
        InkInspectionOptions options,
        IImageSource? allowedMask = null,
        CancellationToken token = default
    );
}
