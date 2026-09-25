using System;

namespace DP.Vision.Algorithms;

/// <summary>归一化单字测量设置，不是整张标签的业务放行规则。</summary>
public sealed class GlyphComparisonOptions
{
    /// <summary>创建单字测量参数；容差单位为归一化像素，设0不会关闭归一化或自动平移对齐。深度比例取默认0.5。</summary>
    /// <param name = "threshold">固定二值化阈值，范围1–255；墨迹灰度必须严格小于该值。</param>
    /// <param name = "tolerance">边缘带宽度，范围0–8；只触及笔画边缘这一宽度内的差异块视为印刷波动。</param>
    /// <param name = "binarization">应用于实际图和参考图的二值化模式，两图分别计算。</param>
    public GlyphComparisonOptions(
        int threshold = 160,
        int tolerance = 2,
        EGlyphBinarization binarization = EGlyphBinarization.Otsu
    )
        : this(threshold, tolerance, binarization, DefaultDepthRatio) { }

    /// <summary>创建单字测量参数，并指定差异块须深入笔画半宽的比例。</summary>
    /// <param name = "threshold">固定二值化阈值，范围1–255；墨迹灰度必须严格小于该值。</param>
    /// <param name = "tolerance">边缘带宽度，范围0–8；只触及笔画边缘这一宽度内的差异块视为印刷波动。</param>
    /// <param name = "binarization">应用于实际图和参考图的二值化模式，两图分别计算。</param>
    /// <param name = "depthRatio">范围0–1；差异块最深点须达到局部笔画半宽的该比例才计入，0只按边缘带判断。</param>
    public GlyphComparisonOptions(
        int threshold,
        int tolerance,
        EGlyphBinarization binarization,
        double depthRatio
    )
    {
        if (threshold < 1 || threshold > 255 || tolerance < 0 || tolerance > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        if (!Enum.IsDefined(typeof(EGlyphBinarization), binarization))
        {
            throw new ArgumentOutOfRangeException(nameof(binarization));
        }

        if (double.IsNaN(depthRatio) || depthRatio < 0 || depthRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depthRatio));
        }

        Threshold = threshold;
        Tolerance = tolerance;
        Binarization = binarization;
        DepthRatio = depthRatio;
    }

    /// <summary>未指定时的深度比例：差异须深入局部笔画半宽的一半。</summary>
    public const double DefaultDepthRatio = .5;

    /// <summary>固定模式的排他灰度阈值，像素灰度小于它才属于墨迹。</summary>
    public int Threshold { get; }

    /// <summary>归一化像素中的边缘带宽度，不是原图像素容差；不再通过膨胀缩小真实缺陷。</summary>
    public int Tolerance { get; }

    /// <summary>差异块最深点须达到的局部笔画半宽比例；缺墨上限为整个半宽，使细笔画断裂仍可检出。</summary>
    public double DepthRatio { get; }

    /// <summary>参考配置选择的二值化模式，两张图各自执行。</summary>
    public EGlyphBinarization Binarization { get; }
}
