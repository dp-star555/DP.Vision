using System;
using System.Collections.Generic;

namespace DP.Vision;

/// <summary>固定图像布局的有界缓冲池。耗尽时明确返回失败，不覆盖仍被读取的帧。</summary>
public sealed class FrameBufferPool : IDisposable
{
    private readonly object _gate = new object();
    private readonly Stack<byte[]> _free = new Stack<byte[]>();
    private readonly int _capacity;
    private int _allocated;
    private bool _disposed;

    /// <summary>创建具有固定容量和字节预算的缓冲池，实际缓冲区按需分配。</summary>
    /// <param name = "info">池内所有图像共用的尺寸和像素布局。</param>
    /// <param name = "capacity">最大槽位数，范围1–1024。</param>
    /// <param name = "byteBudget">允许的总像素字节预算，必须容纳capacity个完整图像。</param>
    public FrameBufferPool(ImageInfo info, int capacity, long byteBudget)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        if (capacity < 1 || capacity > 1024 || (long)capacity * info.ByteLength > byteBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    /// <summary>池内所有槽位固定使用的图像布局。</summary>
    public ImageInfo Info { get; }

    /// <summary>尝试取得已清零的独占写槽位；池满表示背压，不等于允许静默丢弃检测任务。</summary>
    /// <param name = "writer">成功时返回写租约，失败时为null；写完须Publish或Dispose。</param>
    /// <returns>是否取得可写槽位。</returns>
    public bool TryRent(out FrameWriter? writer)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FrameBufferPool));
            }

            byte[] bytes;
            if (_free.Count > 0)
            {
                bytes = _free.Pop();
                Array.Clear(bytes, 0, bytes.Length);
            }
            else
            {
                //满了不允许新建了
                if (_allocated == _capacity)
                {
                    writer = null;
                    return false;
                }

                bytes = new byte[Info.ByteLength];
                _allocated++;
            }

            writer = new FrameWriter(Info, bytes, Return);
            return true;
        }
    }

    private void Return(byte[] bytes)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _free.Push(bytes);
            }
            else
            {
                _allocated--;
            }
        }
    }

    /// <summary>释放空闲存储；已借出的读者仍有效，最后释放时不再将其存储回池。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _allocated -= _free.Count;
            _free.Clear();
        }
    }
}
