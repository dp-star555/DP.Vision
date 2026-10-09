using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>中立像素得分图的阈值/连通区域和显示转换，不改变厂商原始得分单位。</summary>
public static class AnomalyScoreMap
{
    /// <summary>把输入局部坐标得分转换为缺陷证据及热力图。</summary>
    /// <param name="scores">按行排列的有限非负像素得分。</param>
    /// <param name="width">输入宽度。</param>
    /// <param name="height">输入高度。</param>
    /// <param name="threshold">本模型像素阈值。</param>
    /// <param name="minimumArea">最小连通面积。</param>
    /// <param name="description">真实模型/标定说明。</param>
    public static PatchAnomalyResult Measure(float[] scores, int width, int height, double threshold, int minimumArea, string description)
    {
        if (width < 1 || height < 1 || (long)width * height > 16777216 || scores == null || scores.Length != (long)width * height
            || !(threshold > 0) || double.IsInfinity(threshold) || minimumArea < 1 || scores.Any(s => float.IsNaN(s) || float.IsInfinity(s) || s < 0))
            throw new ArgumentException("异常像素图/阈值无效。");
        var visited = new byte[scores.Length]; var heat = new byte[scores.Length]; var queue = new Queue<int>();
        var findings = new List<QualityFinding>(); double maximum = 0;
        for (int i = 0; i < scores.Length; i++) { maximum = Math.Max(maximum, scores[i]); heat[i] = (byte)Math.Min(255, scores[i] / threshold * 128); }
        for (int start = 0; start < scores.Length; start++)
        {
            if (visited[start] != 0 || scores[start] <= threshold) continue;
            visited[start] = 1; queue.Enqueue(start); int area = 0, x0 = width, y0 = height, x1 = 0, y1 = 0; double peak = 0;
            while (queue.Count != 0)
            {
                int i = queue.Dequeue(), x = i % width, y = i / width; area++; peak = Math.Max(peak, scores[i]);
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                    int j = yy * width + xx; if (visited[j] == 0 && scores[j] > threshold) { visited[j] = 1; queue.Enqueue(j); }
                }
            }
            if (area >= minimumArea) findings.Add(new QualityFinding("patch_anomaly", $"异常区域：得分{peak:F3}，阈值{threshold:F3}（{peak / threshold:F2}倍），面积{area}像素²。",
                EQualityFindingKind.Defect, new PixelBounds(x0, y0, x1 - x0 + 1, y1 - y0 + 1), area));
        }
        findings.Add(new QualityFinding("patch_anomaly_scope", description, EQualityFindingKind.Information));
        using var image = VisionImage.CopyFrom(new ImageInfo(width, height, EPixelLayout.Gray8), heat);
        return new PatchAnomalyResult(EAlgorithmStatus.Completed, maximum, threshold, findings, image);
    }
}
