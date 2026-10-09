using System;

namespace DP.Vision.Algorithms;

/// <summary>借用的字符运行实例及制作几何；不要求模型是Patch记忆库。</summary>
public sealed class CharacterAnomalyReference
{
    /// <summary>创建厂商中立参考。</summary>
    /// <param name="runtime">借用实例，租约由调用者管理。</param>
    /// <param name="detection">本次检测参数。</param>
    /// <param name="cellWidth">制作单元宽度。</param>
    /// <param name="cellHeight">制作单元高度。</param>
    /// <param name="inkThreshold">旧手工模型的可选缺墨阈值。</param>
    /// <param name="normalization">制作行几何方式。</param>
    public CharacterAnomalyReference(ILoadedAnomalyModel runtime, AnomalyDetectionOptions detection,
        int cellWidth, int cellHeight, double? inkThreshold, ECharacterNormalization normalization = ECharacterNormalization.LineInk)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Detection = detection ?? throw new ArgumentNullException(nameof(detection));
        if (cellWidth < 1 || cellHeight < 1 || !Enum.IsDefined(typeof(ECharacterNormalization), normalization)) throw new ArgumentException("字符制作几何无效。");
        CellWidth = cellWidth; CellHeight = cellHeight; InkThreshold = inkThreshold; Normalization = normalization;
    }
    /// <summary>借用的厂商运行实例。</summary>
    public ILoadedAnomalyModel Runtime { get; }
    /// <summary>检测参数。</summary>
    public AnomalyDetectionOptions Detection { get; }
    /// <summary>制作宽度。</summary>
    public int CellWidth { get; }
    /// <summary>制作高度。</summary>
    public int CellHeight { get; }
    /// <summary>制作行归一化方式。</summary>
    public ECharacterNormalization Normalization { get; }
    /// <summary>旧手工缺墨标定阈值。</summary>
    public double? InkThreshold { get; }
}
