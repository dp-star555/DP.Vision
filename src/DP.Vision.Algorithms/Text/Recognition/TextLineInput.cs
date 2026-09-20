using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

using PixelRect = DP.Vision.Algorithms.PixelBounds;

/// <summary>拥有数据的归一化BGR/NCHW单行输入，已包含零填充。</summary>
public sealed class TextLineInput
{
    private readonly float[] _values;

    /// <summary>复制归一化的3×48×width张量及缩放几何信息。</summary>
    /// <param name = "width">填充后宽度，最大4096。</param>
    /// <param name = "contentWidth">缩放后不含填充的内容宽度。</param>
    /// <param name = "values">通道优先排列的数据，值域[-1,1]。</param>
    public TextLineInput(int width, int contentWidth, float[] values)
    {
        if (width < 1 || width > 4096 || contentWidth < 1 || contentWidth > width)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        if (values.Length != 3 * 48 * width || values.Any(v => float.IsNaN(v) || v < -1 || v > 1))
        {
            throw new ArgumentException("Invalid normalized tensor.", nameof(values));
        }

        Width = width;
        ContentWidth = contentWidth;
        _values = (float[])values.Clone();
    }

    /// <summary>填充后的模型输入宽度。</summary>
    public int Width { get; }

    /// <summary>实际缩放内容的宽度。</summary>
    public int ContentWidth { get; }

    /// <summary>导出独立的NCHW缓冲区。</summary>
    /// <returns>归一化数值副本。</returns>
    public float[] CopyValues()
    {
        return (float[])_values.Clone();
    }
}
