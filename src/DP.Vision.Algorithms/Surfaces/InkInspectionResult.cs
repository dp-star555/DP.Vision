using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>显式完成状态及不可变测量集合；缺陷集合为空不能单独证明检测成功。</summary>
public sealed class InkInspectionResult
{
    /// <summary>复制证据；未完成结果必须给出可处理的原因。</summary>
    /// <param name = "status">显式算法完成状态。</param>
    /// <param name = "reasonCode">未完成时的稳定阻断代码。</param>
    /// <param name = "reason">面向人的阻断原因。</param>
    /// <param name = "defects">局部缺陷集合，内部复制，不用空集合代替完成状态。</param>
    public InkInspectionResult(
        EAlgorithmStatus status,
        string reasonCode,
        string reason,
        IEnumerable<InkDefect> defects
    )
    {
        if (!Enum.IsDefined(typeof(EAlgorithmStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (reasonCode == null || reason == null || defects == null)
        {
            throw new ArgumentNullException(nameof(defects));
        }

        if (
            status != EAlgorithmStatus.Completed
            && (string.IsNullOrWhiteSpace(reasonCode) || string.IsNullOrWhiteSpace(reason))
        )
        {
            throw new ArgumentException("An incomplete measurement needs a reason.");
        }

        var copy = defects.ToArray();
        if (copy.Any(d => d == null))
        {
            throw new ArgumentException("Null defect.", nameof(defects));
        }

        Status = status;
        ReasonCode = reasonCode;
        Reason = reason;
        Defects = Array.AsReadOnly(copy);
    }

    /// <summary>执行完成状态，与缺陷数量无关。</summary>
    public EAlgorithmStatus Status { get; }

    /// <summary>稳定的阻断诊断代码，测量完成时为空。</summary>
    public string ReasonCode { get; }

    /// <summary>面向人的阻断原因说明。</summary>
    public string Reason { get; }

    /// <summary>返回的全部局部测量记录。</summary>
    public IReadOnlyList<InkDefect> Defects { get; }

    /// <summary>仅在按传入设置完整执行且无缺陷时为true。</summary>
    public bool Passed => Status == EAlgorithmStatus.Completed && Defects.Count == 0;
}
