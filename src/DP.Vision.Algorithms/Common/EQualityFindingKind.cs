using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>算法证据角色，不是业务判定。</summary>
public enum EQualityFindingKind
{
    /// <summary>信息性覆盖或测量记录。</summary>
    Information,

    /// <summary>要求的检测未能完成。</summary>
    Blocker,

    /// <summary>实测质量异常。</summary>
    Defect,
}
