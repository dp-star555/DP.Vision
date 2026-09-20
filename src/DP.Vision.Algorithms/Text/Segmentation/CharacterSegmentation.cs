using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>拥有全部返回的物理图块；不确定或候选状态不代表生产检测完成。</summary>
public sealed class CharacterSegmentation : IDisposable
{
    /// <summary>接管传入图块并复制集合快照。</summary>
    /// <param name = "status">物理分割状态码，候选或不确定状态不代表正式检测通过。</param>
    /// <param name = "reason">状态原因及测量说明。</param>
    /// <param name = "basis">边界来源或分割依据。</param>
    /// <param name = "physicalCount">实测分组数，包含分隔符。</param>
    /// <param name = "characters">需要移交所有权的字符图块集合，内部复制集合。</param>
    public CharacterSegmentation(
        string status,
        string reason,
        string basis,
        int physicalCount,
        IEnumerable<CharacterPatch> characters
    )
    {
        Status = status;
        Reason = reason;
        Basis = basis;
        PhysicalCount = physicalCount;
        Characters = Array.AsReadOnly(characters.ToArray());
    }

    /// <summary>状态码：provisional待确认、explicit_cells显式等格、uncertain不确定、unsupported不支持或review_required待复核。</summary>
    public string Status { get; }

    /// <summary>实测情况说明。</summary>
    public string Reason { get; }

    /// <summary>物理分离的依据。</summary>
    public string Basis { get; }

    /// <summary>原始实测分组数，包含分隔符。</summary>
    public int PhysicalCount { get; }

    /// <summary>本结果拥有的图块集合。</summary>
    public IReadOnlyList<CharacterPatch> Characters { get; }

    /// <summary>释放全部图块租约。</summary>
    public void Dispose()
    {
        foreach (var c in Characters)
        {
            c.Dispose();
        }
    }
}
