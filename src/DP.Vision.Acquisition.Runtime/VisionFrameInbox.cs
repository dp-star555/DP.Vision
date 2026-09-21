using System;
using System.Collections.Generic;
using System.Globalization;

namespace DP.Vision.Acquisition;

/// <summary>
/// 待领取帧的条目；Epoch 与 ReceivedSequence 只在 Runtime 内部和诊断中使用，不进入公共数据接口。
/// <para>所有权：条目进入队列后由队列负责释放，直到被成功领取才转给领取方。</para>
/// </summary>
internal sealed class VisionFrameInboxEntry
{
    /// <summary>创建条目。</summary>
    /// <param name="captureId">本帧的采集身份。</param>
    /// <param name="receivedSequence">Runtime 取得所有权时分配的接收序号。</param>
    /// <param name="frame">Provider 交付的中立帧。</param>
    /// <param name="receivedAtUtc">Runtime 接收该帧的时刻。</param>
    /// <param name="epoch">接收时的采集代次。</param>
    /// <exception cref="ArgumentNullException">帧为空。</exception>
    public VisionFrameInboxEntry(
        string captureId,
        long receivedSequence,
        VisionProviderFrame frame,
        DateTimeOffset receivedAtUtc,
        int epoch)
    {
        CaptureId = captureId ?? throw new ArgumentNullException(nameof(captureId));
        ReceivedSequence = receivedSequence;
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        ReceivedAtUtc = receivedAtUtc;
        Epoch = epoch;
        ByteLength = frame.Image.Info.ByteLength;
    }

    /// <summary>本帧的采集身份。</summary>
    public string CaptureId { get; }

    /// <summary>接收序号；由 Runtime 成功取得帧所有权时递增。</summary>
    public long ReceivedSequence { get; }

    /// <summary>Provider 交付的中立帧。</summary>
    public VisionProviderFrame Frame { get; }

    /// <summary>设备报告或 Provider 观测的采集时刻。</summary>
    public DateTimeOffset CapturedAtUtc => Frame.CapturedAtUtc;

    /// <summary>Runtime 接收该帧的时刻。</summary>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>设备可选提供的帧序号。</summary>
    public long? DeviceSequence => Frame.DeviceSequence;

    /// <summary>接收时的采集代次。</summary>
    public int Epoch { get; }

    /// <summary>逻辑像素字节数。</summary>
    public int ByteLength { get; }
}

/// <summary>一次领取尝试的结果。</summary>
internal readonly struct VisionFrameInboxClaim
{
    /// <summary>创建领取结果。</summary>
    /// <param name="entry">成功领取的条目；无可用帧时为空。</param>
    /// <param name="expiredCount">本次调用中因超龄被释放的条目数。</param>
    /// <param name="staleEpochCount">本次调用中因代次过期被释放的条目数。</param>
    public VisionFrameInboxClaim(VisionFrameInboxEntry? entry, int expiredCount, int staleEpochCount)
    {
        Entry = entry;
        ExpiredCount = expiredCount;
        StaleEpochCount = staleEpochCount;
    }

    /// <summary>成功领取的条目；无可用帧时为空。</summary>
    public VisionFrameInboxEntry? Entry { get; }

    /// <summary>因超龄被释放的条目数。</summary>
    public int ExpiredCount { get; }

    /// <summary>因代次过期被释放的条目数。</summary>
    public int StaleEpochCount { get; }
}

/// <summary>
/// 有界待领取队列；一个 ResourceSession 一个，不进入 Workflow、Provider 插件或公共数据接口。
/// <para>
/// 溢出策略固定为 <c>FaultSource</c>：拒绝新帧并让调用方标记 Source 故障，不做 DropOldest、不静默覆盖。
/// </para>
/// </summary>
internal sealed class VisionFrameInbox
{
    private readonly VisionFrameInboxPolicy _policy;
    private readonly Queue<VisionFrameInboxEntry> _entries = new Queue<VisionFrameInboxEntry>();
    private readonly object _sync = new object();
    private long _bytes;
    private long _receivedSequence;
    private int _highWatermark;
    private long _bytesHighWatermark;
    private long _receivedCount;
    private long _claimedCount;
    private long _expiredCount;
    private long _rejectedCount;
    private long _staleEpochCount;
    private long _drainedCount;

