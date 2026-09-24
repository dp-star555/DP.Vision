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

        // 游程排他右端最大为MaxDimension；最底行行号最大为MaxDimension，所以下边界可达MaxDimension+1。
        if (bounds.Right > ImageInfo.MaxDimension || bounds.Bottom > ImageInfo.MaxDimension + 1)
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

        var contourRows = shape is ContourGeometry filled ? new ContourScanline(filled, left, right) : null;
        for (int y = top; y < bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            contourRows?.BeginRow(y);
            int start = -1;
            for (int x = left; x < right; x++)
            {
                bool inside = contourRows?.Contains(x) ?? shape.Contains(new PointD(x + .5, y + .5));
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

    /// <summary>
    /// 填充轮廓的逐行扫描：每行只求一次各边与像素中心线的交点，再按与
    /// <see cref="ContourGeometry.Contains"/>相同的奇偶规则判定整行，代价从“像素数×顶点数”降为“行数×顶点数+像素数”。
    /// Contains把恰好落在边上的点算作内部；这类点只可能出现在交点、中心线上的顶点及位于中心线上的水平边附近，
    /// 这些列标为“临界列”并直接调用Contains判定，因此结果与逐像素调用Contains完全一致。
    /// </summary>
    private sealed class ContourScanline
    {
        private readonly ContourGeometry _contour;
        private readonly int _left;
        private readonly bool[] _critical;
        private readonly List<double> _crossings = new List<double>();
        private int _row;
        private int _passed;

        internal ContourScanline(ContourGeometry contour, int left, int right)
        {
            _contour = contour;
            _left = left;
            _critical = new bool[right - left];
        }

        internal void BeginRow(int row)
        {
            _row = row;
            _passed = 0;
            _crossings.Clear();
            Array.Clear(_critical, 0, _critical.Length);
            double y = row + .5;
            var points = _contour.Points;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if ((a.Y > y) != (b.Y > y))
                {
                    // 与ContourGeometry.Contains逐字相同的交点表达式，保证浮点结果一致。
                    double crossing = (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X;
                    _crossings.Add(crossing);
                    MarkCritical(crossing, crossing);
                }

                if (a.Y == y)
                {
                    MarkCritical(a.X, b.Y == y ? b.X : a.X);
                }
            }

            _crossings.Sort();
        }

        /// <summary>列必须从左到右依次查询。</summary>
        internal bool Contains(int x)
        {
            double center = x + .5;
            while (_passed < _crossings.Count && _crossings[_passed] <= center)
            {
                _passed++;
            }

            return _critical[x - _left]
                ? _contour.Contains(new PointD(center, _row + .5))
                : ((_crossings.Count - _passed) & 1) == 1;
        }

        private void MarkCritical(double from, double to)
        {
            double low = Math.Min(from, to),
                high = Math.Max(from, to);
            long first = Math.Max((long)Math.Floor(low) - 2, _left),
                last = Math.Min((long)Math.Floor(high) + 2, _left + _critical.Length - 1L);
            for (long x = first; x <= last; x++)
            {
                _critical[x - _left] = true;
            }
        }
    }
}
