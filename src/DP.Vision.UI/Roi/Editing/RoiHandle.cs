namespace DP.Vision.UI;

/// <summary>使用原图坐标表示的一个编辑控制点。</summary>
public readonly struct RoiHandle
{
    /// <summary>创建编辑控制点。</summary>
    /// <param name = "position">控制点的原图坐标。</param>
    /// <param name = "kind">缩放、旋转、顶点或半径控制点类型。</param>
    /// <param name = "index">该几何内部的角点或顶点索引。</param>
    public RoiHandle(PointD position, ERoiHandleKind kind, int index)
    {
        Position = position;
        Kind = kind;
        Index = index;
    }

    /// <summary>原图坐标位置。</summary>
    public PointD Position { get; }

    /// <summary>编辑控制点类型。</summary>
    public ERoiHandleKind Kind { get; }

    /// <summary>几何内部的角点或顶点索引。</summary>
    public int Index { get; }
}
