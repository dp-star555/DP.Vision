using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>独立的序数参考配对接口；字典是传入快照，不是存储连接。</summary>
public interface ICharacterMatcher
{
    /// <summary>按实际图块顺序返回参考键或null，保留重复字符和序列位置。</summary>
    /// <param name = "characters">按顺序排列的实际物理图块，调用期间借用。</param>
    /// <param name = "referenceKeys">独立参考键快照，按大小写敏感序数匹配。</param>
    /// <param name = "token">协作式取消标记。</param>
    IReadOnlyList<string?> Match(
        IReadOnlyList<CharacterPatch> characters,
        IReadOnlyCollection<string> referenceKeys,
        CancellationToken token = default
    );
}
