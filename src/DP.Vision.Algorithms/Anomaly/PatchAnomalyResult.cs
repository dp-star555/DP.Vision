using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>局部块异常检测结果：异常区域、最大得分与热力图。</summary>
public sealed class PatchAnomalyResult : IDisposable
{
    /// <summary>创建结果并持有热力图租约。</summary>
    /// <param name = "status">测量完成状态。</param>
    /// <param name = "maximumScore">全图最大块得分（到最近良品块的特征距离）。</param>
    /// <param name = "threshold">本次使用的阈值。</param>
    /// <param name = "findings">异常区域（缺陷）及诊断记录，内部复制。</param>
    /// <param name = "heatMap">与输入同尺寸的灰度热力图：128对应阈值，255为2倍阈值及以上；可为空。</param>
    public PatchAnomalyResult(
        EAlgorithmStatus status,
        double maximumScore,
        double threshold,
        IEnumerable<QualityFinding> findings,
        IImageSource? heatMap
    )
    {
        Status = status;
        MaximumScore = maximumScore;
        Threshold = threshold;
        Findings = Array.AsReadOnly((findings ?? Array.Empty<QualityFinding>()).ToArray());
        HeatMap = heatMap?.Retain();
    }

    /// <summary>测量完成状态。</summary>
    public EAlgorithmStatus Status { get; }

    /// <summary>全图最大块得分。</summary>
    public double MaximumScore { get; }

    /// <summary>本次使用的阈值。</summary>
    public double Threshold { get; }

    /// <summary>异常区域及诊断记录。</summary>
    public IReadOnlyList<QualityFinding> Findings { get; }

    /// <summary>借用的热力图（128=阈值）；超出本结果生命周期使用时须另行Retain。</summary>
    public IImageSource? HeatMap { get; }

    /// <summary>完整执行且没有异常区域时为true。</summary>
    public bool Passed =>
        Status == EAlgorithmStatus.Completed && !Findings.Any(f => f.Kind == EQualityFindingKind.Defect);

    /// <summary>释放热力图租约。</summary>
    public void Dispose()
    {
        HeatMap?.Dispose();
    }
}
