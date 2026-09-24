using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>具有工作量上限的显示专用轮廓简化；闭合、填充轮廓保持原始点，避免拓扑变化。</summary>
public static class ContourLod
{
    /// <summary>生成保留端点的显示点序列；达到工作预算时返回原始点，不输出未经保证的简化结果。</summary>
    /// <param name = "contour">不可变原始轮廓，不会被修改。</param>
    /// <param name = "tolerance">允许的顶点到简化线段距离，单位为原图像素；0禁用简化。</param>
    /// <param name = "maximumDistanceChecks">距离计算次数上限，必须大于0。</param>
    /// <returns>只读显示点集合；不可替代原始轮廓参与检测。</returns>
    public static IReadOnlyList<PointD> Simplify(
        ContourGeometry contour,
        double tolerance,
        int maximumDistanceChecks = 4000000
    )
    {
        if (contour == null)
        {
            throw new ArgumentNullException(nameof(contour), "轮廓不能为空。");
        }

        if (!PointD.Valid(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "简化容差必须是非负有限值。");
        }

        if (maximumDistanceChecks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDistanceChecks), "距离计算次数上限必须大于0。");
        }

        var p = contour.Points;
        if (tolerance == 0 || contour.Closed || p.Count < 3)
        {
            return p;
        }

        var keep = new bool[p.Count];
        keep[0] = keep[p.Count - 1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, p.Count - 1));
        int checks = 0;
        while (stack.Count > 0)
        {
            var range = stack.Pop();
            double max = tolerance * tolerance;
            int index = -1;
            for (int i = range.First + 1; i < range.Last; i++)
            {
                if (++checks > maximumDistanceChecks)
                {
                    return p;
                }

                double d = GeometryMath.DistanceSquared(p[i], p[range.First], p[range.Last]);
                if (d > max)
                {
                    max = d;
                    index = i;
                }
            }

            if (index >= 0)
            {
                keep[index] = true;
                stack.Push((range.First, index));
                stack.Push((index, range.Last));
            }
        }

        return Array.AsReadOnly(p.Where((v, i) => keep[i]).ToArray());
    }
}
