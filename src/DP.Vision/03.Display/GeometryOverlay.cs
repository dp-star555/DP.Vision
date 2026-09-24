using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>绑定明确帧标识的叠加快照，与图像存储分离。</summary>
public sealed class GeometryOverlay
{
    /// <summary>创建显式绑定帧的叠加证据；可以复用几何，不能复用过期帧标识。</summary>
    /// <param name = "frameId">对应原图的非空帧标识，最长256字符。</param>
    /// <param name = "layers">图层快照集合；图层标识必须唯一。</param>
    public GeometryOverlay(string frameId, IEnumerable<CanvasLayer> layers)
    {
        if (!Identity.IsValid(frameId))
        {
            throw new ArgumentException("帧标识不能为空白，且不得超过256个字符。", nameof(frameId));
        }

        var input = layers?.ToArray() ?? throw new ArgumentNullException(nameof(layers), "图层集合不能为空。");
        if (input.Any(l => l == null))
        {
            throw new ArgumentException("图层集合不能包含空项。", nameof(layers));
        }

        var copy = input.OrderBy(l => l.Order).ToArray();
        if (copy.Length > DisplayLimits.MaxLayers)
        {
            throw new ArgumentException("叠加图层不得超过128个。", nameof(layers));
        }

        if (copy.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("图层标识必须唯一。", nameof(layers));
        }

        if (copy.Sum(l => (long)l.Visuals.Count) > DisplayLimits.MaxVisuals)
        {
            throw new ArgumentException("叠加显示项总数不得超过10000个。", nameof(layers));
        }

        long size = copy.SelectMany(l => l.Visuals).Sum(v => v.Geometry.ElementCount);
        if (size > DisplayLimits.MaxOverlayElements)
        {
            throw new ArgumentException("叠加几何元素总数超过2000000的预算。", nameof(layers));
        }

        FrameId = frameId;
        Layers = Array.AsReadOnly(copy);
    }

    /// <summary>必须与所显示图像一致的帧标识。</summary>
    public string FrameId { get; }

    /// <summary>独立图层的只读集合。</summary>
    public IReadOnlyList<CanvasLayer> Layers { get; }
}
