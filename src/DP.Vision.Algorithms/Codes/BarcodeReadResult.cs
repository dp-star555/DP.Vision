using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>实际读取完成状态及保留的不可变观测记录。</summary>
public sealed class BarcodeReadResult
{
    /// <summary>复制观测集合；恰好一个结果才属于无歧义的完整读取。</summary>
    /// <param name = "observations">实际观测集合，内部复制；零个或多个结果均不算无歧义完成。</param>
    public BarcodeReadResult(IEnumerable<BarcodeObservation> observations)
    {
        Observations = Array.AsReadOnly(observations.ToArray());
        Status = Observations.Count == 1 ? EAlgorithmStatus.Completed : EAlgorithmStatus.InsufficientEvidence;
        ReasonCode =
            Observations.Count == 0 ? "not_decoded"
            : Observations.Count > 1 ? "ambiguous"
            : "";
    }

    /// <summary>显式完成状态。</summary>
    public EAlgorithmStatus Status { get; }

    /// <summary>存在阻断时的原因。</summary>
    public string ReasonCode { get; }

    /// <summary>全部实际读取记录。</summary>
    public IReadOnlyList<BarcodeObservation> Observations { get; }
}
