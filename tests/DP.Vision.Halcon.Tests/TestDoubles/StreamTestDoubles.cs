using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// 可控的假 HALCON 长连接设备。
/// <para>
/// 与 Basler 侧的假相机不同，HALCON 没有事件回调，采集循环由会话自建并**主动拉取**，
/// 因此这里做成"脚本队列 + 阻塞拉取"：测试把帧、超时或故障按顺序放进队列，
/// 采集循环逐项取走。逐帧推动而不是靠计时器，断言才是确定性的。
/// </para>
/// <para>
/// <see cref="AbortGrab"/> 默认只发出**一次性**中止信号：真实 HALCON 的 <c>do_abort_grab</c>
/// 是否被支持取决于采集接口，所以"中止成功"与"中止无效、只能等满抓取超时"两种情形都要能表达。
/// </para>
/// </summary>
internal sealed class FakeHalconStreamCamera : IHalconStreamCamera
{
    private readonly ConcurrentQueue<object> _script = new ConcurrentQueue<object>();
    private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
    private readonly SemaphoreSlim _grabEntered = new SemaphoreSlim(0);
    private readonly object _eventsSync = new object();
    private readonly List<string> _events = new List<string>();

    private int _openCount;
    private int _closeCount;
    private int _disposeCount;
    private int _grabCount;
    private int _abortCount;
    private int _abortPending;
    private int _disposed;

    /// <summary>打开时抛出的异常；用于覆盖"布防在打开阶段就失败"。</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>写入布防参数时抛出的异常；用于覆盖"参数写不进设备"。</summary>
    public Exception? ApplyFailure { get; set; }

    /// <summary>中止抓取是否真的能打断阻塞中的拉取；为 <see langword="false"/> 表示该接口不支持 do_abort_grab。</summary>
    public bool AbortUnblocksGrab { get; set; } = true;

    /// <summary>设备是否已打开。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>打开次数。</summary>
    public int OpenCount => Volatile.Read(ref _openCount);

    /// <summary>关闭次数。</summary>
    public int CloseCount => Volatile.Read(ref _closeCount);

    /// <summary>释放次数。</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>抓取调用次数（含被中止的那一次）。</summary>
    public int GrabCount => Volatile.Read(ref _grabCount);

    /// <summary>中止抓取的调用次数。</summary>
    public int AbortCount => Volatile.Read(ref _abortCount);

    /// <summary>最近一次写入的触发模式。</summary>
    public EVisionTriggerMode? AppliedTriggerMode { get; private set; }

    /// <summary>最近一次写入的触发源。</summary>
    public string? AppliedTriggerSource { get; private set; }

    /// <summary>最近一次写入的抓取超时。</summary>
    public int? AppliedGrabTimeout { get; private set; }

    /// <summary>按发生顺序记录的设备侧动作。</summary>
    public IReadOnlyList<string> Events
    {
        get { lock (_eventsSync) return _events.ToArray(); }
    }

    /// <summary>放入一帧；采集循环下一次拉取时取走。</summary>
    /// <param name="frame">设备帧。</param>
    public void Feed(IHalconGrabFrame frame) => Enqueue(frame ?? throw new ArgumentNullException(nameof(frame)));

    /// <summary>放入一次抓取超时。</summary>
    /// <param name="message">诊断文本。</param>
    public void FeedTimeout(string message = "假设备：抓取超时。") =>
        Enqueue(new HalconGrabTimeoutException(message));

    /// <summary>放入一次设备侧故障。</summary>
    /// <param name="failure">故障。</param>
    public void FeedFault(Exception failure) => Enqueue(failure ?? throw new ArgumentNullException(nameof(failure)));

    /// <summary>等待采集循环进入一次拉取；用于让"循环已经在等帧"成为确定性事实。</summary>
    /// <param name="timeoutMilliseconds">等待上限。</param>
    /// <returns>进入过拉取时返回 <see langword="true"/>。</returns>
    public bool WaitForGrabEntered(int timeoutMilliseconds = 5000) =>
        _grabEntered.Wait(timeoutMilliseconds);

