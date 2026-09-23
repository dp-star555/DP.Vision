namespace DP.Vision;

/// <summary>独立的叠加层分类；绘制顺序由配置决定，不根据几何类型猜测。</summary>
public enum ELayerKind
{
    /// <summary>栅格Region证据层。</summary>
    Region,

    /// <summary>亚像素轮廓证据层。</summary>
    Xld,

    /// <summary>ROI配置的只读显示层。</summary>
    Roi,

    /// <summary>文字与状态标注层。</summary>
    Annotation,

    /// <summary>选择状态与控制点显示层。</summary>
    Interaction,
}
