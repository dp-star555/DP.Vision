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
            throw new ArgumentException("Frame identity required.");
        }

        var input = layers?.ToArray() ?? throw new ArgumentNullException(nameof(layers));
        if (input.Any(l => l == null))
        {
            throw new ArgumentException("Null layer.");
        }

        var copy = input.OrderBy(l => l.Order).ToArray();
        if (
            copy.Length > 128
            || copy.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length
            || copy.Sum(l => (long)l.Visuals.Count) > 10000
        )
        {
            throw new ArgumentException("Invalid layers.");
        }

        long size = copy.SelectMany(l => l.Visuals)
            .Sum(v =>
                v.Geometry is ContourGeometry c ? (long)c.Points.Count
                : v.Geometry is RegionGeometry r ? r.Runs.Count
                : 4
            );
        if (size > 2000000)
        {
            throw new ArgumentException("Overlay exceeds geometry budget.");
        }

        FrameId = frameId;
        Layers = Array.AsReadOnly(copy);
    }

    /// <summary>必须与所显示图像一致的帧标识。</summary>
    public string FrameId { get; }

    /// <summary>独立图层的只读集合。</summary>
    public IReadOnlyList<CanvasLayer> Layers { get; }
}
