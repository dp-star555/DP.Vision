using System;

namespace DP.Vision.UI;

/// <summary>
/// 轴对齐矩形ROI的整像素编辑规则：绘制、移动和缩放都落在整数像素边缘上并限制在原图内，
/// 新建与缩放不小于最小边长；选择时优先面积最小的命中ROI（嵌套时能选中内层）。只作用于轴对齐矩形。
/// </summary>
public sealed class RoiPixelRules
{
    /// <summary>创建规则。</summary>
    /// <param name = "imageWidth">原图宽度（像素），编辑结果不超出。</param>
    /// <param name = "imageHeight">原图高度（像素）。</param>
    /// <param name = "minimumSize">新建与缩放的最小边长（像素）。</param>
    public RoiPixelRules(int imageWidth, int imageHeight, int minimumSize = 4)
    {
        if (imageWidth < 1 || imageHeight < 1 || minimumSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        }

        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        MinimumSize = minimumSize;
    }

    /// <summary>原图宽度。</summary>
    public int ImageWidth { get; }

    /// <summary>原图高度。</summary>
    public int ImageHeight { get; }

    /// <summary>新建与缩放的最小边长。</summary>
    public int MinimumSize { get; }
}
