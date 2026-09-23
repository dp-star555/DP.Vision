using System;

namespace DP.Vision;

/// <summary>只读图像租约。Dispose只释放当前句柄，不影响其他Retain句柄；不向外暴露可修改的底层数组。</summary>
internal sealed partial class ImageBuffer : IDisposable
{
    private Storage? _storage;
    private readonly object _gate = new object();

    internal ImageBuffer(ImageInfo info, Storage storage)
    {
        Info = info;
        _storage = storage;
    }

    /// <summary>复制调用方数据；调用方随后修改输入数组不会改变本图像。</summary>
    /// <param name = "info">原图尺寸及像素布局。</param>
    /// <param name = "pixels">待复制的像素数组，长度必须等于info.ByteLength。</param>
    /// <returns>拥有独立数据的只读租约，由调用方释放。</returns>
    public static ImageBuffer CopyFrom(ImageInfo info, byte[] pixels)
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
    internal static ImageBuffer Owned(ImageInfo info, byte[] bytes)
    {
        return new ImageBuffer(info, new Storage(bytes, null));
    }

    /// <summary>不可变的原图布局信息。</summary>
    public ImageInfo Info { get; }

    /// <summary>取得共享像素存储但生命周期独立的新租约，不复制像素。</summary>
    /// <returns>需独立Dispose的新句柄；原句柄释放后仍可使用。</returns>
    public ImageBuffer Retain()
    {
        lock (_gate)
        {
            var s = Alive();
            s.Add();
            return new ImageBuffer(Info, s);
        }
    }

    private Storage Alive()
    {
        return _storage ?? throw new ObjectDisposedException(nameof(ImageBuffer));
    }

    /// <summary>将像素复制到调用方数组，不泄露底层存储的可写引用。</summary>
    /// <param name = "sourceOffset">源图起始字节偏移，不是像素坐标。</param>
    /// <param name = "destination">调用方拥有的目标数组。</param>
    /// <param name = "destinationOffset">目标数组的起始字节偏移。</param>
    /// <param name = "count">复制字节数；源和目标范围都必须有效。</param>
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

    /// <summary>
    /// 从原图按规则网格生成一块独立小图。level为0时复制原分辨率图块；
    /// level大于0时跳行、跳列取样，使同尺寸小图表示更大的原图范围。
    /// </summary>
    /// <param name="level">采样级别2^n；调用方须保证范围0–20。0每个像素都取，1取第0、2、4等行列，2取第0、4、8等行列。</param>
    /// <param name="tileX">缩小后逻辑图像中的图块列号，从0开始，不是原图X坐标。</param>
    /// <param name="tileY">缩小后逻辑图像中的图块行号，从0开始，不是原图Y坐标。</param>
    /// <param name="tileSize">输出图块的常规边长，单位为输出像素；调用方须保证范围16–1024，图像边缘不足时返回较小图块。</param>
    /// <returns>拥有新数组和独立初始租约的小图，由调用方Dispose；保留原像素布局，不携带原图放置坐标。</returns>
    internal ImageBuffer Sample(int level, int tileX, int tileY, int tileSize)
    {
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
        if (x < 0 || y < 0 || x >= levelWidth || y >= levelHeight)
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

            // 变量名channels沿用现有实现，实际含义是“每像素字节数”，不是颜色通道数。
            // 例如Gray16虽为单通道，这里的值仍是2，要把两个字节一起复制。
            int channels = info.BytesPerPixel;
            for (int row = 0; row < info.Height; row++)
            {
                // row是小图行号。(y+row)*factor得到本行对应的原图行号，x*factor得到原图起始列。
                // from = (原图行号*原图宽度+原图起始列)*每像素字节数，是源数组的字节偏移。
                // to = 小图行号*小图行跨度，是目标数组的字节偏移。两者都不是复制长度。
                int from = ((y + row) * factor * Info.Width + x * factor) * channels,
                    to = row * info.Stride;
                if (factor == 1)
                {
                    // 不缩小时，本行需要的像素连续，直接复制一条小图行。
                    // 下一次循环重新定位原图下一行，跳过原图中不属于该图块的部分。
                    Buffer.BlockCopy(source, from, result, to, info.Stride);
                }
                else if (channels == 1)
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
                        for (int channel = 0; channel < channels; channel++)
                        {
                            result[to + col * channels + channel] = source[
                                from + col * factor * channels + channel
                            ];
                        }
                    }
                }
            }
        }

        // 接管刚刚填好的数组，不再复制；新存储计数从1开始，与原图租约独立。
        return Owned(info, result);
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
}

/// <summary>
/// 多个图像租约共享的底层像素存储。持有一个数组并统计使用它的租约数量，
/// 不在Retain时复制像素；最后一个租约释放后，才通知提供者回收或复用数组。
/// </summary>
internal sealed class Storage
{
    /// <summary>
    /// 共享的实际像素数组。
    /// </summary>
    internal readonly byte[] Bytes;

    /// <summary>尚未释放的租约数量；</summary>
    private int _references = 1;

    /// <summary>
    /// 最后一个租约释放时调用的回收函数，例如归还缓冲池。
    /// </summary>
    private readonly Action<byte[]>? _release;

    /// <summary>接管已准备好的像素数组，建立初始计数为1的共享存储；不复制或校验数组。</summary>
    /// <param name="bytes">已由内部调用方保证有效、尺寸正确的数组，发布后不得再通过其他可写引用修改。</param>
    /// <param name="release">可选回收回调，接收原数组；没有池化或自定义回收需求时传null。</param>
    internal Storage(byte[] bytes, Action<byte[]>? release)
    {
        Bytes = bytes;
        _release = release;
    }

    /// <summary>为一个新的独立租约原子增加引用计数，不复制像素。</summary>
    internal void Add()
    {
        // 多个独立句柄可能同时保留同一存储，普通自增不能保证计数正确。
        System.Threading.Interlocked.Increment(ref _references);
    }

    /// <summary>原子减少一个租约引用，仅在计数降到0时同步执行回收回调。</summary>
    internal void Release()
    {
        // 必须等所有使用者都放弃租约后才能回池，否则下一帧可能覆盖仍在读取的图像。
        if (System.Threading.Interlocked.Decrement(ref _references) == 0)
        {
            _release?.Invoke(Bytes);
        }
    }
}
