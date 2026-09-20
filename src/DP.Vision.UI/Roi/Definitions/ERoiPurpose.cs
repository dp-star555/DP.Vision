using System;
using System.Threading;

namespace DP.Vision.UI;

/// <summary>编辑器中的区域意图，由宿主映射到后台检查或排除配置。</summary>
public enum ERoiPurpose
{
    /// <summary>检查区域内部。</summary>
    Include,

    /// <summary>排除区域内部。</summary>
    Exclude,
}
