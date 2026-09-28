using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>逐字符异常检测中一个字符的结果（原图坐标）。</summary>
public sealed class CharacterAnomalyOutcome
{
    /// <summary>创建结果。</summary>
    /// <param name = "character">被检测的字符输入。</param>
    /// <param name = "status">检测状态。</param>
    /// <param name = "maximumScore">局部块最大得分；未检测时为0。</param>
    /// <param name = "threshold">模型阈值；未检测时为0。</param>
    /// <param name = "findings">异常区域（原图坐标）、说明或阻断原因，按局部块、缺墨、缺墨说明的顺序。</param>
    /// <param name = "inkLoss">缺墨最大值（墨量比例）；未做缺墨检查时为null。</param>
    /// <param name = "inkThreshold">缺墨阈值；未做缺墨检查时为null。</param>
    public CharacterAnomalyOutcome(
        CharacterAnomalyCharacter character,
        ECharacterAnomalyStatus status,
        double maximumScore,
        double threshold,
        IEnumerable<QualityFinding> findings,
        double? inkLoss = null,
        double? inkThreshold = null
    )
    {
        Character = character ?? throw new ArgumentNullException(nameof(character));
        Status = status;
        MaximumScore = maximumScore;
        Threshold = threshold;
        Findings = Array.AsReadOnly(
            (findings ?? throw new ArgumentNullException(nameof(findings))).ToArray()
        );
        InkLoss = inkLoss;
        InkThreshold = inkThreshold;
    }

    /// <summary>被检测的字符输入。</summary>
    public CharacterAnomalyCharacter Character { get; }

    /// <summary>检测状态。</summary>
    public ECharacterAnomalyStatus Status { get; }

    /// <summary>局部块最大得分。</summary>
    public double MaximumScore { get; }

    /// <summary>模型阈值。</summary>
    public double Threshold { get; }

    /// <summary>异常区域、说明或阻断原因。</summary>
    public IReadOnlyList<QualityFinding> Findings { get; }

    /// <summary>缺墨最大值；未做缺墨检查时为null。</summary>
    public double? InkLoss { get; }

    /// <summary>缺墨阈值；未做缺墨检查时为null。</summary>
    public double? InkThreshold { get; }
}
