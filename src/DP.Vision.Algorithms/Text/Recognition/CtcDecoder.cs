using System;
using System.Collections.Generic;

namespace DP.Vision.Algorithms;

/// <summary>贪心CTC连续激活解码；空白分隔重复字符，激活区间不等于图像边界。</summary>
public static class CtcDecoder
{
    /// <summary>解码全部时间步，不丢弃填充区内的观测。</summary>
    /// <param name = "steps">各时间步的最大概率类别观测。</param>
    /// <param name = "dictionary">索引0为空白，其后为模型字符标签。</param>
    /// <returns>解码后的连续激活区间。</returns>
    public static IReadOnlyList<CtcToken> Decode(
        IReadOnlyList<CtcStep> steps,
        IReadOnlyList<string> dictionary
    )
    {
        if (steps == null)
        {
            throw new ArgumentNullException(nameof(steps));
        }

        if (dictionary == null || dictionary.Count < 2)
        {
            throw new ArgumentException("CTC dictionary required.", nameof(dictionary));
        }

        var tokens = new List<CtcToken>();
        int start = 0;
        while (start < steps.Count)
        {
            var step = steps[start] ?? throw new ArgumentException("Null CTC step.", nameof(steps));
            if (step.ClassIndex >= dictionary.Count)
            {
                throw new ArgumentException("Class outside dictionary.", nameof(steps));
            }

            int end = start + 1;
            while (end < steps.Count && steps[end] != null && steps[end].ClassIndex == step.ClassIndex)
            {
                end++;
            }

            if (step.ClassIndex != 0)
            {
                tokens.Add(new CtcToken(dictionary[step.ClassIndex], start, end, step.Confidence));
            }

            start = end;
        }

        return tokens.AsReadOnly();
    }
}
