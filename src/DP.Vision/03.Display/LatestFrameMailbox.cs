using System;

namespace DP.Vision;

/// <summary>容量为1、只保留最新帧的预览黑盒；拒绝旧序号并及时释放被替换的预览租约。</summary>
public sealed class LatestFrameMailbox : IDisposable
{
    private readonly object _gate = new object();
    private CanvasFrame? _pending;
    private long _last = -1;
    private bool _disposed;

    /// <summary>发布保留后的预览包，调用方仍拥有自己的句柄；此丢旧保新策略不能用于检测任务。</summary>
    /// <param name = "frame">待发布的预览包。</param>
    /// <returns>是否接受本帧；旧序号或已关闭邮箱不会接受。</returns>
    public bool Post(CanvasFrame frame)
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        CanvasFrame? old;
        lock (_gate)
        {
            // 画布关闭后生产者线程可能仍在提交：按文档返回false，不向生产者抛出异常。
            if (_disposed || frame.Sequence <= _last)
            {
                return false;
            }

            var next = frame.Retain();
            old = _pending;
            _pending = next;
            _last = frame.Sequence;
        }

        old?.Dispose();
        return true;
    }

    /// <summary>同步显示后推进序号水位，并释放过期的待显示预览包。</summary>
    /// <param name = "sequence">已显示的非负预览序号。</param>
    public void AdvanceTo(long sequence)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        CanvasFrame? old = null;
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LatestFrameMailbox));
            }

            if (sequence > _last)
            {
                _last = sequence;
            }

            if (_pending != null && _pending.Sequence <= sequence)
            {
                old = _pending;
                _pending = null;
            }
        }

        old?.Dispose();
    }

    /// <summary>取走最新待显示帧的所有权，同时清空邮箱。</summary>
    /// <returns>调用方需释放的预览包；没有新帧时为null。</returns>
    public CanvasFrame? Take()
    {
        lock (_gate)
        {
            var result = _pending;
            _pending = null;
            return result;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        CanvasFrame? old;
        lock (_gate)
        {
            _disposed = true;
            old = _pending;
            _pending = null;
        }

        old?.Dispose();
    }
}
