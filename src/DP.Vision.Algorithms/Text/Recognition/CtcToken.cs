using System;

namespace DP.Vision.Algorithms;
/// <summary>解码后的CTC连续激活区间，不是物理字符边界。</summary>
public sealed class CtcToken
{
    /// <summary>创建连续激活区间。</summary>
    /// <param name = "text">字典字符标签。</param>
    /// <param name = "start">起始时间步，包含端点。</param>
    /// <param name = "end">结束时间步，不包含端点。</param>
    /// <param name = "confidence">首时间步概率，与贪心参考解码一致。</param>
    public CtcToken(string text, int start, int end, float confidence)
    {
        if (
            string.IsNullOrEmpty(text)
            || start < 0
            || end <= start
            || float.IsNaN(confidence)
            || confidence < 0
            || confidence > 1
        )
        {
            throw new ArgumentException("Invalid CTC token.");
        }

        Text = text;
        Start = start;
        End = end;
        Confidence = confidence;
    }

    /// <summary>解码后的字符标签。</summary>
    public string Text { get; }

    /// <summary>包含端点的激活起始时间步。</summary>
    public int Start { get; }

    /// <summary>不包含端点的激活结束时间步。</summary>
    public int End { get; }

    /// <summary>未经正确率标定的识别置信度。</summary>
    public float Confidence { get; }
}
