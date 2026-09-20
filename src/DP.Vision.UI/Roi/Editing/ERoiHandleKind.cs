using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>保持屏幕交互尺寸稳定的控制点类型。</summary>
public enum ERoiHandleKind
{
    /// <summary>局部外接框缩放控制点。</summary>
    Size,

    /// <summary>绕几何中心旋转的控制点。</summary>
    Rotation,

    /// <summary>原始轮廓顶点。</summary>
    Vertex,

    /// <summary>圆半径控制点。</summary>
    Radius,
}
