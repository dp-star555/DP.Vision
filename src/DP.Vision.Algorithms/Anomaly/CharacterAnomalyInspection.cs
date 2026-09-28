using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>一行文字的逐字符异常检测结果。</summary>
public sealed class CharacterAnomalyInspection : IDisposable
{
    /// <summary>创建结果，接管热力图。</summary>
    /// <param name = "crop">热力图对应的原图范围（请求范围与图像的交集）。</param>
    /// <param name = "characters">带模型键字符的结果，按输入顺序。</param>
    /// <param name = "heatMap">与<paramref name = "crop"/>同尺寸的合成热力图（128对应各字符自己的阈值）；没有已检测字符时为null。</param>
    public CharacterAnomalyInspection(
        PixelBounds crop,
        IEnumerable<CharacterAnomalyOutcome> characters,
        IImageSource? heatMap
    )
    {
        Crop = crop;
        Characters = Array.AsReadOnly(
            (characters ?? throw new ArgumentNullException(nameof(characters))).ToArray()
        );
        HeatMap = heatMap;
    }

    /// <summary>热力图对应的原图范围。</summary>
    public PixelBounds Crop { get; }

    /// <summary>逐字符结果。</summary>
    public IReadOnlyList<CharacterAnomalyOutcome> Characters { get; }

    /// <summary>合成热力图，由本对象拥有。</summary>
    public IImageSource? HeatMap { get; }

    /// <summary>释放热力图。</summary>
    public void Dispose()
    {
        HeatMap?.Dispose();
    }
}
