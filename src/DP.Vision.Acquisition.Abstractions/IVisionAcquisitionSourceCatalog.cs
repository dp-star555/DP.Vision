using System.Collections.Generic;

namespace DP.Vision.Acquisition;

/// <summary>
/// 已发布逻辑源目录投影；由不可变Composition直接提供，宿主不再从原始绑定手工构造第二份目录。
/// 工作流文档只保存SourceId，本目录负责把它映射到当前机器上的Provider。
/// </summary>
public interface IVisionAcquisitionSourceCatalog
{
    /// <summary>全部已发布逻辑源条目，按SourceId排序；不可用源也出现并带诊断。</summary>
    IReadOnlyList<VisionAcquisitionSourceInfo> SourceCatalog { get; }

    /// <summary>按逻辑源标识查找。</summary>
    /// <param name="sourceId">逻辑源标识。</param>
    /// <param name="source">找到的条目。</param>
    /// <returns>已发布时返回 <see langword="true"/>。</returns>
    bool TryGetSourceEntry(string sourceId, out VisionAcquisitionSourceInfo? source);
}
