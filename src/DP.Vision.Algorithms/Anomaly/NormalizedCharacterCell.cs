using System;

namespace DP.Vision.Algorithms;

/// <summary>按逐字符训练方式归一化的一个字符单元（用于与其他异常检测算法在相同输入上对比）。</summary>
public sealed class NormalizedCharacterCell : IDisposable
{
    /// <summary>创建单元，接管图像。</summary>
    /// <param name = "line">所属行的序号（训练行在前，其余行接续编号）。</param>
    /// <param name = "index">在该行字符列表中的序号。</param>
    /// <param name = "key">模型键。</param>
    /// <param name = "training">是否来自训练行。</param>
    /// <param name = "image">归一化后的灰度单元，与同键训练单元同尺寸，由本对象拥有。</param>
    public NormalizedCharacterCell(int line, int index, string key, bool training, IImageSource image)
    {
        Line = line;
        Index = index;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Training = training;
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <summary>所属行的序号。</summary>
    public int Line { get; }

    /// <summary>在该行字符列表中的序号。</summary>
    public int Index { get; }

    /// <summary>模型键。</summary>
    public string Key { get; }

    /// <summary>是否来自训练行。</summary>
    public bool Training { get; }

    /// <summary>归一化后的灰度单元。</summary>
    public IImageSource Image { get; }

    /// <summary>释放单元图像。</summary>
    public void Dispose()
    {
        Image.Dispose();
    }
}
