using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>仅用于参考制作的有界垂直切分候选，不是识别或生产质量规则。</summary>
internal static class ThinBridgeCandidates
{
    internal static List<Tuple<int, int>>? Split(
        Mat mask,
        List<Tuple<int, int>> original,
        int target,
        CancellationToken token
    )
    {
        if (original.Count < 3 || target <= original.Count || target - original.Count > 4)
        {
            return null;
        }

        var widths = original.Select(r => r.Item2 - r.Item1).OrderBy(w => w).ToArray();
        double typical = widths[widths.Length / 2];
        if (typical < 4)
        {
            return null;
        }

        var ink = new int[mask.Cols];
        var top = Enumerable.Repeat(mask.Rows, mask.Cols).ToArray();
        var bottom = new int[mask.Cols];
        for (int y = 0; y < mask.Rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < mask.Cols; x++)
            {
                if (mask.At<byte>(y, x) != 0)
                {
                    ink[x]++;
                    top[x] = Math.Min(top[x], y);
                    bottom[x] = y + 1;
                }
            }
        }

        var runs = original.ToList();
        int work = 0;
        int minimumWidth = Math.Max(4, (int)Math.Ceiling(typical * .4));
        while (runs.Count < target)
        {
            token.ThrowIfCancellationRequested();
            int bestRun = -1,
                bestCut = -1;
            double bestCost = double.MaxValue;
            for (int r = 0; r < runs.Count; r++)
            {
                int left = runs[r].Item1,
                    right = runs[r].Item2;
                if (right - left < typical * 1.65 || right - left > typical * 4)
                {
                    continue;
                }

                int minY = mask.Rows,
                    maxY = 0;
                for (int x = left; x < right; x++)
                {
                    minY = Math.Min(minY, top[x]);
                    maxY = Math.Max(maxY, bottom[x]);
                }

                int height = maxY - minY;
                if (height < 8)
                {
                    continue;
                }

                int allowance = Math.Max(1, (int)Math.Floor(height * .15));
                int radius = Math.Max(3, (int)Math.Ceiling(typical * .4));
                for (int cut = left + minimumWidth; cut <= right - minimumWidth; cut++)
                {
                    // 边界位于两列之间；边界两列都须稀疏，且两侧具有足够笔画。
                    if (ink[cut - 1] > allowance || ink[cut] > allowance)
                    {
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    work += right - left + 2 * radius;
                    if (work > 2000000)
                    {
                        return null;
                    }

                    int lPeak = 0,
                        rPeak = 0;
                    for (int x = Math.Max(left, cut - radius); x < cut; x++)
                    {
                        lPeak = Math.Max(lPeak, ink[x]);
                    }

                    for (int x = cut; x < Math.Min(right, cut + radius); x++)
                    {
                        rPeak = Math.Max(rPeak, ink[x]);
                    }

                    if (lPeak < height * .45 || rPeak < height * .45)
                    {
                        continue;
                    }

                    int lt = mask.Rows,
                        lb = 0,
                        rt = mask.Rows,
                        rb = 0,
                        la = 0,
                        ra = 0;
                    for (int x = left; x < cut; x++)
                    {
                        lt = Math.Min(lt, top[x]);
                        lb = Math.Max(lb, bottom[x]);
                        la += ink[x];
                    }

                    for (int x = cut; x < right; x++)
                    {
                        rt = Math.Min(rt, top[x]);
                        rb = Math.Max(rb, bottom[x]);
                        ra += ink[x];
                    }

                    if (
                        lb - lt < height * .65
                        || rb - rt < height * .65
                        || Math.Min(la, ra) < (la + ra) * .15
                    )
                    {
                        continue;
                    }

                    double cost =
                        (ink[cut - 1] + ink[cut]) / (double)height
                        + .01 * Math.Abs((cut - left) - (right - cut)) / (right - left);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestRun = r;
                        bestCut = cut;
                    }
                }
            }

            if (bestRun < 0)
            {
                return null;
            }

            var run = runs[bestRun];
            runs[bestRun] = Tuple.Create(run.Item1, bestCut);
            runs.Insert(bestRun + 1, Tuple.Create(bestCut, run.Item2));
        }

        return runs;
    }
}
