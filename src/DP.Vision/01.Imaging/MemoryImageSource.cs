using System;

namespace DP.Vision;

/// <summary>
/// 内存图像源：只读图像租约，按需生成显示图块。多个句柄共享同一份<see cref="Storage"/>，
/// Dispose只释放当前句柄，不影响其他Retain句柄；不向外暴露可修改的底层数组。
/// </summary>
internal sealed class MemoryImageSource : IImageSource
{
    private readonly object _gate = new object();
    private Storage? _storage;

    /// <summary>接管一份租约计数已包含本句柄的存储，不复制像素。</summary>
    /// <param name="info">与存储数组匹配的紧密布局。</param>
    /// <param name="storage">共享存储；本句柄释放时减少其租约计数。</param>
    internal MemoryImageSource(ImageInfo info, Storage storage)
    {
        Info = info;
        _storage = storage;
    }

    /// <summary>复制调用方数据；调用方随后修改输入数组不会改变本图像。</summary>
    /// <param name="info">原图尺寸及像素布局。</param>
    /// <param name="pixels">待复制的像素数组，长度必须等于info.ByteLength。</param>
    /// <returns>拥有独立数据的只读租约，由调用方释放。</returns>
    internal static MemoryImageSource CopyFrom(ImageInfo info, byte[] pixels)
    {
        if (info == null)
        {
            throw new ArgumentNullException(nameof(info));
        }

        if (pixels == null || pixels.Length != info.ByteLength)
        {
            throw new ArgumentException("Pixel length mismatch.", nameof(pixels));
        }

        return Owned(info, (byte[])pixels.Clone());
    }

    /// <summary>接管内部已验证的数组，不复制；外部不得再持有可写引用。</summary>
    /// <param name="info">与数组匹配的紧密布局。</param>
    /// <param name="bytes">独占像素数组。</param>
    /// <returns>初始租约计数为1的新句柄。</returns>
    internal static MemoryImageSource Owned(ImageInfo info, byte[] bytes)
    {
        return new MemoryImageSource(info, new Storage(bytes, null));
    }

    /// <inheritdoc/>
    public ImageInfo Info { get; }

    /// <summary>取得共享像素存储但生命周期独立的新租约，不复制像素。</summary>
    /// <returns>需独立Dispose的新句柄；原句柄释放后仍可使用。</returns>
    public IImageSource Retain()
    {
        lock (_gate)
        {
            var storage = Alive();
            storage.Add();
            return new MemoryImageSource(Info, storage);
        }
    }

