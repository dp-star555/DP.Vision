using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>为匹配结果准备模型轮廓（模板像素边界坐标）；只用于显示，不参与匹配。</summary>
public static class TemplateModelContours
{
    /// <summary>默认的轮廓点数上限，避免大模板让叠加显示卡顿。</summary>
    public const int DefaultPointLimit = 20000;

    /// <summary>
    /// 掩码有效区域的边界，按像素边界给出；同一行或同一列上连续的边界合并为一段，矩形掩码只有四段。
    /// </summary>
    /// <param name="mask">行优先掩码，非零为有效。</param><param name="width">宽。</param><param name="height">高。</param>
    /// <returns>两点线段组成的轮廓列表。</returns>
    public static IReadOnlyList<IReadOnlyList<Coordinate2D>> FromMask(byte[] mask, int width, int height)
    {
        if (mask == null) throw new ArgumentNullException(nameof(mask));
        if (width < 1 || height < 1 || mask.Length != (long)width * height) throw new ArgumentException("掩码尺寸不一致。");
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x] != 0;
        var result = new List<IReadOnlyList<Coordinate2D>>();
        // 水平边界：y 处的边界线位于第 y-1 行与第 y 行之间。
        for (int y = 0; y <= height; y++)
        {
            int start = -1;
            for (int x = 0; x <= width; x++)
            {
                bool edge = x < width && Inside(x, y - 1) != Inside(x, y);
                if (edge && start < 0) start = x;
                else if (!edge && start >= 0) { result.Add(new[] { new Coordinate2D(start, y), new Coordinate2D(x, y) }); start = -1; }
            }
        }
        // 垂直边界：x 处的边界线位于第 x-1 列与第 x 列之间。
        for (int x = 0; x <= width; x++)
        {
            int start = -1;
            for (int y = 0; y <= height; y++)
            {
                bool edge = y < height && Inside(x - 1, y) != Inside(x, y);
                if (edge && start < 0) start = y;
                else if (!edge && start >= 0) { result.Add(new[] { new Coordinate2D(x, start), new Coordinate2D(x, y) }); start = -1; }
            }
        }
        return result;
    }

    /// <summary>掩码是否覆盖整个模板矩形（此时边界与匹配框重合，不必重复显示）。</summary>
    /// <param name="mask">行优先掩码。</param>
    public static bool IsFull(byte[] mask) => mask != null && mask.All(v => v != 0);

    /// <summary>把总点数限制在 <paramref name="limit"/> 内：超出时对每条轮廓等间隔抽点，保留首尾。</summary>
    /// <param name="contours">原始轮廓。</param><param name="limit">总点数上限。</param>
    /// <returns>抽点后的轮廓，至少两点的才保留。</returns>
    public static IReadOnlyList<IReadOnlyList<Coordinate2D>> Limit(IEnumerable<IReadOnlyList<Coordinate2D>> contours, int limit = DefaultPointLimit)
    {
        if (contours == null) throw new ArgumentNullException(nameof(contours));
        if (limit < 2) throw new ArgumentOutOfRangeException(nameof(limit));
        var list = contours.Where(c => c != null && c.Count >= 2).ToArray();
        long total = list.Sum(c => (long)c.Count);
        if (total <= limit) return list;
        int stride = (int)Math.Ceiling(total / (double)limit);
        return list.Select(c =>
            {
                var kept = new List<Coordinate2D>(c.Count / stride + 2);
                for (int i = 0; i < c.Count; i += stride) kept.Add(c[i]);
                if ((c.Count - 1) % stride != 0) kept.Add(c[c.Count - 1]);
                return (IReadOnlyList<Coordinate2D>)kept;
            })
            .Where(c => c.Count >= 2).ToArray();
    }
}
