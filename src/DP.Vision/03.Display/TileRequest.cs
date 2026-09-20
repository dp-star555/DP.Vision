using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision;

/// <summary>一个可见图块请求及其原图坐标位置。</summary>
public readonly struct TileRequest
{
    /// <summary>创建显示图块请求。</summary>
    /// <param name = "level">采样级别，间隔为2的level次方。</param>
    /// <param name = "x">当前级别的图块列索引。</param>
    /// <param name = "y">当前级别的图块行索引。</param>
    /// <param name = "bounds">图块在原图中的范围，应已裁到图像内。</param>
    public TileRequest(int level, int x, int y, RectD bounds)
    {
        Level = level;
        X = x;
        Y = y;
        Bounds = bounds;
    }

    /// <summary>2次幂采样级别。</summary>
    public int Level { get; }

    /// <summary>图块列索引。</summary>
    public int X { get; }

    /// <summary>图块行索引。</summary>
    public int Y { get; }

    /// <summary>已限制在原图内的源坐标范围。</summary>
    public RectD Bounds { get; }

    /// <summary>同一图像源版本内的缓存键。</summary>
    public string Key => Level + ":" + X + ":" + Y;
}
