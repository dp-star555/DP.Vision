using System;

namespace DP.Vision.Algorithms;

/// <summary>实测墨迹异常；范围使用原图像素边缘坐标。</summary>
public sealed class InkDefect
{
    /// <summary>创建具有正面积的局部测量记录。</summary>
    /// <param name = "code">缺墨、多墨或空白污点的稳定代码。</param>
    /// <param name = "bounds">原图像素边缘坐标中的范围。</param>
    /// <param name = "area">实际连通像素的正面积，不是外接框面积。</param>
    public InkDefect(string code, RectD bounds, int area)
    {
        if (string.IsNullOrWhiteSpace(code) || bounds.Width <= 0 || bounds.Height <= 0 || area < 1)
        {
            throw new ArgumentException("Invalid defect measurement.");
        }

        Code = code;
        Bounds = bounds;
        Area = area;
    }

    /// <summary>异常码：missing_ink缺墨、extra_ink多墨或blank_spot空白污点。</summary>
    public string Code { get; }

    /// <summary>原图坐标范围。</summary>
    public RectD Bounds { get; }

    /// <summary>实际连通像素数，不是外接矩形面积。</summary>
    public int Area { get; }
}
