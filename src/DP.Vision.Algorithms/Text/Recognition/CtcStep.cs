using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

using PixelRect = DP.Vision.Algorithms.PixelBounds;

/// <summary>一个CTC时间步的原始最大概率类别观测，包含空白和重复字符。</summary>
public sealed class CtcStep
{
    /// <summary>创建概率观测。</summary>
    /// <param name = "classIndex">模型类别索引，0为空白。</param>
    /// <param name = "confidence">[0,1]范围的概率。</param>
    public CtcStep(int classIndex, float confidence)
    {
        if (classIndex < 0 || float.IsNaN(confidence) || confidence < 0 || confidence > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(classIndex));
        }

        ClassIndex = classIndex;
        Confidence = confidence;
    }

    /// <summary>模型字典类别。</summary>
    public int ClassIndex { get; }

    /// <summary>最大类别概率，不是经标定的正确率。</summary>
    public float Confidence { get; }
}
