using System;
using System.Collections.Generic;

namespace DP.Vision;

/// <summary>像素布局</summary>
public enum EPixelLayout
{
    /// <summary>单通道灰度，每像素1字节。</summary>
    Gray8,

    /// <summary>单通道灰度，每像素2字节，小端存储。</summary>
    Gray16,

    /// <summary>依次存储蓝、绿、红通道。</summary>
    Bgr24,

    /// <summary>依次存储红、绿、蓝通道。</summary>
    Rgb24,

    /// <summary>依次存储蓝、绿、红和非预乘Alpha。</summary>
    Bgra32,

    /// <summary>依次存储红、绿、蓝和非预乘Alpha。</summary>
    Rgba32,
}
