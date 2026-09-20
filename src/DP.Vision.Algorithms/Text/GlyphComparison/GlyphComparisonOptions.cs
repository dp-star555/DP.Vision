using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>归一化单字测量设置，不是整张标签的业务放行规则。</summary>
public sealed class GlyphComparisonOptions
{
    /// <summary>创建单字测量参数；容差单位为归一化像素，设0不会关闭归一化或自动平移对齐。</summary>
    /// <param name = "threshold">固定二值化阈值，范围1–255；墨迹灰度必须严格小于该值。</param>
    /// <param name = "tolerance">归一化墨迹膨胀半径，范围0–8；2对应5×5结构元素。</param>
    /// <param name = "binarization">应用于实际图和参考图的二值化模式，两图分别计算。</param>
    public GlyphComparisonOptions(
        int threshold = 160,
        int tolerance = 2,
        EGlyphBinarization binarization = EGlyphBinarization.Otsu
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

        Threshold = threshold;
        Tolerance = tolerance;
        Binarization = binarization;
    }

    /// <summary>固定模式的排他灰度阈值，像素灰度小于它才属于墨迹。</summary>
    public int Threshold { get; }

    /// <summary>归一化像素中的膨胀半径，不是原图像素容差。</summary>
    public int Tolerance { get; }

    /// <summary>参考配置选择的二值化模式，两张图各自执行。</summary>
    public EGlyphBinarization Binarization { get; }
}