    /// <summary>创建队列。</summary>
    /// <param name="policy">组合期确定的容量、字节预算与最大帧龄。</param>
    /// <exception cref="ArgumentNullException">策略为空。</exception>
    public VisionFrameInbox(VisionFrameInboxPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>待领取帧数。</summary>
    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    /// <summary>待领取帧的逻辑像素字节总和。</summary>
    public long Bytes
    {
        get { lock (_sync) return _bytes; }
    }

    /// <summary>已接收（含被拒绝）的帧总数。</summary>
    public long ReceivedCount
    {
        get { lock (_sync) return _receivedCount; }
    }

    /// <summary>已成功领取的帧总数。</summary>
    public long ClaimedCount
    {
        get { lock (_sync) return _claimedCount; }
    }

    /// <summary>因超龄被释放的帧总数。</summary>
    public long ExpiredCount
    {
        get { lock (_sync) return _expiredCount; }
    }

    /// <summary>因容量或字节预算不足被拒绝的帧总数。</summary>
    public long RejectedCount
    {
        get { lock (_sync) return _rejectedCount; }
    }

    /// <summary>因采集代次过期被释放的帧总数。</summary>
    public long StaleEpochCount
    {
        get { lock (_sync) return _staleEpochCount; }
    }

    /// <summary>因运行退役或故障被整体释放的帧总数。</summary>
    public long DrainedCount
    {
        get { lock (_sync) return _drainedCount; }
    }

    /// <summary>待领取帧数的历史峰值。</summary>
    public int HighWatermark
    {
        get { lock (_sync) return _highWatermark; }
    }

    /// <summary>待领取字节数的历史峰值。</summary>
    public long BytesHighWatermark
    {
        get { lock (_sync) return _bytesHighWatermark; }
    }

    /// <summary>
    /// 接收一帧。所有权进入本方法即归队列：无论接受还是拒绝，队列都负责释放。
    /// </summary>
    /// <param name="frame">Provider 交付的中立帧。</param>
    /// <param name="epoch">接收时的采集代次。</param>
    /// <param name="receivedAtUtc">接收时刻。</param>
    /// <param name="receivedSequence">本次接收分配的序号；即使被拒绝也会分配，便于诊断。</param>
    /// <param name="overflowReason">被拒绝时的容量诊断；接受时为空。</param>
    /// <returns>被接受时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException">帧为空。</exception>
    public bool TryEnqueue(
        VisionProviderFrame frame,
        int epoch,
        DateTimeOffset receivedAtUtc,
        out long receivedSequence,
        out string? overflowReason)
    {
        if (frame is null)
            throw new ArgumentNullException(nameof(frame));

        bool accepted;
        lock (_sync)
        {
            receivedSequence = ++_receivedSequence;
            _receivedCount++;
            var byteLength = frame.Image.Info.ByteLength;
            if (_entries.Count >= _policy.Capacity || _bytes + byteLength > _policy.ByteBudget)
            {
                _rejectedCount++;
                overflowReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "待领取队列已满：帧数 {0}/{1}，字节 {2}+{3}/{4}。",
                    _entries.Count,
                    _policy.Capacity,
                    _bytes,
                    byteLength,
                    _policy.ByteBudget);
                accepted = false;
            }
            else
            {
                _entries.Enqueue(new VisionFrameInboxEntry(
                    Guid.NewGuid().ToString("N"),
                    receivedSequence,
                    frame,
                    receivedAtUtc,
                    epoch));
                _bytes += byteLength;
                if (_entries.Count > _highWatermark)
                    _highWatermark = _entries.Count;
                if (_bytes > _bytesHighWatermark)
                    _bytesHighWatermark = _bytes;
                overflowReason = null;
                accepted = true;
            }
        }

        // 在锁外释放：帧的 Dispose 可能触发较大的资源回收，不应挡住厂商回调线程。
        if (!accepted)
            frame.Dispose();
        return accepted;
    }

