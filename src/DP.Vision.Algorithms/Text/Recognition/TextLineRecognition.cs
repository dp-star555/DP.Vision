using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

using PixelRect = DP.Vision.Algorithms.PixelBounds;

/// <summary>不可变的单行OCR证据，与外观合格证明分开。</summary>
public sealed class TextLineRecognition
{
    /// <summary>复制解码观测，并保留原图坐标来源。</summary>
    /// <param name = "bounds">原图中选择的ROI。</param>
    /// <param name = "modelSha256">实际加载模型的精确标识。</param>
    /// <param name = "inputWidth">填充后宽度。</param>
    /// <param name = "contentWidth">不含填充的内容宽度。</param>
    /// <param name = "steps">全部时间步观测。</param>
    /// <param name = "tokens">解码后的激活区间。</param>
    public TextLineRecognition(
        PixelRect bounds,
        string modelSha256,
        int inputWidth,
        int contentWidth,
        IEnumerable<CtcStep> steps,
        IEnumerable<CtcToken> tokens
    )
    {
        if (
            bounds.Width < 1
            || bounds.Height < 1
            || inputWidth < 1
            || contentWidth < 1
            || contentWidth > inputWidth
        )
        {
            throw new ArgumentException("Invalid recognition geometry.");
        }

        if (steps == null || tokens == null)
        {
            throw new ArgumentNullException(nameof(steps));
        }

        var s = steps.ToArray();
        var t = tokens.ToArray();
        if (s.Length == 0 || s.Any(v => v == null) || t.Any(v => v == null || v.End > s.Length))
        {
            throw new ArgumentException("Invalid decoder observations.");
        }

        Bounds = bounds;
        ModelSha256 = modelSha256 ?? throw new ArgumentNullException(nameof(modelSha256));
        InputWidth = inputWidth;
        ContentWidth = contentWidth;
        Steps = new ReadOnlyCollection<CtcStep>(s);
        Tokens = new ReadOnlyCollection<CtcToken>(t);
        Text = string.Concat(t.Select(v => v.Text));
        Confidence = t.Length == 0 ? 0 : t.Average(v => (double)v.Confidence);
    }

    /// <summary>原图中选定的ROI。</summary>
    public PixelRect Bounds { get; }

    /// <summary>模型原始字节的SHA256。</summary>
    public string ModelSha256 { get; }

    /// <summary>填充后的输入宽度。</summary>
    public int InputWidth { get; }

    /// <summary>实际缩放后的宽度。</summary>
    public int ContentWidth { get; }

    /// <summary>全部最大概率类别观测，不只保留已解码字符。</summary>
    public IReadOnlyList<CtcStep> Steps { get; }

    /// <summary>连续激活区间，不能当作物理切字框。</summary>
    public IReadOnlyList<CtcToken> Tokens { get; }

    /// <summary>识别假设，不是预期字符串。</summary>
    public string Text { get; }

    /// <summary>所选时间步的平均置信度，空输出为0。</summary>
    public double Confidence { get; }
}
