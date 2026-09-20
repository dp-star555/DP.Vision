using System;
using System.Threading;

namespace DP.Vision.UI;

/// <summary>不可变的UI编辑定义；不是可直接传给后台的活动编辑对象。</summary>
public sealed class RoiDefinition
{
    /// <summary>创建具有标识、用途和启用状态的可编辑形状。</summary>
    /// <param name = "id">非空ROI标识，最长256字符。</param>
    /// <param name = "shape">原图坐标下的不可变几何快照。</param>
    /// <param name = "purpose">包含或排除意图。</param>
    /// <param name = "enabled">是否启用该ROI。</param>
    public RoiDefinition(
        string id,
        Geometry shape,
        ERoiPurpose purpose = ERoiPurpose.Include,
        bool enabled = true
    )
        : this(id, shape, purpose, enabled, ERoiConstraint.None) { }

    /// <summary>创建已启用的包含区域，并指定编辑约束。</summary>
    /// <param name = "id">非空ROI标识，最长256字符。</param>
    /// <param name = "shape">原图坐标下的不可变几何。</param>
    /// <param name = "constraint">无约束、轴对齐或圆形约束，必须与输入几何一致。</param>
    public RoiDefinition(string id, Geometry shape, ERoiConstraint constraint)
        : this(id, shape, ERoiPurpose.Include, true, constraint) { }

    /// <summary>创建并校验完整的ROI编辑定义。</summary>
    /// <param name = "id">非空ROI标识，最长256字符。</param>
    /// <param name = "shape">非空的不可变原图几何。</param>
    /// <param name = "purpose">检查内部或排除内部的意图。</param>
    /// <param name = "enabled">是否启用；禁用项仍可以在UI中选择和编辑。</param>
    /// <param name = "constraint">几何编辑约束；圆必须等半径，轴对齐形状必须角度为0。</param>
    public RoiDefinition(
        string id,
        Geometry shape,
        ERoiPurpose purpose,
        bool enabled,
        ERoiConstraint constraint
    )
    {
        if (
            string.IsNullOrWhiteSpace(id)
            || id.Length > 256
            || !Enum.IsDefined(typeof(ERoiPurpose), purpose)
            || !Enum.IsDefined(typeof(ERoiConstraint), constraint)
        )
        {
            throw new ArgumentException("Invalid ROI.");
        }

        Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        if (
            constraint == ERoiConstraint.Circle
            && (!(shape is EllipseGeometry circle) || circle.RadiusX != circle.RadiusY)
        )
        {
            throw new ArgumentException("Circle constraint requires equal ellipse radii.");
        }

        if (
            constraint == ERoiConstraint.AxisAligned
            && !(shape is RectangleGeometry rectangle && rectangle.Angle == 0)
            && !(shape is EllipseGeometry ellipse && ellipse.Angle == 0)
        )
        {
            throw new ArgumentException("Axis-aligned constraint requires a zero-angle rectangle/ellipse.");
        }

        Id = id;
        Purpose = purpose;
        Enabled = enabled;
        Constraint = constraint;
    }

    /// <summary>把已启用定义确认成独立后台Region；结果不包含UI控制点、约束或编辑器引用，不会隐式填充开放轮廓。</summary>
    /// <param name = "imageWidth">后台原图宽度，单位为像素；不允许形状超出图像。</param>
    /// <param name = "imageHeight">后台原图高度，单位为像素。</param>
    /// <param name = "maximumWork">栅格化工作预算：采样像素数乘轮廓顶点数，或复制游程数；必须大于0。</param>
    /// <param name = "token">协作式取消标记，取消时不返回部分Region。</param>
    /// <returns>不可变的像素集合快照，后续UI编辑不会改变它。</returns>
    public RegionGeometry ToRegion(
        int imageWidth,
        int imageHeight,
        long maximumWork = 16777216,
        CancellationToken token = default
    )
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("A disabled ROI cannot be submitted for inspection.");
        }

        return RegionRasterizer.Rasterize(Shape, imageWidth, imageHeight, maximumWork, token);
    }

    /// <summary>编辑器内的ROI标识。</summary>
    public string Id { get; }

    /// <summary>原图坐标下的不可变形状快照。</summary>
    public Geometry Shape { get; }

    /// <summary>包含或排除意图，由宿主映射到后台业务。</summary>
    public ERoiPurpose Purpose { get; }

    /// <summary>该ROI配置是否启用。</summary>
    public bool Enabled { get; }

    /// <summary>UI编辑约束，不是后台算法要求。</summary>
    public ERoiConstraint Constraint { get; }
}
