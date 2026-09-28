using System;

namespace DP.Vision.Algorithms;

/// <summary>逐字符异常检测输入中的一个字符：身份、原图分割单元及模型键。</summary>
public sealed class CharacterAnomalyCharacter
{
    /// <summary>创建字符输入。</summary>
    /// <param name = "character">字符身份（单个字符时用于区分高字符与下伸字符）。</param>
    /// <param name = "tokenIndex">在该行中的序号（从0开始）。</param>
    /// <param name = "bounds">字符分割单元（原图坐标）。</param>
    /// <param name = "key">模型键；null表示只参与行几何测量，不训练、不检测。</param>
    public CharacterAnomalyCharacter(string character, int tokenIndex, PixelBounds bounds, string? key)
    {
        Character = character ?? throw new ArgumentNullException(nameof(character));
        TokenIndex = tokenIndex;
        Bounds = bounds;
        Key = key;
    }

    /// <summary>字符身份。</summary>
    public string Character { get; }

    /// <summary>在该行中的序号。</summary>
    public int TokenIndex { get; }

    /// <summary>分割单元（原图坐标）。</summary>
    public PixelBounds Bounds { get; }

    /// <summary>模型键；null时只参与行几何测量。</summary>
    public string? Key { get; }
}
