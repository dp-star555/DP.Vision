using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>
/// 逐字符局部块异常检测，用于内容可变的文字：每个模型键（通常为字符或“字体组/字符”）一个模型，由多个良品样本训练。
/// 字符按整行几何归一化后与同键良品比较，相邻字符与字符间距的变化互不影响；可选缺墨检查报告比所有良品都浅的笔画。
/// 字符身份与分割由调用方提供（OCR、分割或显式等格）。
/// </summary>
[VisionCapability("anomaly.character", "异常检测", "字符异常检测")]
public interface ICharacterAnomalyDetector
{
    /// <summary>按模型键汇总各行样本并训练，每个键一个结果，按键的序数顺序。</summary>
    /// <param name = "lines">良品行样本，字符身份须已确认。</param>
    /// <param name = "options">训练参数。</param>
    /// <param name = "token">协作式取消标记。</param>
    IReadOnlyList<CharacterAnomalyTraining> Train(
        IReadOnlyList<CharacterAnomalyLine> lines,
        CharacterAnomalyOptions options,
        CancellationToken token = default
    );

    /// <summary>
    /// 按训练时的方式归一化字符单元：每个键的单元宽度由训练行决定，其他行按同一宽度归一化；训练行中没有的键不输出，不做样本数上限选取。
    /// </summary>
    /// <param name = "training">训练行。</param>
    /// <param name = "others">其他（测试）行，序号接在训练行之后。</param>
    /// <param name = "token">协作式取消标记。</param>
    IReadOnlyList<NormalizedCharacterCell> NormalizeCells(
        IReadOnlyList<CharacterAnomalyLine> training,
        IReadOnlyList<CharacterAnomalyLine> others,
        CancellationToken token = default
    );

    /// <summary>检测一行中带模型键的字符，异常区域以原图坐标报告，结果顺序与输入一致。</summary>
    /// <param name = "image">借用的整张待检图。</param>
    /// <param name = "characters">该行字符（原图坐标）；全部字母/数字参与行几何测量。</param>
    /// <param name = "crop">热力图对应的原图范围，通常为ROI。</param>
    /// <param name = "references">按模型键查找参考；没有时返回null。</param>
    /// <param name = "inkLoss">是否做缺墨检查（参考未标定缺墨阈值时不做）。</param>
    /// <param name = "token">协作式取消标记。</param>
    CharacterAnomalyInspection Inspect(
        IImageSource image,
        IReadOnlyList<CharacterAnomalyCharacter> characters,
        PixelBounds crop,
        Func<string, CharacterAnomalyReference?> references,
        bool inkLoss = true,
        CancellationToken token = default
    );
}