    /// <summary>
    /// 领取最早的可用帧。超龄与代次过期的条目在扫描过程中被释放并计数。
    /// <para>队列按接收顺序排列且帧龄单调递增，因此超龄条目必然出现在队首，只需从队首扫描。</para>
    /// </summary>
    /// <param name="now">当前时刻，用于判定超龄。</param>
    /// <param name="epoch">当前采集代次；其他代次的条目一律不领取。</param>
    /// <returns>领取结果。</returns>
    public VisionFrameInboxClaim TryClaim(DateTimeOffset now, int epoch)
    {
        VisionFrameInboxEntry? claimed = null;
        List<VisionFrameInboxEntry>? dropped = null;
        int expired = 0;
        int stale = 0;

        lock (_sync)
        {
            while (_entries.Count > 0)
            {
                var head = _entries.Peek();
                if (head.Epoch != epoch)
                {
                    _entries.Dequeue();
                    _bytes -= head.ByteLength;
                    _staleEpochCount++;
                    stale++;
                    (dropped ??= new List<VisionFrameInboxEntry>()).Add(head);
                    continue;
                }

                if (now - head.ReceivedAtUtc > _policy.MaximumFrameAge)
                {
                    _entries.Dequeue();
                    _bytes -= head.ByteLength;
                    _expiredCount++;
                    expired++;
                    (dropped ??= new List<VisionFrameInboxEntry>()).Add(head);
                    continue;
                }

                _entries.Dequeue();
                _bytes -= head.ByteLength;
                _claimedCount++;
                claimed = head;
                break;
            }
        }

        Release(dropped);
        return new VisionFrameInboxClaim(claimed, expired, stale);
    }

    /// <summary>清退所有非当前代次的条目，用于根运行开始新一轮时丢弃上一轮遗留帧。</summary>
    /// <param name="epoch">当前采集代次。</param>
    /// <returns>被释放的条目数。</returns>
    public int DropStaleEpochs(int epoch)
    {
        List<VisionFrameInboxEntry>? dropped = null;
        lock (_sync)
        {
            var kept = new List<VisionFrameInboxEntry>(_entries.Count);
            while (_entries.Count > 0)
            {
                var entry = _entries.Dequeue();
                if (entry.Epoch == epoch)
                {
                    kept.Add(entry);
                    continue;
                }

                _bytes -= entry.ByteLength;
                _staleEpochCount++;
                (dropped ??= new List<VisionFrameInboxEntry>()).Add(entry);
            }

            foreach (var entry in kept)
                _entries.Enqueue(entry);
        }

        Release(dropped);
        return dropped?.Count ?? 0;
    }

    /// <summary>
    /// 移出全部待领取条目并把所有权转移给调用方，用于运行退役或故障收口。
    /// <para>队列<b>不</b>释放它们：调用方拿到的是仍然有效的句柄，必须自行释放。</para>
    /// </summary>
    /// <returns>被移出的条目；调用方负责释放。</returns>
    public IReadOnlyList<VisionFrameInboxEntry> Drain()
    {
        List<VisionFrameInboxEntry> drained;
        lock (_sync)
        {
            drained = new List<VisionFrameInboxEntry>(_entries.Count);
            while (_entries.Count > 0)
            {
                var entry = _entries.Dequeue();
                _bytes -= entry.ByteLength;
                drained.Add(entry);
            }

            _drainedCount += drained.Count;
        }

        return drained;
    }

    private static void Release(List<VisionFrameInboxEntry>? entries)
    {
        if (entries is null)
            return;
        foreach (var entry in entries)
            entry.Frame.Dispose();
    }
}
