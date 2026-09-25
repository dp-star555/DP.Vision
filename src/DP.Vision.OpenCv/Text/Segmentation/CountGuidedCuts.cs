using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// 已知字符数时的整行最优切割：投影空白与连通域数量对不上（粘连、断字、标点）时，
/// 在候选切线中选择使“切过的墨迹”和“各字宽度偏离预期”总代价最小的一组切线。
/// 断开的字不会被拆散，粘连处在最薄的连接处切开，切过墨迹的位置逐一报告。
/// </summary>
internal static class CountGuidedCuts
{
    /// <summary>切线穿过整字高墨迹的代价，相对宽度偏差平方的权重为1。</summary>
    private const double CutWeight = 4;

    /// <summary>一次切割的结果：每字的列范围，以及切过墨迹的切线数和最大穿墨比例。</summary>
    internal sealed class Result
    {
        internal Result(
            List<Tuple<int, int>> pieces,
            int bridgedCuts,
            double worstCrossing,
            double worstWidth
        )
        {
            Pieces = pieces;
            BridgedCuts = bridgedCuts;
            WorstCrossing = worstCrossing;
            WorstWidth = worstWidth;
        }

        /// <summary>每个字符的[左, 右)列范围（ROI坐标），相邻字符共用切线。</summary>
        internal List<Tuple<int, int>> Pieces { get; }

        /// <summary>穿过墨迹的切线数。</summary>
        internal int BridgedCuts { get; }

        /// <summary>单条切线穿过墨迹的最大比例（相对墨迹行高）。</summary>
        internal double WorstCrossing { get; }

        /// <summary>单字墨迹宽度与预期宽度之比偏离1的最大值。</summary>
        internal double WorstWidth { get; }
    }

    /// <summary>
    /// 近似等线无衬线字体的相对字宽：窄字符、标点与宽字符分别加权；未知字符按1处理。
    /// 只作切割先验，不要求与实际字体一致。
    /// </summary>
    internal static double RelativeWidth(char c)
    {
        if ("Iil1|!.,:;'`".IndexOf(c) >= 0)
        {
            return .35;
        }

        if ("()[]{}-".IndexOf(c) >= 0)
        {
            return .5;
        }

        if ("MWmw".IndexOf(c) >= 0)
        {
            return 1.3;
        }

        return 1;
    }

    /// <param name = "ink">已去除噪点的行墨迹二值图（墨迹为非零）。</param>
    /// <param name = "tokens">按顺序排列的非空白字符。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>找不到每字都有墨迹的切割时返回null。</returns>
    internal static Result? Cut(Mat ink, IReadOnlyList<char> tokens, CancellationToken token)
    {
        int n = tokens.Count,
            width = ink.Cols,
            height = ink.Rows;
        var columnInk = new int[width];
        var crossing = new int[width + 1];
        int top = height,
            bottom = 0;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                if (ink.At<byte>(y, x) == 0)
                {
                    continue;
                }

                columnInk[x]++;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + 1);
                // 切线位于第x列左侧：左右两列同一行都有墨迹时，这一行墨迹被切断。
                if (x > 0 && ink.At<byte>(y, x - 1) != 0)
                {
                    crossing[x]++;
                }
            }
        }

        int left = Array.FindIndex(columnInk, v => v > 0),
            right = Array.FindLastIndex(columnInk, v => v > 0) + 1;
        if (n == 0 || left < 0 || right - left < n)
        {
            return null;
        }

        double inkHeight = Math.Max(1, bottom - top);
        // 字宽单位：有墨迹的列数按字宽先验分摊，行内空格和字间空白不计入。
        double unit = columnInk.Count(v => v > 0) / tokens.Sum(RelativeWidth);
        var prefix = new int[width + 1];
        for (int x = 0; x < width; x++)
        {
            prefix[x + 1] = prefix[x] + (columnInk[x] > 0 ? 1 : 0);
        }

        // 候选切线：每段空白取中点；墨迹段内取穿墨局部极小处（粘连最薄处）。
        var candidates = new List<int> { left };
        for (int x = left + 1; x < right; )
        {
            if (columnInk[x] == 0)
            {
                int start = x;
                while (x < right && columnInk[x] == 0)
                {
                    x++;
                }

                candidates.Add((start + x) / 2);
                continue;
            }

            if (
                columnInk[x - 1] > 0
                && crossing[x] <= crossing[x - 1]
                && (x + 1 >= right || crossing[x] <= crossing[x + 1])
            )
            {
                candidates.Add(x);
            }

            x++;
        }

        candidates.Add(right);
        candidates = candidates.Distinct().OrderBy(v => v).ToList();
        int m = candidates.Count;
        if (m - 1 < n)
        {
            return null;
        }

        (int, int)? InkSpan(int a, int b)
        {
            if (prefix[b] - prefix[a] == 0)
            {
                return null;
            }

            int s = a;
            while (columnInk[s] == 0)
            {
                s++;
            }

            int e = b;
            while (columnInk[e - 1] == 0)
            {
                e--;
            }

            return (s, e);
        }

        double CutCost(int x)
        {
            return x == left || x == right ? 0 : CutWeight * crossing[x] / inkHeight;
        }

        var cost = new double[n + 1, m];
        var back = new int[n + 1, m];
        for (int k = 0; k <= n; k++)
        {
            for (int j = 0; j < m; j++)
            {
                cost[k, j] = double.PositiveInfinity;
            }
        }

        cost[0, 0] = 0;
        for (int k = 1; k <= n; k++)
        {
            token.ThrowIfCancellationRequested();
            double expected = RelativeWidth(tokens[k - 1]) * unit;
            for (int j = k; j < m - (n - k); j++)
            {
                double best = double.PositiveInfinity;
                int from = -1;
                for (int i = j - 1; i >= k - 1; i--)
                {
                    if (double.IsPositiveInfinity(cost[k - 1, i]))
                    {
                        continue;
                    }

                    var span = InkSpan(candidates[i], candidates[j]);
                    if (span == null)
                    {
                        continue;
                    }

                    double inkWidth = span.Value.Item2 - span.Value.Item1;
                    if (inkWidth > 3 * expected + 2)
                    {
                        // 更早的起点只会更宽。
                        break;
                    }

                    double deviation = (inkWidth - expected) / expected;
                    double value = cost[k - 1, i] + deviation * deviation + CutCost(candidates[j]);
                    if (value < best)
                    {
                        best = value;
                        from = i;
                    }
                }

                cost[k, j] = best;
                back[k, j] = from;
            }
        }

        if (double.IsPositiveInfinity(cost[n, m - 1]))
        {
            return null;
        }

        var cuts = new int[n + 1];
        for (int k = n, j = m - 1; k >= 0; k--)
        {
            cuts[k] = j;
            j = k > 0 ? back[k, j] : 0;
        }

        var pieces = new List<Tuple<int, int>>();
        int bridged = 0;
        double worstCrossing = 0,
            worstWidth = 0;
        for (int k = 0; k < n; k++)
        {
            int a = candidates[cuts[k]],
                b = candidates[cuts[k + 1]];
            pieces.Add(Tuple.Create(a, b));
            var span = InkSpan(a, b)!.Value;
            double expected = RelativeWidth(tokens[k]) * unit;
            worstWidth = Math.Max(worstWidth, Math.Abs((span.Item2 - span.Item1) / expected - 1));
            if (k > 0 && crossing[a] > 0)
            {
                bridged++;
                worstCrossing = Math.Max(worstCrossing, crossing[a] / inkHeight);
            }
        }

        return new Result(pieces, bridged, worstCrossing, worstWidth);
    }
}
