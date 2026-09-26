using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// 局部块特征：灰度按本图纸色（98%分位）换算为墨量（0纸白–1满墨），轻度平滑后取原尺度P×P块与1/2尺度同位置P×P块。
/// 按纸色归一化抵消整体亮度/增益变化，但整体变浅（褪色）仍保留为墨量下降；纯纸白块不参与记忆库和评分。
/// </summary>
internal static partial class PatchFeatures
{
    /// <summary>1/2尺度上下文块的权重。</summary>
    private const float ContextWeight = .7f;

    /// <summary>块内最大墨量低于此值视为纯纸白块。</summary>
    private const float BlankInk = .12f;

    internal static int Dimensions(int patchSize)
    {
        return 2 * patchSize * patchSize;
    }

    /// <summary>灰度图换算为原尺度与1/2尺度墨量平面。</summary>
    internal static (Plane Full, Plane Half) Prepare(Mat gray)
    {
        var histogram = new int[256];
        for (int y = 0; y < gray.Rows; y++)
        {
            for (int x = 0; x < gray.Cols; x++)
            {
                histogram[gray.At<byte>(y, x)]++;
            }
        }

        long target = (long)Math.Ceiling(gray.Rows * (long)gray.Cols * .98),
            seen = 0;
        int paper = 255;
        for (int v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen >= target)
            {
                paper = v;
                break;
            }
        }

        paper = Math.Max(paper, 20);
        using var ink = new Mat();
        gray.ConvertTo(ink, MatType.CV_32F, -1.0 / paper, 1.0);
        Cv2.Max(ink, 0, ink);
        Cv2.GaussianBlur(ink, ink, new Size(0, 0), .7);
        using var half = new Mat();
        Cv2.Resize(
            ink,
            half,
            new Size(Math.Max(1, ink.Cols / 2), Math.Max(1, ink.Rows / 2)),
            0,
            0,
            InterpolationFlags.Area
        );
        return (ToPlane(ink), ToPlane(half));
    }

    /// <summary>按步长提取块特征；<paramref name = "includeBlank"/>为false时跳过纯纸白块。</summary>
    internal static Set Extract(
        Plane full,
        Plane half,
        int patchSize,
        int stride,
        CancellationToken token,
        bool includeBlank = false
    )
    {
        var set = new Set(Dimensions(patchSize));
        var buffer = new float[set.Dimensions];
        for (int y = 0; y + patchSize <= full.Height; y += stride)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x + patchSize <= full.Width; x += stride)
            {
                if (Fill(full, half, x, y, patchSize, buffer) < BlankInk && !includeBlank)
                {
                    continue;
                }

                set.Values.AddRange(buffer);
                set.Corners.Add(new Point(x, y));
            }
        }

        return set;
    }

    /// <summary>填充左上角(x, y)处的块特征，返回块内最大墨量。</summary>
    internal static float Fill(Plane full, Plane half, int x, int y, int patchSize, float[] buffer)
    {
        float peak = 0;
        int k = 0;
        for (int dy = 0; dy < patchSize; dy++)
        {
            int row = (y + dy) * full.Width + x;
            for (int dx = 0; dx < patchSize; dx++)
            {
                float v = full.Data[row + dx];
                buffer[k++] = v;
                peak = Math.Max(peak, v);
            }
        }

        int cx = (x + patchSize / 2) / 2 - patchSize / 2,
            cy = (y + patchSize / 2) / 2 - patchSize / 2;
        for (int dy = 0; dy < patchSize; dy++)
        {
            for (int dx = 0; dx < patchSize; dx++)
            {
                float v = half.At(cx + dx, cy + dy);
                buffer[k++] = v * ContextWeight;
                peak = Math.Max(peak, v);
            }
        }

        return peak;
    }

    /// <summary>
    /// 查询块特征与参考平面上(x, y)处块的平方距离；累计超过<paramref name = "limit"/>即提前返回（位置相关检测的逐位置搜索）。
    /// </summary>
    internal static float SquaredDistance(
        float[] query,
        Plane full,
        Plane half,
        int x,
        int y,
        int patchSize,
        float limit
    )
    {
        float sum = 0;
        int k = 0;
        for (int dy = 0; dy < patchSize; dy++)
        {
            int row = (y + dy) * full.Width + x;
            for (int dx = 0; dx < patchSize; dx++)
            {
                float t = query[k++] - full.Data[row + dx];
                sum += t * t;
            }

            if (sum > limit)
            {
                return sum;
            }
        }

        int cx = (x + patchSize / 2) / 2 - patchSize / 2,
            cy = (y + patchSize / 2) / 2 - patchSize / 2;
        for (int dy = 0; dy < patchSize; dy++)
        {
            for (int dx = 0; dx < patchSize; dx++)
            {
                float t = query[k++] - half.At(cx + dx, cy + dy) * ContextWeight;
                sum += t * t;
            }
        }

        return sum;
    }

    private static Plane ToPlane(Mat m)
    {
        using var continuous = m.IsContinuous() ? m.Clone() : m.Clone();
        var data = new float[m.Rows * m.Cols];
        Marshal.Copy(continuous.Data, data, 0, data.Length);
        return new Plane(data, m.Cols, m.Rows);
    }
}
