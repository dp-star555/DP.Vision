using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>
/// 工具下拉框的一项：工具及其显示文字。各宿主的编辑窗口用同一份列表绑定一个下拉框，
/// 不再为每种区域单独排一个按钮；<see cref="ToString"/>返回显示文字，可直接作为下拉项。
/// </summary>
public sealed class RoiToolChoice
{
    private RoiToolChoice(ERoiTool tool, string text)
    {
        Tool = tool;
        Text = text;
    }

    /// <summary>对应的编辑器工具。</summary>
    public ERoiTool Tool { get; }

    /// <summary>下拉框显示文字。</summary>
    public string Text { get; }

    /// <summary>是否为涂抹工具（画笔/橡皮）；宿主可据此启用笔刷半径和涂抹用途。</summary>
    public bool IsPaint => Tool == ERoiTool.Brush || Tool == ERoiTool.Eraser;

    /// <summary>全部工具，按“选择、面积形状、线与点、顶点编辑、涂抹”排列。</summary>
    public static IReadOnlyList<RoiToolChoice> All { get; } =
        Array.AsReadOnly(
            new[]
            {
                new RoiToolChoice(ERoiTool.Select, "选择 / 移动"),
                new RoiToolChoice(ERoiTool.Rectangle, "矩形"),
                new RoiToolChoice(ERoiTool.RotatedRectangle, "旋转矩形"),
                new RoiToolChoice(ERoiTool.Circle, "圆"),
                new RoiToolChoice(ERoiTool.Ellipse, "椭圆"),
                new RoiToolChoice(ERoiTool.Polygon, "多边形"),
                new RoiToolChoice(ERoiTool.Polyline, "折线"),
                new RoiToolChoice(ERoiTool.Point, "点"),
                new RoiToolChoice(ERoiTool.InsertVertex, "插入顶点（选中轮廓）"),
                new RoiToolChoice(ERoiTool.DeleteVertex, "删除顶点（选中轮廓）"),
                new RoiToolChoice(ERoiTool.Brush, "画笔（涂抹）"),
                new RoiToolChoice(ERoiTool.Eraser, "橡皮（擦涂抹）"),
            }
        );

    /// <summary>能构成面积区域（参与有效掩膜）的工具：选择、面积形状、顶点编辑和涂抹，不含折线与点。</summary>
    public static IReadOnlyList<RoiToolChoice> Areas { get; } =
        Array.AsReadOnly(All.Where(c => c.Tool != ERoiTool.Polyline && c.Tool != ERoiTool.Point).ToArray());

    /// <summary>在列表中查找工具对应的项。</summary>
    /// <param name="choices">下拉框使用的列表。</param>
    /// <param name="tool">编辑器当前工具。</param>
    /// <returns>对应项；列表中没有该工具时为null。</returns>
    public static RoiToolChoice? Find(IEnumerable<RoiToolChoice> choices, ERoiTool tool)
    {
        return (choices ?? throw new ArgumentNullException(nameof(choices))).FirstOrDefault(c => c.Tool == tool);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Text;
    }
}
