using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>
/// 逐字符异常检测的一行训练样本：借用的原图及该行全部字符。整行字母/数字用于测量字高与基线（字符按行几何归一化），
/// 只有带模型键的字符作为训练样本。同一原图对象的各行视为同一来源（缺墨阈值按来源图留一标定）。
/// </summary>
public sealed class CharacterAnomalyLine
{
    /// <summary>创建一行样本。</summary>
    /// <param name = "image">借用的原始整图（良品），调用方拥有。</param>
    /// <param name = "characters">该行按顺序的字符（原图坐标）。</param>
    public CharacterAnomalyLine(IImageSource image, IEnumerable<CharacterAnomalyCharacter> characters)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        var list = (characters ?? throw new ArgumentNullException(nameof(characters))).ToArray();
        if (list.Any(c => c == null))
        {
            throw new ArgumentException("Characters must not be null.", nameof(characters));
        }

        Characters = new ReadOnlyCollection<CharacterAnomalyCharacter>(list);
    }

    /// <summary>借用的原始整图。</summary>
    public IImageSource Image { get; }

    /// <summary>该行字符。</summary>
    public IReadOnlyList<CharacterAnomalyCharacter> Characters { get; }
}
