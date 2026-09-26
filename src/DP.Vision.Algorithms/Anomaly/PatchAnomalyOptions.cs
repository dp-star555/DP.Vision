using System;

namespace DP.Vision.Algorithms;

/// <summary>
/// 局部块异常检测（PatchCore式）的参数：块大小、采样步长、记忆库容量与阈值余量。
/// 只描述测量方式，不包含标签业务规则。
/// </summary>
public sealed class PatchAnomalyOptions
{
    /// <summary>创建并校验参数。</summary>
    /// <param name = "patchSize">原尺度块边长（像素），4–32，默认8；另取2倍尺度同样大小的块作上下文。</param>
    /// <param name = "stride">检测时块的采样步长，1–16，默认2；训练固定按1/2步长的相位采样。</param>
    /// <param name = "memorySize">记忆库最多保留的块数，256–200000，默认6000；超出时按贪心k中心保留覆盖最广的块。</param>
    /// <param name = "thresholdMargin">自动阈值相对良品留一法最大得分的倍数，1–5，默认1.5（良品样本少时留足余量）。</param>
    /// <param name = "threshold">显式阈值；为null时使用训练时标定的阈值。</param>
    /// <param name = "minimumArea">异常区域最小面积（原图平方像素），1–100000，默认6。</param>
    /// <param name = "localRadius">
    /// 位置相关搜索半径（像素），1–16；为null时与位置无关（适合可变内容）。固定内容ROI建议3：
    /// 缺笔画的字只和良品同一位置的字比较，不会被别处正常的笔画端点“解释”掉。
    /// </param>
    public PatchAnomalyOptions(
        int patchSize = 8,
        int stride = 2,
        int memorySize = 6000,
        double thresholdMargin = 1.5,
        double? threshold = null,
        int minimumArea = 6,
        int? localRadius = null
    )
    {
        if (patchSize < 4 || patchSize > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(patchSize));
        }

        if (stride < 1 || stride > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        if (memorySize < 256 || memorySize > 200000)
        {
            throw new ArgumentOutOfRangeException(nameof(memorySize));
        }

        if (double.IsNaN(thresholdMargin) || thresholdMargin < 1 || thresholdMargin > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdMargin));
        }

        if (threshold is { } t && (double.IsNaN(t) || double.IsInfinity(t) || t <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        if (minimumArea < 1 || minimumArea > 100000)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumArea));
        }

        if (localRadius is { } r && (r < 1 || r > 16))
        {
            throw new ArgumentOutOfRangeException(nameof(localRadius));
        }

        LocalRadius = localRadius;
        PatchSize = patchSize;
        Stride = stride;
        MemorySize = memorySize;
        ThresholdMargin = thresholdMargin;
        Threshold = threshold;
        MinimumArea = minimumArea;
    }

    /// <summary>原尺度块边长（像素）。</summary>
    public int PatchSize { get; }

    /// <summary>检测时块的采样步长（像素）。</summary>
    public int Stride { get; }

    /// <summary>记忆库最多保留的块数。</summary>
    public int MemorySize { get; }

    /// <summary>自动阈值相对良品留一法最大得分的倍数。</summary>
    public double ThresholdMargin { get; }

    /// <summary>显式阈值；为null时使用模型标定值。</summary>
    public double? Threshold { get; }

    /// <summary>异常区域最小面积（原图平方像素）。</summary>
    public int MinimumArea { get; }

    /// <summary>位置相关搜索半径（像素）；null为与位置无关。</summary>
    public int? LocalRadius { get; }
}