    /// <inheritdoc/>
    public void Open()
    {
        if (IsOpen)
            return;
        Record("Open");
        if (OpenFailure is not null)
            throw OpenFailure;
        IsOpen = true;
        Interlocked.Increment(ref _openCount);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (!IsOpen)
            return;
        Record("Close");
        IsOpen = false;
        Interlocked.Increment(ref _closeCount);
    }

    /// <inheritdoc/>
    public void ApplyArmParameters(
        EVisionTriggerMode triggerMode,
        string? triggerSource,
        int grabTimeoutMilliseconds)
    {
        Record("Apply");
        if (ApplyFailure is not null)
            throw ApplyFailure;
        AppliedTriggerMode = triggerMode;
        AppliedTriggerSource = triggerSource;
        AppliedGrabTimeout = grabTimeoutMilliseconds;
    }

    /// <inheritdoc/>
    public IHalconGrabFrame GrabOnce()
    {
        Interlocked.Increment(ref _grabCount);
        _grabEntered.Release();

        while (true)
        {
            if (_script.TryDequeue(out var item))
            {
                Interlocked.Exchange(ref _abortPending, 0);
                return Materialize(item);
            }

            if (Volatile.Read(ref _abortPending) != 0)
            {
                Interlocked.Exchange(ref _abortPending, 0);
                throw new HalconGrabTimeoutException("假设备：抓取已被中止。");
            }

            _available.Wait();
        }
    }

    /// <inheritdoc/>
    public void AbortGrab()
    {
        Interlocked.Increment(ref _abortCount);
        Record("Abort");
        if (!AbortUnblocksGrab)
            return;
        Interlocked.Exchange(ref _abortPending, 1);
        _available.Release();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 先关再记，使 Events 的顺序读起来就是"关闭然后释放"。
        Close();
        Record("Dispose");
        Interlocked.Increment(ref _disposeCount);
    }

    private void Enqueue(object item)
    {
        _script.Enqueue(item);
        _available.Release();
    }

    private static IHalconGrabFrame Materialize(object item)
    {
        switch (item)
        {
            case IHalconGrabFrame frame:
                return frame;
            case Exception failure:
                throw failure;
            default:
                throw new InvalidOperationException("假设备脚本项类型不受支持：" + item.GetType().FullName);
        }
    }

    private void Record(string action)
    {
        lock (_eventsSync)
            _events.Add(action);
    }
}

/// <summary>
/// 可控的假设备帧。像素由测试给出，通道排布与真实实现一致（0 红、1 绿、2 蓝）。
/// <para>
/// <see cref="CopyChannelInto"/> 复刻真实实现的目标缓冲长度校验：这样"中立层是否给出正确大小的缓冲"
/// 在没有 SDK 的机器上也能被咬住，而不是只在现场才暴露。
/// </para>
/// </summary>
internal sealed class FakeHalconGrabFrame : IHalconGrabFrame
{
    private readonly byte[][] _channels;

