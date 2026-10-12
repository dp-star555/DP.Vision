using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>显示项在结果中的角色；画布按角色统一配色和画法，结果类型不决定颜色。</summary>
public enum EVisionDisplayRole
{
    /// <summary>主结果：拟合线/圆、交点、匹配框等。</summary>
    Result,
    /// <summary>输入或参考：参与计算的线/圆、扫描路径、卡尺位置。</summary>
    Input,
    /// <summary>测量线：距离、宽度。</summary>
    Measurement,
    /// <summary>参与计算的点（内点）。</summary>
    Accepted,
    /// <summary>被忽略的点（离群点）。</summary>
    Rejected,
    /// <summary>模型轮廓，如模板匹配的边缘。</summary>
    Model,
    /// <summary>区域或连通域。</summary>
    Region,
    /// <summary>坐标系 X 轴。</summary>
    AxisX,
    /// <summary>坐标系 Y 轴。</summary>
    AxisY
}

/// <summary>显示项的判定状态；非 <see cref="None"/> 时画布用状态色代替角色色（几何和标签都换）。</summary>
public enum EVisionDisplayTone
{
    /// <summary>无判定，按角色配色。</summary>
    None,
    /// <summary>合格（绿）。</summary>
    Ok,
    /// <summary>警告/临界（橙）。</summary>
    Warning,
    /// <summary>不合格（红）。</summary>
    Ng
}

/// <summary>
/// 一个显示项：原图坐标几何或标记点、角色、图上短标签及点击说明。标记点由画布按屏幕像素画成固定大小的 ×。
/// </summary>
public sealed class VisionDisplayItem
{
    private VisionDisplayItem(Geometry? geometry, PointD? marker, EVisionDisplayRole role, string? label, string? detail,
        EVisionDisplayTone tone = EVisionDisplayTone.None, uint? argb = null)
    {
        if (!Enum.IsDefined(typeof(EVisionDisplayRole), role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (!Enum.IsDefined(typeof(EVisionDisplayTone), tone)) throw new ArgumentOutOfRangeException(nameof(tone));
        if (label?.Length > 64) throw new ArgumentException("图上标签最长64个字符，完整说明放在 detail。", nameof(label));
        Geometry = geometry; Marker = marker; Role = role; Label = label; Detail = detail; Tone = tone; Argb = argb;
    }

    /// <summary>几何显示项。</summary>
    /// <param name="geometry">原图坐标几何。</param><param name="role">角色。</param>
    /// <param name="label">可选图上短标签（如“12.345 px”）。</param><param name="detail">可选点击说明；为空时画布用结果摘要。</param>
    public VisionDisplayItem(Geometry geometry, EVisionDisplayRole role, string? label = null, string? detail = null)
        : this(geometry ?? throw new ArgumentNullException(nameof(geometry)), null, role, label, detail) { }

    /// <summary>标记点显示项（画布画成固定屏幕大小的 ×）。</summary>
    /// <param name="position">原图坐标。</param><param name="role">角色。</param>
    /// <param name="label">可选图上短标签。</param><param name="detail">可选点击说明。</param>
    public static VisionDisplayItem Point(PointD position, EVisionDisplayRole role = EVisionDisplayRole.Result, string? label = null, string? detail = null) =>
        new VisionDisplayItem(null, position, role, label, detail);

    /// <summary>两点线段显示项。</summary>
    /// <param name="a">起点。</param><param name="b">终点。</param><param name="role">角色。</param>
    /// <param name="label">可选图上短标签。</param><param name="detail">可选点击说明。</param>
    public static VisionDisplayItem Segment(PointD a, PointD b, EVisionDisplayRole role, string? label = null, string? detail = null) =>
        new VisionDisplayItem(new ContourGeometry(new[] { a, b }), role, label, detail);

    /// <summary>几何；标记点为空。</summary>
    public Geometry? Geometry { get; }
    /// <summary>标记点位置；几何项为空。</summary>
    public PointD? Marker { get; }
    /// <summary>角色。</summary>
    public EVisionDisplayRole Role { get; }
    /// <summary>图上短标签；为空时不在图上写字。</summary>
    public string? Label { get; }
    /// <summary>点击说明；为空时由画布使用结果摘要。</summary>
    public string? Detail { get; }
    /// <summary>判定状态；非 None 时覆盖角色色。</summary>
    public EVisionDisplayTone Tone { get; }
    /// <summary>显式颜色（0xAARRGGBB），仅在角色和状态都表达不了时使用；不为空时覆盖状态色和角色色。</summary>
    public uint? Argb { get; }

    /// <summary>返回带判定状态的副本，如按公差判出的 OK/NG。</summary>
    /// <param name="tone">判定状态。</param>
    public VisionDisplayItem WithTone(EVisionDisplayTone tone) => new VisionDisplayItem(Geometry, Marker, Role, Label, Detail, tone, Argb);

    /// <summary>返回带显式颜色的副本；传 null 恢复按状态/角色配色。优先用角色和状态，颜色只作最后手段。</summary>
    /// <param name="argb">0xAARRGGBB 颜色。</param>
    public VisionDisplayItem WithColor(uint? argb) => new VisionDisplayItem(Geometry, Marker, Role, Label, Detail, Tone, argb);

    /// <summary>不依赖屏幕尺度的几何（标记点为半径2像素的小圆），供只认几何的显示使用。</summary>
    public Geometry ToGeometry() => Geometry ?? new EllipseGeometry(Marker!.Value, 2, 2);
}

/// <summary>可在画布上显示的结果：按角色给出显示项，并提供完整摘要。</summary>
public interface IVisionDisplayFact
{
    /// <summary>原图坐标显示项，按绘制顺序排列。</summary>
    IReadOnlyList<VisionDisplayItem> DisplayItems { get; }
    /// <summary>完整说明，显示在状态栏，也是没有单独说明的显示项被点击时的文字。</summary>
    string Summary { get; }
}

/// <summary>显示项的公共构造。</summary>
public static class VisionDisplay
{
    /// <summary>由显示项得到只含几何的列表（<see cref="IVisionGeometryFact.DisplayGeometry"/> 的统一实现）。</summary>
    /// <param name="items">显示项。</param>
    public static IReadOnlyList<Geometry> Geometries(IEnumerable<VisionDisplayItem> items) =>
        Array.AsReadOnly((items ?? throw new ArgumentNullException(nameof(items))).Select(item => item.ToGeometry()).ToArray());

    /// <summary>坐标系原点标记和 X/Y 轴线（各 <paramref name="length"/> 像素）。</summary>
    /// <param name="localToImage">局部到原图映射。</param><param name="length">轴线显示长度，原图像素。</param>
    /// <param name="label">原点上的短标签。</param>
    public static IEnumerable<VisionDisplayItem> Axes(CoordinateMatrix2D localToImage, double length, string? label = null)
    {
        if (localToImage == null) throw new ArgumentNullException(nameof(localToImage));
        var origin = new PointD(localToImage.Tx, localToImage.Ty);
        PointD Tip(double x, double y) { double norm = Math.Sqrt(x * x + y * y); return new PointD(origin.X + length * x / norm, origin.Y + length * y / norm); }
        yield return VisionDisplayItem.Point(origin, EVisionDisplayRole.Result, label);
        yield return VisionDisplayItem.Segment(origin, Tip(localToImage.M11, localToImage.M21), EVisionDisplayRole.AxisX);
        yield return VisionDisplayItem.Segment(origin, Tip(localToImage.M12, localToImage.M22), EVisionDisplayRole.AxisY);
    }
}
