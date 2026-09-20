using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>所要求的测量是否确实完成，不是标签业务判定。</summary>
public enum EAlgorithmStatus
{
    /// <summary>全部要求的测量均已完成。</summary>
    Completed,

    /// <summary>实现无法处理所提供的输入。</summary>
    UnsupportedInput,

    /// <summary>输入有效，但证据不足以完成测量。</summary>
    InsufficientEvidence,

    /// <summary>明确未要求执行该操作，不代表测量成功。</summary>
    NotRequested,
}
