using System;
using System.Collections.Generic;

namespace DP.Vision;

/// <summary>内存图像源；按需生成显示图块。</summary>
internal sealed class MemoryImageSource : IImageSource
{
    private readonly ImageBuffer _image;

    /// <summary>接管内部图像句柄，不增加租约；调用方不得再使用或释放该句柄。</summary>
    /// <param name="image">待接管的内部存储租约。</param>
    internal MemoryImageSource(ImageBuffer image)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <inheritdoc/>
    public ImageInfo Info => _image.Info;

    /// <inheritdoc/>
    public IImageSource Retain()
    {
        return new MemoryImageSource(_image.Retain());
    }

    /// <summary>
    /// 校验参数后，从保留的原图按网格生成一个独立图块。
    /// 第0级只裁切；更高级别跳行、跳列取原像素，不预先生成完整缩小图。
    /// </summary>
    /// <param name="level">采样级别，范围0–20；原图采样间隔为2的level次方。</param>
    /// <param name="tileX">当前级别的非负图块列号，不是原图像素列；指定图块必须与图像相交。</param>
    /// <param name="tileY">当前级别的非负图块行号，不是原图像素行；指定图块必须与图像相交。</param>
    /// <param name="tileSize">常规输出边长，范围16–1024，单位为输出像素；边缘不足时返回较小图块。</param>
    /// <returns>拥有独立像素数组的小图租约，由调用方Dispose；释放原图不会使已返回的小图失效。</returns>
    /// <remarks>每次读取生成新图块，不在本对象内缓存；低分辨率间隔采样可能跳过细小笔画或缺陷。</remarks>
    public IImageSource ReadTile(int level, int tileX, int tileY, int tileSize)
    {
        if (level < 0 || level > 20 || tileX < 0 || tileY < 0 || tileSize < 16 || tileSize > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        return new MemoryImageSource(_image.Sample(level, tileX, tileY, tileSize));
    }

    /// <inheritdoc/>
    public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count)
    {
        _image.CopyTo(sourceOffset, destination, destinationOffset, count);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _image.Dispose();
    }
}
