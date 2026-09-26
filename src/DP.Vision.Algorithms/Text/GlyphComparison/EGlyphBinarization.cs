namespace DP.Vision.Algorithms;

/// <summary>由参考配置确定的二值化模式，不使用隐式的实现专用字符串。</summary>
public enum EGlyphBinarization
{
    /// <summary>实际图和参考图分别执行Otsu二值化。</summary>
    Otsu,

    /// <summary>灰度严格小于配置阈值的像素视为墨迹。</summary>
    Fixed,

    /// <summary>
    /// 实际图和参考图分别以自身墨色与纸色（2%/98%灰度分位，3×3中值去噪后）的中点为阈值。
    /// 模糊边缘在中点处穿过真实边缘位置，笔画宽度不随模糊程度、对比度或背景面积变化，比Otsu更稳定。
    /// </summary>
    Midpoint,
}
