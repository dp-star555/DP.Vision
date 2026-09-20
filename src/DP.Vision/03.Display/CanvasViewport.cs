using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>WinForms/WPF共用的视口计算；控件单位分别为像素和DIP。</summary>
public sealed class CanvasViewport
{
    /// <summary>每个原图像素对应的控件单位数。</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>原图左上角在控件中的位置。</summary>
    public PointD Origin { get; private set; }

    /// <summary>将完整原图适配到控件客户区并居中。</summary>
    /// <param name = "width">原图宽度，单位为像素。</param>
    /// <param name = "height">原图高度，单位为像素。</param>
    /// <param name = "clientWidth">客户区宽度，单位为控件像素或DIP。</param>
    /// <param name = "clientHeight">客户区高度，单位为控件像素或DIP。</param>
    public void Fit(int width, int height, double clientWidth, double clientHeight)
    {
        Scale = Math.Max(
            1.0 / 1024,
            Math.Min(Math.Max(1, clientWidth - 24) / width, Math.Max(1, clientHeight - 24) / height)
        );
        Origin = new PointD((clientWidth - width * Scale) / 2, (clientHeight - height * Scale) / 2);
    }

    /// <summary>围绕指定控件点缩放，并限制缩放范围。</summary>
    /// <param name = "factor">本次缩放倍率，有限且大于0。</param>
    /// <param name = "anchor">缩放时保持原图位置不变的控件坐标点。</param>
    public void Zoom(double factor, PointD anchor)
    {
        if (!PointD.Valid(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        double next = Math.Max(1.0 / 1024, Math.Min(128, Scale * factor)),
            ratio = next / Scale;
        Origin = new PointD(
            anchor.X - (anchor.X - Origin.X) * ratio,
            anchor.Y - (anchor.Y - Origin.Y) * ratio
        );
        Scale = next;
    }

    /// <summary>按控件坐标平移视口，不修改原图几何。</summary>
    /// <param name = "dx">水平位移，向右为正，单位为控件像素或DIP。</param>
    /// <param name = "dy">垂直位移，向下为正，单位为控件像素或DIP。</param>
    public void Pan(double dx, double dy)
    {
        Origin = new PointD(Origin.X + dx, Origin.Y + dy);
    }

    /// <summary>将控件坐标转换为原图像素边缘坐标。</summary>
    /// <param name = "point">控件客户区坐标点。</param>
    /// <returns>对应的原图坐标，不会自动截断到图像边界。</returns>
    public PointD ToImage(PointD point)
    {
        return new PointD((point.X - Origin.X) / Scale, (point.Y - Origin.Y) / Scale);
    }

    /// <summary>计算客户区对应的可见原图范围。</summary>
    /// <param name = "width">客户区宽度，单位为控件像素或DIP。</param>
    /// <param name = "height">客户区高度，单位为控件像素或DIP。</param>
    /// <returns>原图坐标范围，可能延伸到图像之外。</returns>
    public RectD Visible(double width, double height)
    {
        var p = ToImage(new PointD(0, 0));
        return new RectD(p.X, p.Y, width / Scale, height / Scale);
    }
}
