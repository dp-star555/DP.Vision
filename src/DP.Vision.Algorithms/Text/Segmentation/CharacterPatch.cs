using System;

namespace DP.Vision.Algorithms;

/// <summary>拥有像素的物理字符证据；字符身份仍是调用方假设，不是业务真值。</summary>
public sealed class CharacterPatch : IDisposable
{
    /// <summary>接管传入的独立图像租约；范围可能与邻字重叠，应使用Patch，不能按Bounds重新裁图。</summary>
    /// <param name = "character">待确认的单字符标签，不是业务真值。</param>
    /// <param name = "tokenIndex">在非空白输入标签序列中的索引。</param>
    /// <param name = "bounds">原图整数范围，允许与邻字范围重叠。</param>
    /// <param name = "patch">移交给结果的独立图像租约，不能再由调用方直接释放。</param>
    /// <param name = "neighborInkRemoved">从图块移除的已知邻字墨迹数，单位为原图像素。</param>
    public CharacterPatch(
        string character,
        int tokenIndex,
        PixelBounds bounds,
        IImageSource patch,
        int neighborInkRemoved = 0
    )
    {
        Character = character;
        TokenIndex = tokenIndex;
        Bounds = bounds;
        Patch = patch ?? throw new ArgumentNullException(nameof(patch));
        NeighborInkRemoved = neighborInkRemoved;
    }

    /// <summary>待确认的字符身份。</summary>
    public string Character { get; }

    /// <summary>在去除空白后的输入字符序列中的位置。</summary>
    public int TokenIndex { get; }

    /// <summary>原图像素范围。</summary>
    public PixelBounds Bounds { get; }

    /// <summary>借用的独立像素证据；若需超出本对象生命周期，应另行Retain。</summary>
    public IImageSource Patch { get; }

    /// <summary>实测移除的邻字墨迹量。</summary>
    public int NeighborInkRemoved { get; }

    /// <summary>释放所拥有的证据。</summary>
    public void Dispose()
    {
        Patch.Dispose();
    }
}
