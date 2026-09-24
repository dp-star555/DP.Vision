using System;
using System.Collections.Generic;

namespace DP.Vision.UI;

/// <summary>
/// 一次绘制内的文字标注避让：与先前标注重叠时向下移到其下方，不改变几何本身。
/// 每次绘制新建一个实例；只在UI线程使用，坐标单位为控件像素或DIP。
/// </summary>
public sealed class CaptionLayout
{
    // 只与最近放置的标注比较，保证大量结果标注时每帧开销有界。
    private const int Window = 256;
    private readonly List<(double Left, double Top, double Right, double Bottom)> _placed =
        new List<(double Left, double Top, double Right, double Bottom)>();

    /// <summary>放置一条标注并返回其实际顶部坐标；左侧坐标保持不变。</summary>
    /// <param name="left">期望的左侧坐标。</param>
    /// <param name="top">期望的顶部坐标，通常为几何外接框左上角。</param>
    /// <param name="width">标注宽度，非负。</param>
    /// <param name="height">标注高度，非负。</param>
    /// <returns>避开已放置标注后的顶部坐标。</returns>
    public double Place(double left, double top, double width, double height)
    {
        if (width < 0 || height < 0 || double.IsNaN(width) || double.IsNaN(height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "标注宽高必须为非负数。");
        }

        double right = left + width;
        for (int i = Math.Max(0, _placed.Count - Window); i < _placed.Count; i++)
        {
            var other = _placed[i];
            if (left < other.Right && other.Left < right && top < other.Bottom && other.Top < top + height)
            {
                top = other.Bottom;
            }
        }

        _placed.Add((left, top, right, top + height));
        return top;
    }
}
