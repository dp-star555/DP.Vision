namespace DP.Vision.Algorithms;

/// <summary>单个字符的配对结果及其拥有的比较证据，允许记录未完成原因。</summary>
public sealed class GlyphQualityEvidence
{
    /// <summary>接管比较结果；字符图块仍由外层分割结果拥有。</summary>
    /// <param name = "character">借用的物理字符图块及原图位置。</param>
    /// <param name = "referenceKey">选择的参考标识，未匹配时为null。</param>
    /// <param name = "status">compared、exceeds_threshold或具体阻断码。</param>
    /// <param name = "comparison">要移交的归一化比较结果；未执行时可为null。</param>
    public GlyphQualityEvidence(
        CharacterPatch character,
        string? referenceKey,
        string status,
        GlyphComparisonResult? comparison
    )
    {
        Character = character;
        ReferenceKey = referenceKey;
        Status = status;
        Comparison = comparison;
    }

    /// <summary>借用的物理字符图块。</summary>
    public CharacterPatch Character { get; }

    /// <summary>已选择的参考标识，未匹配时为null。</summary>
    public string? ReferenceKey { get; }

    /// <summary>比较完成、超过阈值或具体阻断状态码。</summary>
    public string Status { get; }

    /// <summary>本条记录拥有的归一化证据。</summary>
    public GlyphComparisonResult? Comparison { get; }
}
