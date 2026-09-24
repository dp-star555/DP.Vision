namespace DP.Vision.UI;

/// <summary>实际约束编辑行为的几何规则，不只是显示提示。</summary>
public enum ERoiConstraint
{
    /// <summary>无额外编辑约束。</summary>
    None,

    /// <summary>保持旋转角为零。</summary>
    AxisAligned,

    /// <summary>缩放时保持两个半径相等且圆心不变。</summary>
    Circle,
}
