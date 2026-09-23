using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>显式的印刷检查完成状态；未要求的空检查不能通过。</summary>
public sealed class BarcodeQualityResult
{
    /// <summary>复制证据快照；只要存在阻断，无论缺陷数量多少都不算完整完成。</summary>
    /// <param name = "requested">本轮是否明确要求印刷检查。</param>
    /// <param name = "findings">测量和阻断记录，内部复制；阻断会使完成状态失效。</param>
    public BarcodeQualityResult(bool requested, IEnumerable<QualityFinding> findings)
    {
        Findings = Array.AsReadOnly(findings.ToArray());
        Status =
            !requested ? EAlgorithmStatus.NotRequested
            : Findings.Any(f => f.Kind == EQualityFindingKind.Blocker) ? EAlgorithmStatus.InsufficientEvidence
            : EAlgorithmStatus.Completed;
    }

    /// <summary>执行状态。</summary>
    public EAlgorithmStatus Status { get; }

    /// <summary>全部测量及阻断诊断记录。</summary>
    public IReadOnlyList<QualityFinding> Findings { get; }

    /// <summary>仅在完整执行且没有缺陷时为true。</summary>
    public bool Passed =>
        Status == EAlgorithmStatus.Completed && !Findings.Any(f => f.Kind == EQualityFindingKind.Defect);
}