    /// <summary>创建假设备帧。</summary>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="pixelTypeName">像素类型名。</param>
    /// <param name="channels">逐通道的原始像素。</param>
    public FakeHalconGrabFrame(int width, int height, string pixelTypeName, params byte[][] channels)
    {
        Width = width;
        Height = height;
        PixelTypeName = pixelTypeName;
        _channels = channels ?? Array.Empty<byte[]>();
        ChannelCount = _channels.Length;
        CapturedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public string PixelTypeName { get; }

    /// <inheritdoc/>
    public int ChannelCount { get; }

    /// <inheritdoc/>
    public DateTimeOffset CapturedAtUtc { get; }

    /// <summary>释放次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>逐通道复制请求的长度；用于断言中立层给出的缓冲大小正确。</summary>
    public IReadOnlyList<int> CopyLengths { get; } = new List<int>();

    /// <inheritdoc/>
    public void CopyChannelInto(int channel, byte[] destination)
    {
        if (destination is null)
            throw new ArgumentNullException(nameof(destination));

        var bytesPerPixel = PixelTypeName == "uint2" ? 2 : 1;
        var expected = checked(Width * Height * bytesPerPixel);
        if (destination.Length != expected)
        {
            throw new ArgumentException(
                $"目标缓冲长度必须等于 Width*Height*每像素字节数（{expected}），实得 {destination.Length}。",
                nameof(destination));
        }

        if (channel < 0 || channel >= _channels.Length)
            throw new ArgumentOutOfRangeException(nameof(channel));

        Buffer.BlockCopy(_channels[channel], 0, destination, 0, destination.Length);
        ((List<int>)CopyLengths).Add(destination.Length);
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeCount++;
}

/// <summary>记录交付与结束通知的假接收方；可选地模拟"接收方违约"与"交付中阻塞"。</summary>
internal sealed class RecordingStreamSink : IVisionProviderFrameSink
{
    private readonly List<VisionProviderFrame> _frames = new List<VisionProviderFrame>();
    private readonly List<Exception?> _completions = new List<Exception?>();
    private readonly SemaphoreSlim _delivered = new SemaphoreSlim(0);
    private readonly SemaphoreSlim _completed = new SemaphoreSlim(0);

    /// <summary>交付时抛出的异常；模拟接收方违约。</summary>
    public Exception? PublishFailure { get; set; }

    /// <summary>交付进入时置位；与 <see cref="Release"/> 配合让交付停在半途。</summary>
    public ManualResetEventSlim? Entered { get; set; }

    /// <summary>交付等待它被置位后才继续。</summary>
    public ManualResetEventSlim? Release { get; set; }

    /// <summary>已交付的帧。</summary>
    public IReadOnlyList<VisionProviderFrame> Frames
    {
        get { lock (_frames) return _frames.ToArray(); }
    }

    /// <summary>收到的结束通知。</summary>
    public IReadOnlyList<Exception?> Completions
    {
        get { lock (_completions) return _completions.ToArray(); }
    }

    /// <inheritdoc/>
    public void Publish(VisionProviderFrame frame)
    {
        Entered?.Set();
        Release?.Wait(TimeSpan.FromSeconds(30));

        if (PublishFailure is not null)
        {
            // 公共契约：接收方即使拒绝也必须释放帧。
            frame.Dispose();
            throw PublishFailure;
        }

        lock (_frames)
            _frames.Add(frame);
        _delivered.Release();
    }

    /// <inheritdoc/>
    public void Complete(Exception? failure)
    {
        lock (_completions)
            _completions.Add(failure);
        _completed.Release();
    }

    /// <summary>释放已经记录的帧；测试收尾用。</summary>
    public void DisposeFrames()
    {
        VisionProviderFrame[] frames;
        lock (_frames)
        {
            frames = _frames.ToArray();
            _frames.Clear();
        }

        foreach (var frame in frames)
            frame.Dispose();
    }

    /// <summary>等待至少 <paramref name="count"/> 帧交付完成。</summary>
    /// <param name="count">期望帧数。</param>
    /// <param name="timeoutMilliseconds">等待上限。</param>
    /// <returns>达到期望帧数时返回 <see langword="true"/>。</returns>
    public bool WaitForFrames(int count, int timeoutMilliseconds = 5000) =>
        WaitFor(_delivered, count, timeoutMilliseconds);

    /// <summary>等待收到至少 <paramref name="count"/> 次结束通知。</summary>
    /// <param name="count">期望通知数。</param>
    /// <param name="timeoutMilliseconds">等待上限。</param>
    /// <returns>达到期望通知数时返回 <see langword="true"/>。</returns>
    public bool WaitForCompletions(int count, int timeoutMilliseconds = 5000) =>
        WaitFor(_completed, count, timeoutMilliseconds);

    private static bool WaitFor(SemaphoreSlim signal, int count, int timeoutMilliseconds)
    {
        for (var index = 0; index < count; index++)
        {
            if (!signal.Wait(timeoutMilliseconds))
                return false;
        }

        return true;
    }
}
