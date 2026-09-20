using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>拥有物理分割和归一化证据；是否完成与缺陷条数独立。</summary>
public sealed class TextQualityResult : IDisposable
{
    /// <summary>接管分割及单字比较证据，释放结果时一并释放。</summary>
    /// <param name = "segmentation">需要移交所有权的物理分割结果。</param>
    /// <param name = "glyphs">包含完成项与阻断项的字符比较记录；集合会复制。</param>
    public TextQualityResult(CharacterSegmentation segmentation, IEnumerable<GlyphQualityEvidence> glyphs)
    {
        Segmentation = segmentation ?? throw new ArgumentNullException(nameof(segmentation));
        Glyphs = Array.AsReadOnly((glyphs ?? throw new ArgumentNullException(nameof(glyphs))).ToArray());
        Findings = Array.Empty<QualityFinding>();
    }

    private readonly bool? _completed;

    /// <summary>创建替代质量策略的区域级结果，不要求伪造单字图块或差异图。</summary>
    /// <param name = "completed">策略是否完整执行；即使为true，存在阻断记录仍视为未完成。</param>
    /// <param name = "findings">区域级测量、缺陷或阻断记录；集合会复制。</param>
    public TextQualityResult(bool completed, IEnumerable<QualityFinding> findings)
    {
        _completed = completed;
        Findings = Array.AsReadOnly(
            (findings ?? throw new ArgumentNullException(nameof(findings))).ToArray()
        );
        Glyphs = Array.Empty<GlyphQualityEvidence>();
    }

    /// <summary>替代策略的测量记录；区域级结果不必伪造字符局部证据。</summary>
    public IReadOnlyList<QualityFinding> Findings { get; }

    /// <summary>物理分割结果；不采用字符分割的策略可为null。</summary>
    public CharacterSegmentation? Segmentation { get; }

    /// <summary>全部字符测量记录，包含阻断项。</summary>
    public IReadOnlyList<GlyphQualityEvidence> Glyphs { get; }

    /// <summary>所有应测字符是否完成测量；部分超过阈值仍属于已完成但不通过。</summary>
    public bool Completed =>
        !Findings.Any(f => f.Kind == EQualityFindingKind.Blocker)
        && (
            _completed
            ?? (
                (Segmentation?.Status == "provisional" || Segmentation?.Status == "explicit_cells")
                && Glyphs.Count > 0
                && Glyphs.Count == Segmentation!.Characters.Count
                && Glyphs.All(g => g.Comparison?.Status == EAlgorithmStatus.Completed)
            )
        );

    /// <summary>测量完整完成，且没有超差或缺陷记录。</summary>
    public bool Passed =>
        Completed
        && Glyphs.All(g => g.Status == "compared")
        && !Findings.Any(f => f.Kind == EQualityFindingKind.Defect);

    /// <summary>释放本结果拥有的全部物理图块和归一化证据。</summary>
    public void Dispose()
    {
        foreach (var g in Glyphs)
        {
            g.Comparison?.Dispose();
        }

        Segmentation?.Dispose();
    }
}
