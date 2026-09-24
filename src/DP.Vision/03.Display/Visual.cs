using System;

namespace DP.Vision;

/// <summary>几何显示样式；颜色使用非预乘ARGB打包值。</summary>
public sealed class Visual
{
    /// <summary>创建带样式的几何显示项；标题只用于显示，不参与算法判定。</summary>
    /// <param name = "id">调用方提供的显示项标识，非空且最长256字符；不会自动生成，建议在所属图层内唯一。</param>
    /// <param name = "geometry">不可变的原始几何，保留其引用。</param>
    /// <param name = "argb">非预乘ARGB颜色，可使用VisionColors预定义值或自定义值，最高字节为Alpha。</param>
    /// <param name = "caption">可选标题，最长4096字符。</param>
    public Visual(string id, Geometry geometry, uint argb = VisionColors.Rose, string? caption = null)
    {
        if (!Identity.IsValid(id) || caption?.Length > 4096)
        {
            throw new ArgumentException("Invalid visual identity/caption.");
        }

        Id = id;
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        Argb = argb;
        Caption = caption;
    }

    /// <summary>报告或配置项标识。</summary>
    public string Id { get; }

    /// <summary>精确的原始几何，不是显示简化结果。</summary>
    public Geometry Geometry { get; }

    /// <summary>非预乘ARGB颜色值。</summary>
    public uint Argb { get; }

    /// <summary>可选显示标题。</summary>
    public string? Caption { get; }
}
