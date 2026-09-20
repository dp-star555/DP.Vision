using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>组合可替换的物理分割、字符配对和单字比较，不依赖具体视觉厂商。</summary>
public sealed class TextQualityInspector : ITextQualityInspector
{
    private readonly ICharacterSegmenter _segmenter;
    private readonly ICharacterMatcher _matcher;
    private readonly IGlyphComparer _comparer;

    /// <summary>接收宿主拥有的算法，不在内部创建具体厂商或原生实现。</summary>
    /// <param name = "segmenter">物理字符分割策略。</param>
    /// <param name = "matcher">实际字符到参考标识的配对策略。</param>
    /// <param name = "comparer">单字归一化比较策略。</param>
    public TextQualityInspector(
        ICharacterSegmenter segmenter,
        ICharacterMatcher matcher,
        IGlyphComparer comparer
    )
    {
        _segmenter = segmenter ?? throw new ArgumentNullException(nameof(segmenter));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
    }

    /// <inheritdoc/>
    public bool RequiresReferences => true;

    /// <inheritdoc/>
    public bool RequiresRecognition => true;

    /// <inheritdoc/>
    public TextQualityResult Inspect(TextQualityRequest request, CancellationToken token = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        token.ThrowIfCancellationRequested();
        var segmentation = request.EqualCells
            ? _segmenter.EqualCells(request.Image, request.Bounds, request.Identity)
            : _segmenter.Segment(request.Image, request.Bounds, request.Identity, token);
        var glyphs = new List<GlyphQualityEvidence>();
        try
        {
            if (segmentation.Status != "provisional" && segmentation.Status != "explicit_cells")
            {
                return new TextQualityResult(segmentation, glyphs);
            }

            var matched = _matcher.Match(segmentation.Characters, request.References.Keys.ToArray(), token);
            if (matched.Count != segmentation.Characters.Count)
            {
                throw new InvalidOperationException("Matcher omitted character results.");
            }

            for (int i = 0; i < matched.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var c = segmentation.Characters[i];
                var key = matched[i];
                if (key == null || !request.References.TryGetValue(key, out var reference))
                {
                    glyphs.Add(new GlyphQualityEvidence(c, null, "missing_template", null));
                    continue;
                }

                var result = _comparer.Compare(
                    c.Patch,
                    reference.Image,
                    new GlyphComparisonOptions(request.Threshold, request.Tolerance, reference.Binarization),
                    token
                );
                glyphs.Add(
                    new GlyphQualityEvidence(
                        c,
                        key,
                        result.Status != EAlgorithmStatus.Completed ? result.ReasonCode
                            : result.Difference > request.MaximumDifference ? "exceeds_threshold"
                            : "compared",
                        result
                    )
                );
            }

            token.ThrowIfCancellationRequested();
            return new TextQualityResult(segmentation, glyphs);
        }
        catch
        {
            foreach (var g in glyphs)
            {
                g.Comparison?.Dispose();
            }

            segmentation.Dispose();
            throw;
        }
    }
}
