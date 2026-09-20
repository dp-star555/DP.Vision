namespace DP.Vision;

/// <summary>
/// 不依赖WinForms/WPF的预定义显示颜色，采用非预乘ARGB打包值。
/// 除Transparent外均不透明；颜色只影响显示，不代表算法判定或缺陷等级。
/// </summary>
public static class VisionColors
{
    /// <summary>完全透明。</summary>
    public const uint Transparent = 0x00000000;

    /// <summary>白色。</summary>
    public const uint White = 0xFFFFFFFF;

    /// <summary>黑色。</summary>
    public const uint Black = 0xFF000000;

    /// <summary>灰色。</summary>
    public const uint Gray = 0xFF808080;

    /// <summary>红色。</summary>
    public const uint Red = 0xFFFF0000;

    /// <summary>深红色。</summary>
    public const uint Crimson = 0xFFDC143C;

    /// <summary>橙色。</summary>
    public const uint Orange = 0xFFFFA500;

    /// <summary>深橙色。</summary>
    public const uint DarkOrange = 0xFFFF8C00;

    /// <summary>金色。</summary>
    public const uint Gold = 0xFFFFD700;

    /// <summary>黄色。</summary>
    public const uint Yellow = 0xFFFFFF00;

    /// <summary>绿色。</summary>
    public const uint Green = 0xFF008000;

    /// <summary>亮绿色。</summary>
    public const uint Lime = 0xFF00FF00;

    /// <summary>森林绿。</summary>
    public const uint ForestGreen = 0xFF228B22;

    /// <summary>青色。</summary>
    public const uint Cyan = 0xFF00FFFF;

    /// <summary>蓝绿色。</summary>
    public const uint Teal = 0xFF008080;

    /// <summary>蓝色。</summary>
    public const uint Blue = 0xFF0000FF;

    /// <summary>宝蓝色。</summary>
    public const uint RoyalBlue = 0xFF4169E1;

    /// <summary>紫色。</summary>
    public const uint Purple = 0xFF800080;

    /// <summary>品红色。</summary>
    public const uint Magenta = 0xFFFF00FF;

    /// <summary>粉色。</summary>
    public const uint Pink = 0xFFFFC0CB;

    /// <summary>玫红色。</summary>
    public const uint Rose = 0xFFFF3388;
}
