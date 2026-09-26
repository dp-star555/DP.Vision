using System;
using System.Linq;
using System.Threading;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// 贪心k中心核心集：在随机投影后的低维空间里反复选取离已选集合最远的点，
/// 用较少的块覆盖全部良品块的多样性（罕见笔画形态不会像随机抽样那样被丢掉）。
/// </summary>
internal static class Coreset
{
    private const int ProjectedDimensions = 24;

    /// <summary>从行特征矩阵中选出至多k行的索引。</summary>
    /// <param name = "data">N×D的CV_32F特征矩阵。</param>
    /// <param name = "k">目标行数。</param>
    /// <param name = "seed">随机投影与起点的种子，保证可复现。</param>
    /// <param name = "token">协作式取消标记。</param>
    internal static int[] Select(Mat data, int k, int seed, CancellationToken token)
    {
        int n = data.Rows;
        if (n <= k)
        {
            var all = new int[n];
            for (int i = 0; i < n; i++)
            {
                all[i] = i;
            }

            return all;
        }

        var random = new Random(seed);
        // 先随机抽取至多3k个候选再做k中心，代价从N×k降到3k×k；罕见形态在3倍候选中仍大概率保留。
        int[] pool = Enumerable.Range(0, n).ToArray();
        int m = Math.Min(n, 3 * k);
        for (int i = 0; i < m; i++)
        {
            int j = i + random.Next(n - i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        using var candidates = new Mat(m, data.Cols, MatType.CV_32F);
        for (int i = 0; i < m; i++)
        {
            data.Row(pool[i]).CopyTo(candidates.Row(i));
        }

        n = m;
        using var projection = new Mat(data.Cols, ProjectedDimensions, MatType.CV_32F);
        for (int r = 0; r < projection.Rows; r++)
        {
            for (int c = 0; c < projection.Cols; c++)
            {
                double u1 = 1 - random.NextDouble(),
                    u2 = random.NextDouble();
                projection.Set(r, c, (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
            }
        }

        using var projected = new Mat();
        Cv2.Gemm(candidates, projection, 1, new Mat(), 0, projected);
        var nearest = new float[n];
        for (int i = 0; i < n; i++)
        {
            nearest[i] = float.MaxValue;
        }

        var selected = new int[k];
        int current = random.Next(n);
        // ‖p−c‖² = ‖p‖² − 2p·c + ‖c‖²：点积由OpenCV矩阵乘法（向量化）一次算出全部候选。
        projected.GetArray(out float[] flat);
        var norms = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = 0;
            for (int j = 0; j < ProjectedDimensions; j++)
            {
                float v = flat[i * ProjectedDimensions + j];
                t += v * v;
            }

            norms[i] = t;
        }

        using var dots = new Mat();
        var row = new float[n];
        for (int s = 0; s < k; s++)
        {
            if ((s & 255) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            selected[s] = pool[current];
            using (var center = projected.Row(current))
            {
                Cv2.Gemm(projected, center, 1, new Mat(), 0, dots, GemmFlags.B_T);
            }

            dots.GetArray(out float[] dot);
            float self = norms[current];
            for (int i = 0; i < n; i++)
            {
                row[i] = norms[i] - 2 * dot[i] + self;
            }

            int farthest = 0;
            float best = -1;
            for (int i = 0; i < n; i++)
            {
                if (row[i] < nearest[i])
                {
                    nearest[i] = row[i];
                }

                if (nearest[i] > best)
                {
                    best = nearest[i];
                    farthest = i;
                }
            }

            current = farthest;
        }

        return selected;
    }
}
