using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>
/// 逐字符异常检测的一行训练样本：借用的原图及该行全部字符。提供稳定行ROI时按行范围归一化；历史未提供时按整行墨迹测量字高与基线，
/// 只有带模型键的字符作为训练样本。同一原图对象的各行视为同一来源（缺墨阈值按来源图留一标定）。
/// </summary>
public sealed class CharacterAnomalyLine
{
    /// <summary>创建一行样本。</summary>
    /// <param name = "image">借用的原始整图（良品），调用方拥有。</param>
    /// <param name = "characters">该行按顺序的字符（原图坐标）。</param>
    /// <param name="bounds">可选稳定单行ROI；指定时采用行ROI归一化，支持中文及纯标点。</param>
    public CharacterAnomalyLine(
        IImageSource image,
        IEnumerable<CharacterAnomalyCharacter> characters,
        PixelBounds? bounds = null
    )
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        var list = (characters ?? throw new ArgumentNullException(nameof(characters))).ToArray();
        if (list.Any(c => c == null))
        {
            throw new ArgumentException("Characters must not be null.", nameof(characters));
        }

        if (
            bounds is PixelBounds region
            && (
                region.X < 0
                || region.Y < 0
                || region.Width < 4
                || region.Height < 4
                || (long)region.X + region.Width > image.Info.Width
                || (long)region.Y + region.Height > image.Info.Height
                || list.Any(c =>
                    c.Bounds.X < region.X
                    || c.Bounds.Y < region.Y
                    || (long)c.Bounds.X + c.Bounds.Width > (long)region.X + region.Width
                    || (long)c.Bounds.Y + c.Bounds.Height > (long)region.Y + region.Height
                )
            )
        )
            throw new ArgumentException(
                "Character line ROI must contain its characters and fit the image.",
                nameof(bounds)
            );
        Bounds = bounds;
        Characters = new ReadOnlyCollection<CharacterAnomalyCharacter>(list);
    }

    /// <summary>借用的原始整图。</summary>
    public IImageSource Image { get; }

    /// <summary>稳定原图单行ROI；null保留历史墨迹行几何。</summary>
    public PixelBounds? Bounds { get; }

    /// <summary>该行字符。</summary>
    public IReadOnlyList<CharacterAnomalyCharacter> Characters { get; }
}
