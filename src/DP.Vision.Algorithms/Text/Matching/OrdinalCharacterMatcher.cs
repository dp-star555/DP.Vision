using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>大小写敏感匹配，不做O/0修正或推测替换。</summary>
public sealed class OrdinalCharacterMatcher : ICharacterMatcher
{
    /// <inheritdoc/>
    public IReadOnlyList<string?> Match(
        IReadOnlyList<CharacterPatch> characters,
        IReadOnlyCollection<string> referenceKeys,
        CancellationToken token = default
    )
    {
        if (characters == null || referenceKeys == null)
        {
            throw new ArgumentNullException(nameof(characters));
        }

        var keys = new HashSet<string>(referenceKeys, StringComparer.Ordinal);
        var result = new string?[characters.Count];
        for (int i = 0; i < result.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            result[i] = keys.Contains(characters[i].Character) ? characters[i].Character : null;
        }

        return Array.AsReadOnly(result);
    }
}
