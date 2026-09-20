using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.Vision;

/// <summary>把几何转换为按像素中心采样的精确Region，不依赖编辑器或UI。</summary>
public static class RegionRasterizer
{
    /// <summary>
    /// 根据原图像素中心的成员关系创建Region：矩形采用半开边界，椭圆及填充多边形包含边界。
    /// 拒绝越界形状及开放/未填充轮廓，不静默裁剪或填充。
    /// </summary>
    /// <param name = "shape">矩形、椭圆、显式填充的闭合轮廓或已有Region快照。</param>
    /// <param name = "imageWidth">原图宽度，必须大于0，单位为像素。</param>
    /// <param name = "imageHeight">原图高度，必须大于0，单位为像素。</param>
    /// <param name = "maximumWork">工作量上限：采样像素数乘轮廓顶点数，或复制的Region游程数；必须大于0。</param>
    /// <param name = "token">协作式取消标记，取消时不返回部分区域。</param>
    /// <returns>独立不可变的Region；不包含任何像素中心的微小形状可得到空Region。</returns>
    public static RegionGeometry Rasterize(
        Geometry shape,
        int imageWidth,
        int imageHeight,
        long maximumWork = 16777216,
        CancellationToken token = default
    )
    {
        if (shape == null)
        {
            throw new ArgumentNullException(nameof(shape));
        }

        if (imageWidth < 1 || imageHeight < 1 || maximumWork < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        }

        token.ThrowIfCancellationRequested();
        var bounds = shape.Bounds;
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Right > imageWidth || bounds.Bottom > imageHeight)
        {
            throw new ArgumentException(
                "形状超出原图范围；如需裁去越界部分，请在转换前显式处理。",
                nameof(shape)
            );
        }

        if (bounds.Right > 1000000 || bounds.Bottom > 1000001)
        {
            throw new ArgumentException("形状超出像素区域游程支持的坐标范围。", nameof(shape));
        }

        var runs = new List<RegionRun>();
        if (shape is RegionGeometry existing)
        {
            if (existing.Runs.Count > maximumWork)
            {
                throw new ArgumentException("像素区域的游程数量超过转换工作量上限。", nameof(maximumWork));
            }

            foreach (var run in existing.Runs)
            {
                token.ThrowIfCancellationRequested();
                runs.Add(run);
            }

            return new RegionGeometry(runs);
        }

        int cost = 1;
        if (shape is ContourGeometry contour)
        {
            if (!contour.Closed || !contour.Filled || contour.Points.Count < 3)
            {
                throw new ArgumentException(
                    "只有明确填充、已闭合且至少包含三个顶点的轮廓才能转换为像素区域。",
                    nameof(shape)
                );
            }

            cost = contour.Points.Count;
        }
        else if (!(shape is RectangleGeometry) && !(shape is EllipseGeometry))
        {
            throw new NotSupportedException("不支持将此几何类型转换为像素区域。");
        }

        int left = (int)Math.Floor(bounds.X),
            top = (int)Math.Floor(bounds.Y),
            right = (int)Math.Ceiling(bounds.Right),
            bottom = (int)Math.Ceiling(bounds.Bottom);
        long pixels = (long)(right - left) * (bottom - top);
        if (pixels > maximumWork / cost)
        {
            throw new ArgumentException("形状的采样计算量超过转换工作量上限。", nameof(maximumWork));
        }

        for (int y = top; y < bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            int start = -1;
            for (int x = left; x < right; x++)
            {
                token.ThrowIfCancellationRequested();
                bool inside = shape.Contains(new PointD(x + .5, y + .5));
                if (inside && start < 0)
                {
                    start = x;
                }

                if (!inside && start >= 0)
                {
                    runs.Add(new RegionRun(y, start, x));
                    start = -1;
                }
            }

            if (start >= 0)
            {
                runs.Add(new RegionRun(y, start, right));
            }
        }

        token.ThrowIfCancellationRequested();
        return new RegionGeometry(runs);
    }
}
