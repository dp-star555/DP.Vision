using System;
using System.Collections.Generic;
using System.Threading;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 确定性帧接收方：按调用顺序记录每一帧，并可注入"拒绝"与"阻塞"两种行为。
/// <para>
/// 所有权语义按契约实现：<see cref="Publish"/> 一旦进入就由本对象负责释放帧——
/// 即使随后抛异常，也先释放再抛，绝不让帧泄漏。
/// </para>
/// </summary>
internal sealed class RecordingFrameSink : IVisionProviderFrameSink
{
    private readonly List<long> _receivedSequences = new List<long>();
    private readonly List<string> _violations = new List<string>();
    private readonly object _gate = new object();
    private readonly ManualResetEventSlim? _entered;
    private readonly ManualResetEventSlim? _release;
    private int _publishCount;
    private int _disposedCount;
    private int _completedCount;
    private Exception? _completionFailure;
    private bool _completed;

    /// <summary>创建接收方。</summary>
    /// <param name="entered">非空时，每次进入 <see cref="Publish"/> 都先置位，用于让测试观察到"回调已进入"。</param>
    /// <param name="release">非空时，每次 <see cref="Publish"/> 都等待它，用于制造"回调进行中"的窗口。</param>
    public RecordingFrameSink(ManualResetEventSlim? entered = null, ManualResetEventSlim? release = null)
    {
        _entered = entered;
        _release = release;
    }

    /// <summary>拒绝接收：先释放帧，再抛异常。用于验证接收方拒绝时帧不泄漏。</summary>
    public bool RejectFrames { get; set; }

    /// <summary>
    /// 交付过程中的观察点，在释放等待之后、拒绝判断之前调用。
    /// <para>
    /// 必须在这里观察设备状态：调用方在 <c>Emit</c> 返回后再读，锁已经释放，
    /// 观察到的可能是"释放之后"的状态，从而把竞态误判成实现缺陷。
    /// </para>
    /// </summary>
    public Action? AfterDelivery { get; set; }

    /// <summary>成功进入 <see cref="Publish"/> 的次数。</summary>
    public int PublishCount => Volatile.Read(ref _publishCount);

    /// <summary>已释放的帧数；与 <see cref="PublishCount"/> 相等才说明没有泄漏。</summary>
    public int DisposedCount => Volatile.Read(ref _disposedCount);

    /// <summary><see cref="Complete"/> 的调用次数。</summary>
    public int CompletedCount => Volatile.Read(ref _completedCount);

    /// <summary><see cref="Complete"/> 携带的失败原因。</summary>
    public Exception? CompletionFailure
    {
        get { lock (_gate) return _completionFailure; }
    }

    /// <summary>接收到的设备帧序号，按到达顺序。</summary>
    public IReadOnlyList<long> ReceivedSequences
    {
        get { lock (_gate) return _receivedSequences.ToArray(); }
    }

    /// <summary>协议违规记录，例如 Complete 之后仍收到帧。</summary>
    public IReadOnlyList<string> Violations
    {
        get { lock (_gate) return _violations.ToArray(); }
    }

    /// <inheritdoc/>
    public void Publish(VisionProviderFrame frame)
    {
        if (frame is null)
            throw new ArgumentNullException(nameof(frame));

        try
        {
            Interlocked.Increment(ref _publishCount);
            lock (_gate)
            {
                if (_completed)
                    _violations.Add("Complete 之后仍然收到 Publish。");
                if (frame.DeviceSequence is { } sequence)
                    _receivedSequences.Add(sequence);
            }

            _entered?.Set();
            _release?.Wait();
            AfterDelivery?.Invoke();

            if (RejectFrames)
                throw new InvalidOperationException("接收方拒绝该帧（模拟队列已满或已退役）。");
        }
        finally
        {
            // 所有权已转移给接收方，因此无论接受还是拒绝都要释放。
            frame.Dispose();
            Interlocked.Increment(ref _disposedCount);
        }
    }

    /// <inheritdoc/>
    public void Complete(Exception? failure)
    {
        lock (_gate)
        {
            if (_completed)
                _violations.Add("Complete 被重复调用。");
            _completed = true;
            _completionFailure = failure;
        }

        Interlocked.Increment(ref _completedCount);
    }
}
