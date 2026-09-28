using System;

namespace DP.Vision.Algorithms;

/// <summary>检测时一个字符的参考：模型、与其特征来源匹配的检测实现、检测参数、单元尺寸及缺墨阈值。</summary>
public sealed class CharacterAnomalyReference
{
    /// <summary>创建字符参考。</summary>
    /// <param name = "model">局部块模型。</param>
    /// <param name = "detector">与模型特征来源一致的检测实现，调用方拥有。</param>
    /// <param name = "detection">检测参数（步长、阈值、最小面积）。</param>
    /// <param name = "cellWidth">训练时的归一化单元宽度。</param>
    /// <param name = "cellHeight">训练时的归一化单元高度，与当前实现不一致时不检测。</param>
    /// <param name = "inkThreshold">缺墨阈值；null时不做缺墨检查。</param>
    public CharacterAnomalyReference(
        PatchAnomalyModel model,
        IPatchAnomalyDetector detector,
        PatchAnomalyOptions detection,
        int cellWidth,
        int cellHeight,
        double? inkThreshold
    )
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Detector = detector ?? throw new ArgumentNullException(nameof(detector));
        Detection = detection ?? throw new ArgumentNullException(nameof(detection));
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        InkThreshold = inkThreshold;
    }

    /// <summary>局部块模型。</summary>
    public PatchAnomalyModel Model { get; }

    /// <summary>检测实现。</summary>
    public IPatchAnomalyDetector Detector { get; }

    /// <summary>检测参数。</summary>
    public PatchAnomalyOptions Detection { get; }

    /// <summary>归一化单元宽度。</summary>
    public int CellWidth { get; }

    /// <summary>归一化单元高度。</summary>
    public int CellHeight { get; }

    /// <summary>缺墨阈值。</summary>
    public double? InkThreshold { get; }
}
