using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>具有独立绘制顺序和可见性的图层。</summary>
public sealed class CanvasLayer
{
    /// <summary>复制显示项集合，保留其中几何的引用。</summary>
    /// <param name = "id">非空图层标识。</param>
    /// <param name = "kind">图层类别，不决定绘制顺序。</param>
    /// <param name = "visuals">需要复制的显示项集合。</param>
    /// <param name = "order">绘制顺序，较小值先绘制。</param>
    /// <param name = "visible">图层是否可见；隐藏不会删除原始几何。</param>
    /// <param name="name">可选显示名称；省略时使用Id，名称不参与图层身份比较。</param>
    public CanvasLayer(
        string id,
        ELayerKind kind,
        IEnumerable<Visual> visuals,
        int order = 0,
        bool visible = true,
        string? name = null
    )
    {
        if (!Identity.IsValid(id))
        {
            throw new ArgumentException("图层标识不能为空白，且不得超过256个字符。", nameof(id));
        }

        if (!Enum.IsDefined(typeof(ELayerKind), kind))
        {
            throw new ArgumentException("未定义的图层类别。", nameof(kind));
        }

        var copy = visuals?.ToArray() ?? throw new ArgumentNullException(nameof(visuals), "显示项集合不能为空。");
        if (copy.Length > DisplayLimits.MaxVisuals)
        {
            throw new ArgumentException("单个图层的显示项不得超过10000个。", nameof(visuals));
        }

        if (copy.Any(v => v == null))
        {
            throw new ArgumentException("显示项集合不能包含空项。", nameof(visuals));
        }

        if (name != null && !Identity.IsValid(name))
        {
            throw new ArgumentException("图层显示名称不能为空白，且不得超过256个字符。", nameof(name));
        }

        Id = id;
        Name = name ?? id;
        Kind = kind;
        Visuals = Array.AsReadOnly(copy);
        Order = order;
        Visible = visible;
    }

    /// <summary>图层标识。</summary>
    public string Id { get; }

    /// <summary>面向用户的显示名称，不代替稳定图层键。</summary>
    public string Name { get; }

    /// <summary>图层类别。</summary>
    public ELayerKind Kind { get; }

    /// <summary>按顺序排列的只读显示项。</summary>
    public IReadOnlyList<Visual> Visuals { get; }

    /// <summary>升序绘制顺序。</summary>
    public int Order { get; }

    /// <summary>图层是否可见。</summary>
    public bool Visible { get; }
}
