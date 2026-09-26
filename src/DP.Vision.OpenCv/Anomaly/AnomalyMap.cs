using System.Collections.Generic;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// 异常得分图转为结果：边缘带只作上下文（不报异常、不计入最大得分和热力图），超阈值连通域为缺陷，热力图128对应阈值。两种特征实现共用。
/// </summary>
internal static class AnomalyMap
{
    /// <param name = "map">与裁图同尺寸的CV_32F得分图。</param>
    /// <param name = "threshold">阈值。</param>
    /// <param name = "border">裁图边缘只作上下文的宽度（像素）。</param>
    /// <param name = "minimumArea">异常区域最小面积。</param>
    /// <param name = "maximum">全部块的最大得分；裁图大于两倍边缘带时改用边缘带以外的最大得分，与异常区域、热力图一致。</param>
    /// <param name = "scope">说明信息。</param>
    internal static PatchAnomalyResult Result(
        Mat map,
        double threshold,
        int border,
        int minimumArea,
        double maximum,
        string scope
    )
    {
        using var scored = map.Clone();
        if (scored.Rows > 2 * border && scored.Cols > 2 * border)
        {
            scored.RowRange(0, border).SetTo(0);
            scored.RowRange(scored.Rows - border, scored.Rows).SetTo(0);
            scored.ColRange(0, border).SetTo(0);
            scored.ColRange(scored.Cols - border, scored.Cols).SetTo(0);
            Cv2.MinMaxLoc(scored, out _, out maximum);
        }

        var findings = new List<QualityFinding>();
        using var mask = new Mat();
        Cv2.Threshold(scored, mask, threshold, 255, ThresholdTypes.Binary);
        mask.ConvertTo(mask, MatType.CV_8U);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(
            mask,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8
        );
        for (int c = 1; c < count; c++)
        {
            int area = stats.At<int>(c, (int)ConnectedComponentsTypes.Area);
            if (area < minimumArea)
            {
                continue;
            }

            var bounds = new PixelBounds(
                stats.At<int>(c, (int)ConnectedComponentsTypes.Left),
                stats.At<int>(c, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(c, (int)ConnectedComponentsTypes.Width),
                stats.At<int>(c, (int)ConnectedComponentsTypes.Height)
            );
            using var region = new Mat(scored, CvPixels.Rect(bounds));
            Cv2.MinMaxLoc(region, out _, out double peak);
            findings.Add(
                new QualityFinding(
                    "patch_anomaly",
                    $"局部异常：最大得分{peak:F3}（阈值{threshold:F3}，{peak / threshold:F2}倍），面积{area}像素²；与良品中所有局部块都不相似。",
                    EQualityFindingKind.Defect,
                    bounds,
                    area
                )
            );
        }

        findings.Add(new QualityFinding("patch_anomaly_scope", scope, EQualityFindingKind.Information));
        using var heat = new Mat();
        scored.ConvertTo(heat, MatType.CV_8U, 128.0 / threshold);
        using var heatImage = CvPixels.Buffer(heat);
        return new PatchAnomalyResult(EAlgorithmStatus.Completed, maximum, threshold, findings, heatImage);
    }
}
