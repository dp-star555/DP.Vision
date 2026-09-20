using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>由参考配置确定的二值化模式，不使用隐式的实现专用字符串。</summary>
public enum EGlyphBinarization
{
    /// <summary>实际图和参考图分别执行Otsu二值化。</summary>
    Otsu,

    /// <summary>灰度严格小于配置阈值的像素视为墨迹。</summary>
    Fixed,
}
