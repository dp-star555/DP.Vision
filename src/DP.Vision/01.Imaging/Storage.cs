using System;

namespace DP.Vision;

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
