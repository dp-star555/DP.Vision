using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>原图像素墨迹检查设置；实现必须保留所声明的阈值和掩码语义。</summary>
public sealed class InkInspectionOptions
{
    /// <summary>创建设置：灰度严格低于阈值视为墨迹，容差为矩形膨胀半径，连通域采用8邻接。</summary>
    /// <param name = "threshold">固定灰度阈值，像素灰度严格小于它才视为墨迹。</param>
    /// <param name = "tolerance">矩形膨胀半径，单位为原图像素。</param>
    /// <param name = "minimumArea">保留连通域的最小面积，单位为原图平方像素。</param>
    public InkInspectionOptions(int threshold = 160, int tolerance = 1, int minimumArea = 8)
    {
        if (threshold < 1 || threshold > 255 || tolerance < 0 || tolerance > 10 || minimumArea < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        Threshold = threshold;
        Tolerance = tolerance;
        MinimumArea = minimumArea;
    }

    /// <summary>排他灰度阈值。</summary>
    public int Threshold { get; }

    /// <summary>以原图像素计的半径，不是归一化字符像素。</summary>
    public int Tolerance { get; }

    /// <summary>保留连通域的最小面积，单位为原图平方像素。</summary>
    public int MinimumArea { get; }
}
