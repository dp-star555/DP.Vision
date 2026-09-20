using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>不依赖具体图形API的共享图块选择与LOD计算。</summary>
public static class CanvasPlanning
{
    /// <summary>选择可见的分级图块，不要求生成整图显示位图。</summary>
    /// <param name = "image">原图尺寸与布局。</param>
    /// <param name = "view">当前缩放和平移视口。</param>
    /// <param name = "width">客户区宽度，单位为控件像素或DIP。</param>
    /// <param name = "height">客户区高度，单位为控件像素或DIP。</param>
    /// <param name = "tileSize">当前级别图块边长，单位为像素。</param>
    /// <returns>与原图相交的可见图块请求集合。</returns>
    public static IReadOnlyList<TileRequest> Tiles(
        ImageInfo image,
        CanvasViewport view,
        double width,
        double height,
        int tileSize
    )
    {
        if (image == null || view == null)
        {
            throw new ArgumentNullException(nameof(image), "原图信息和视口均不能为空。");
        }

        if (
            tileSize < 16
            || tileSize > 1024
            || !PointD.Valid(width)
            || !PointD.Valid(height)
            || width < 0
            || height < 0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileSize),
                "图块边长必须在16～1024之间，客户区宽高必须为0～10000000之间的有限值。"
            );
        }

        int level = Math.Max(0, Math.Min(20, (int)Math.Floor(Math.Log(1 / view.Scale, 2))));
        int factor = 1 << level;
        double edge = (double)tileSize * factor;
        var visible = view.Visible(width, height);
        int left = Math.Max(0, (int)Math.Floor(visible.X / edge)),
            top = Math.Max(0, (int)Math.Floor(visible.Y / edge));
        int right = Math.Min((int)Math.Ceiling(image.Width / edge), (int)Math.Ceiling(visible.Right / edge)),
            bottom = Math.Min(
                (int)Math.Ceiling(image.Height / edge),
                (int)Math.Ceiling(visible.Bottom / edge)
            );
        var tiles = new List<TileRequest>();
        if (right <= left || bottom <= top)
        {
            return tiles;
        }

        if ((long)(right - left) * (bottom - top) > 4096)
        {
            throw new InvalidOperationException(
                "当前视口需要的图块数量超过单次请求上限4096，请调整视口或图块大小。"
            );
        }

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                tiles.Add(
                    new TileRequest(
                        level,
                        x,
                        y,
                        new RectD(
                            x * edge,
                            y * edge,
                            Math.Min(edge, image.Width - x * edge),
                            Math.Min(edge, image.Height - y * edge)
                        )
                    )
                );
            }
        }

        return tiles;
    }

    /// <summary>返回保守量化后的原图LOD容差；1:1及放大显示时保持精确几何。</summary>
    /// <param name = "options">显示策略中的LOD开关和屏幕误差限制。</param>
    /// <param name = "scale">每个原图像素对应的控件单位数，必须为有效缩放比例。</param>
    /// <returns>原图像素单位的简化容差；0表示不简化。</returns>
    public static double LodTolerance(CanvasOptions options, double scale)
    {
        return !options.ContourLod || scale >= 1
            ? 0
            : Math.Pow(2, Math.Floor(Math.Log(options.MaximumScreenError / scale, 2)));
    }
}
