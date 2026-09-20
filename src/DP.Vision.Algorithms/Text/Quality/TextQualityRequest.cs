using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>中立的文字质量输入，不包含标签配方、业务引导值或存储仓库。</summary>
public sealed class TextQualityRequest
{
    /// <summary>创建同步借用的文字质量请求；身份来自真实读取或明确声明的等格标签，不做静默修正。</summary>
    /// <param name = "image">原始只读图像，调用期间借用。</param>
    /// <param name = "bounds">原图整数范围，必须完全位于image内。</param>
    /// <param name = "identity">实际OCR文本，或调用方明确确认的等格标签；不是自动生成的业务真值。</param>
    /// <param name = "equalCells">是否明确声明等宽单元；false使用实际物理分割。</param>
    /// <param name = "references">大小写敏感的独立参考集合，图像在调用期间必须有效。</param>
    /// <param name = "threshold">固定二值化阈值，范围1–255。</param>
    /// <param name = "tolerance">归一化像素容差，范围0–8。</param>
    /// <param name = "maximumDifference">本归一化策略允许的最大差异比，范围0–5，不是OCR置信度。</param>
    public TextQualityRequest(
        IImageSource image,
        PixelBounds bounds,
        string identity,
        bool equalCells,
        IReadOnlyDictionary<string, GlyphTemplate> references,
        int threshold,
        int tolerance,
        double maximumDifference
    )
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        References = references ?? throw new ArgumentNullException(nameof(references));
        if (!bounds.Fits(image))
        {
            throw new ArgumentException("Text scope must fit the original image.", nameof(bounds));
        }

        if (threshold < 1 || threshold > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        if (tolerance < 0 || tolerance > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }

        if (double.IsNaN(maximumDifference) || maximumDifference < 0 || maximumDifference > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDifference));
        }

        Bounds = bounds;
        EqualCells = equalCells;
        Threshold = threshold;
        Tolerance = tolerance;
        MaximumDifference = maximumDifference;
    }

    /// <summary>调用期间借用的原始图像。</summary>
    public IImageSource Image { get; }

    /// <summary>原图坐标中的整数范围。</summary>
    public PixelBounds Bounds { get; }

    /// <summary>实际OCR身份或调用方明确声明的等格标签。</summary>
    public string Identity { get; }

    /// <summary>明确的等宽布局声明，不是分割失败后的隐式回退。</summary>
    public bool EqualCells { get; }

    /// <summary>借用的独立参考集合。</summary>
    public IReadOnlyDictionary<string, GlyphTemplate> References { get; }

    /// <summary>固定二值化灰度阈值。</summary>
    public int Threshold { get; }

    /// <summary>归一化像素中的容差半径。</summary>
    public int Tolerance { get; }

    /// <summary>仅适用于本归一化测量策略的差异阈值。</summary>
    public double MaximumDifference { get; }
}
