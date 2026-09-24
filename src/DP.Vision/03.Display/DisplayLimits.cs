namespace DP.Vision;

/// <summary>显示模型各类共用的上限，集中定义以免各处取值漂移。</summary>
internal static class DisplayLimits
{
    /// <summary>最大2次幂采样级别。</summary>
    internal const int MaxLevel = 20;

    /// <summary>图块边长下限，单位为当前级别像素。</summary>
    internal const int MinTileEdge = 16;

    /// <summary>图块边长上限，单位为当前级别像素。</summary>
    internal const int MaxTileEdge = 1024;

    /// <summary>单个图层以及整个叠加快照的显示项上限。</summary>
    internal const int MaxVisuals = 10000;

    /// <summary>叠加快照的图层上限。</summary>
    internal const int MaxLayers = 128;

    /// <summary>叠加快照的几何元素（点、游程、控制点）总数上限。</summary>
    internal const long MaxOverlayElements = 2000000;

    /// <summary>视口最小缩放比例（每原图像素对应的控件单位）。</summary>
    internal const double MinScale = 1.0 / 1024;

    /// <summary>视口最大缩放比例。</summary>
    internal const double MaxScale = 128;
}
