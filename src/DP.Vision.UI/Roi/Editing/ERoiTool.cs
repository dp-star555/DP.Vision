using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>明确的ROI编辑工具；检测证据层不会被隐式变成可编辑对象。</summary>
public enum ERoiTool
{
    /// <summary>选择、移动、缩放或旋转ROI配置。</summary>
    Select,

    /// <summary>轴对齐矩形。</summary>
    Rectangle,

    /// <summary>带旋转控制点的矩形。</summary>
    RotatedRectangle,

    /// <summary>保持等半径约束的圆。</summary>
    Circle,

    /// <summary>可旋转椭圆。</summary>
    Ellipse,

    /// <summary>闭合并按奇偶规则填充的多边形。</summary>
    Polygon,

    /// <summary>开放的有序折线。</summary>
    Polyline,

    /// <summary>单个亚像素点。</summary>
    Point,

    /// <summary>在选中轮廓最近的边上插入顶点。</summary>
    InsertVertex,

    /// <summary>删除选中轮廓最近的顶点。</summary>
    DeleteVertex,
}
