using System;

namespace DP.Vision.Algorithms;

/// <summary>逐字符异常检测的训练参数。</summary>
public sealed class CharacterAnomalyOptions
{
    /// <summary>创建训练参数。</summary>
    /// <param name = "thresholdMargin">自动阈值相对良品留一法最大得分的倍数（1–5）。</param>
    /// <param name = "localRadius">单元内位置相关搜索半径（归一化像素，1–16）：字符已按行几何和分割中心归一化，更大的半径只增加误报和耗时。</param>
    /// <param name = "maximumSamples">每个模型最多使用的训练样本数，超出时按形态多样性选取（检测耗时与样本数成正比）。</param>
    public CharacterAnomalyOptions(double thresholdMargin = 1.5, int localRadius = 1, int maximumSamples = 16)
    {
        if (maximumSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSamples));
        }

        Patch = new PatchAnomalyOptions(thresholdMargin: thresholdMargin, localRadius: localRadius);
        MaximumSamples = maximumSamples;
    }

    /// <summary>各字符模型的局部块训练参数（位置相关模式）。</summary>
    public PatchAnomalyOptions Patch { get; }

    /// <summary>每个模型最多使用的训练样本数。</summary>
    public int MaximumSamples { get; }
}
