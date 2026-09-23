using System;

namespace DP.Vision;

/// <summary>独占写租约；Publish移交存储所有权后，当前句柄永久失去写权限。</summary>
public sealed class FrameWriter : IDisposable
{
    private readonly object _gate = new object();
    private byte[]? _bytes;
    private readonly Action<byte[]> _release;
    private readonly ImageInfo _info;

    internal FrameWriter(ImageInfo info, byte[] bytes, Action<byte[]> release)
    {
        _info = info;
        _bytes = bytes;
        _release = release;
    }

    /// <summary>将输入字节复制到当前独占槽位。</summary>
    /// <param name = "offset">槽位内的目标起始字节偏移。</param>
    /// <param name = "source">调用方拥有的源像素数组。</param>
    /// <param name = "sourceOffset">源数组起始字节偏移。</param>
    /// <param name = "count">复制字节数；源、目标范围都必须有效，发布后禁止写入。</param>
    public void Write(int offset, byte[] source, int sourceOffset, int count)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (
            offset < 0
            || sourceOffset < 0
            || count < 0
            || (long)offset + count > _info.ByteLength
            || (long)sourceOffset + count > source.Length
        )
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_gate)
        {
            Buffer.BlockCopy(
                source,
                sourceOffset,
                _bytes ?? throw new ObjectDisposedException(nameof(FrameWriter)),
                offset,
                count
            );
        }
    }

    /// <summary>不复制像素，将当前槽位移交为只读图像；只能成功调用一次。</summary>
    /// <returns>拥有该槽位的统一图像源，由调用方释放；显示和处理可内部Retain，最后一个租约结束后才回池。</returns>
    public IImageSource Publish()
    {
        lock (_gate)
        {
            var bytes = _bytes ?? throw new ObjectDisposedException(nameof(FrameWriter));
            // 先完成只读源的创建，再交出写句柄；创建失败时仍能由writer.Dispose归还槽位。
            var source = new MemoryImageSource(new ImageBuffer(_info, new Storage(bytes, _release)));
            _bytes = null;
            return source;
        }
    }

    /// <summary>归还尚未发布的槽位；已发布图像有独立生命周期，不受此调用影响。</summary>
    public void Dispose()
    {
        byte[]? old;
        lock (_gate)
        {
            old = _bytes;
            _bytes = null;
        }

        if (old != null)
        {
            _release(old);
        }
    }
}