    /// <summary>
    /// 校验参数后，从保留的原图按规则网格生成一块独立小图。level为0时复制原分辨率图块；
    /// level大于0时跳行、跳列取样，使同尺寸小图表示更大的原图范围。
    /// </summary>
    /// <param name="level">采样级别，范围0–20；原图采样间隔为2的level次方。0每个像素都取，1取第0、2、4等行列，2取第0、4、8等行列。</param>
    /// <param name="tileX">缩小后逻辑图像中的图块列号，从0开始，不是原图X坐标；指定图块必须与图像相交。</param>
    /// <param name="tileY">缩小后逻辑图像中的图块行号，从0开始，不是原图Y坐标；指定图块必须与图像相交。</param>
    /// <param name="tileSize">输出图块的常规边长，范围16–1024，单位为输出像素；图像边缘不足时返回较小图块。</param>
    /// <returns>拥有新数组和独立初始租约的小图，由调用方Dispose；保留原像素布局，不携带原图放置坐标。</returns>
    /// <remarks>每次读取生成新图块，不在本对象内缓存；低分辨率间隔采样可能跳过细小笔画或缺陷。</remarks>
    public IImageSource ReadTile(int level, int tileX, int tileY, int tileSize)
    {
        if (level < 0 || level > 20 || tileX < 0 || tileY < 0 || tileSize < 16 || tileSize > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        // 确定跳行、跳列的间隔。左移相当于计算2的level次方。
        int factor = 1 << level;

        // 只计算缩小后逻辑图像的尺寸，不分配这张完整缩小图。
        // 向上取整：例如原图宽33、间隔2，会取0、2至32列，共17列。
        int levelWidth = (Info.Width + factor - 1) / factor,
            levelHeight = (Info.Height + factor - 1) / factor;

        // 把图块编号转换为当前级别的像素起点。
        // 注意此处x、y尚不是原图坐标；乘factor后才是原图起点。
        int x = checked(tileX * tileSize),
            y = checked(tileY * tileSize);
        if (x >= levelWidth || y >= levelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(tileX));
        }

        // 确定实际输出尺寸。边缘剩余不足tileSize时，只取有效部分，不补黑边。
        var info = new ImageInfo(
            Math.Min(tileSize, levelWidth - x),
            Math.Min(tileSize, levelHeight - y),
            Info.Layout
        );
        // 只为这块小图分配新数组，像素不与原图共享。
        var result = new byte[info.ByteLength];
        lock (_gate)
        {
            // 保证本句柄在复制期间不会被Dispose，并检查它是否仍然有效。
            var source = Alive().Bytes;
            int bytesPerPixel = info.BytesPerPixel;
            for (int row = 0; row < info.Height; row++)
            {
                // row是小图行号。(y+row)*factor得到本行对应的原图行号，x*factor得到原图起始列。
                // from = (原图行号*原图宽度+原图起始列)*每像素字节数，是源数组的字节偏移。
                // to = 小图行号*小图行跨度，是目标数组的字节偏移。两者都不是复制长度。
                int from = ((y + row) * factor * Info.Width + x * factor) * bytesPerPixel,
                    to = row * info.Stride;
                if (factor == 1)
                {
                    // 不缩小时，本行需要的像素连续，直接复制一条小图行。
                    // 下一次循环重新定位原图下一行，跳过原图中不属于该图块的部分。
                    Buffer.BlockCopy(source, from, result, to, info.Stride);
                }
                else if (bytesPerPixel == 1)
                {
                    // Gray8缩小时，每个输出像素只取一个字节：源列按factor跳，目标列连续写。
                    for (int col = 0; col < info.Width; col++)
                    {
                        result[to + col] = source[from + col * factor];
                    }
                }
                else
                {
                    // Gray16或彩色图：先选中一个原图像素，再原样复制该像素的全部字节。
                    // 保留位深、通道顺序及Alpha，不在这里转换颜色或灰度。
                    for (int col = 0; col < info.Width; col++)
                    {
                        for (int b = 0; b < bytesPerPixel; b++)
                        {
                            result[to + col * bytesPerPixel + b] = source[
                                from + col * factor * bytesPerPixel + b
                            ];
                        }
                    }
                }
            }
        }

        // 接管刚刚填好的数组，不再复制；新存储计数从1开始，与原图租约独立。
        return Owned(info, result);
    }

    /// <summary>将像素复制到调用方数组，不泄露底层存储的可写引用。</summary>
    /// <param name="sourceOffset">源图起始字节偏移，不是像素坐标。</param>
    /// <param name="destination">调用方拥有的目标数组。</param>
    /// <param name="destinationOffset">目标数组的起始字节偏移。</param>
    /// <param name="count">复制字节数；源和目标范围都必须有效。</param>
    public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (
            sourceOffset < 0
            || destinationOffset < 0
            || count < 0
            || (long)sourceOffset + count > Info.ByteLength
            || (long)destinationOffset + count > destination.Length
        )
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_gate)
        {
            Buffer.BlockCopy(Alive().Bytes, sourceOffset, destination, destinationOffset, count);
        }
    }

    /// <summary>幂等释放当前租约；当前读操作结束后才允许底层存储回池。</summary>
    public void Dispose()
    {
        Storage? old;
        lock (_gate)
        {
            old = _storage;
            _storage = null;
        }

        old?.Release();
    }

    private Storage Alive()
    {
        return _storage ?? throw new ObjectDisposedException(nameof(MemoryImageSource));
    }
}
