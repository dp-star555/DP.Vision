using System;

namespace DP.Vision.Algorithms;

/// <summary>一个模型键的逐字符训练结果：局部块模型、归一化单元尺寸及缺墨阈值。</summary>
public sealed class CharacterAnomalyTraining
{
    /// <summary>创建训练结果。</summary>
    /// <param name = "key">模型键。</param>
    /// <param name = "model">局部块模型。</param>
    /// <param name = "options">训练所用局部块参数（检测步长、最小面积随库保存）。</param>
    /// <param name = "cellWidth">归一化单元宽度。</param>
    /// <param name = "cellHeight">归一化单元高度。</param>
    /// <param name = "samples">实际使用的训练样本数。</param>
    /// <param name = "inkThreshold">缺墨阈值；来源图少于2张或模型不支持时为null。</param>
    /// <param name = "calibration">阈值标定说明（含缺墨标定）。</param>
    public CharacterAnomalyTraining(
        string key,
        PatchAnomalyModel model,
        PatchAnomalyOptions options,
        int cellWidth,
        int cellHeight,
        int samples,
        double? inkThreshold,
        string calibration
    )
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        Samples = samples;
        InkThreshold = inkThreshold;
        Calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
    }

    /// <summary>模型键。</summary>
    public string Key { get; }

    /// <summary>局部块模型。</summary>
    public PatchAnomalyModel Model { get; }

    /// <summary>训练所用局部块参数。</summary>
    public PatchAnomalyOptions Options { get; }

    /// <summary>归一化单元宽度。</summary>
    public int CellWidth { get; }

    /// <summary>归一化单元高度。</summary>
    public int CellHeight { get; }

    /// <summary>实际使用的训练样本数。</summary>
    public int Samples { get; }

    /// <summary>缺墨阈值（墨量比例）；未标定时为null。</summary>
    public double? InkThreshold { get; }

    /// <summary>阈值标定说明。</summary>
    public string Calibration { get; }
}
