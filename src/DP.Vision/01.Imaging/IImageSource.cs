using System;
using System.Collections.Generic;

namespace DP.Vision;

/// <summary>
/// 客户统一使用的不可变图像源。创建、Retain和ReadTile返回的源均由取得者释放。
/// 读取及Retain必须与Dispose安全同步；共享的原始像素不可修改，不向外暴露底层可写数组。
/// </summary>
public interface IImageSource : IDisposable
{
    /// <summary>原始全分辨率图像的布局。</summary>
    ImageInfo Info { get; }

    /// <summary>获取图像源的独立生命周期。</summary>
    /// <returns>需要单独释放的新图像源句柄。</returns>
    IImageSource Retain();

    /// <summary>读取按最近邻方式采样的图块；第0级为精确原像素，边缘图块可小于约定尺寸。</summary>
    /// <param name = "level">采样级别，范围0–20；原图采样间隔为2的level次方。</param>
    /// <param name = "tileX">当前级别的非负图块列索引，不是像素坐标。</param>
    /// <param name = "tileY">当前级别的非负图块行索引，不是像素坐标。</param>
    /// <param name = "tileSize">图块边长，范围16–1024，单位为当前级别像素。</param>
    /// <returns>需由调用方Dispose的图块租约，保留源通道布局。</returns>
    IImageSource ReadTile(int level, int tileX, int tileY, int tileSize);

    /// <summary>复制紧密排列的原分辨率像素字节到调用方数组，不暴露底层可写存储。</summary>
    /// <param name="sourceOffset">源图起始字节偏移，不是像素坐标。</param>
    /// <param name="destination">调用方拥有的目标数组。</param>
    /// <param name="destinationOffset">目标数组中的起始字节偏移。</param>
    /// <param name="count">复制字节数，源和目标范围必须有效。</param>
    void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count);
}
