using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition;

/// <summary>ResourceSession 的生命周期状态。</summary>
internal enum EVisionResourceState
{
    /// <summary>已创建但尚未打开设备。</summary>
    Created = 0,

    /// <summary>正在打开设备或布防接收流。</summary>
    Opening = 1,

    /// <summary>已布防，接收生产帧。</summary>
    Armed = 2,

    /// <summary>已故障；拒绝新的采集与领取，直到宿主执行显式恢复。</summary>
    Faulted = 3,

    /// <summary>正在停止：先关闭接受门，再停流，再等待在途操作退出。</summary>
    Stopping = 4,

    /// <summary>已释放。</summary>
    Disposed = 5
}

/// <summary>
/// 一个物理资源键对应的运行会话：拥有设备、互斥门、可选的有界待领取队列与接收流。
/// <para>
/// 一个 ResourceKey 只有一个会话，因此同一物理相机的所有互斥状态集中在一处，不会形成互不相知的锁域。
/// </para>
/// </summary>
internal sealed class VisionResourceSession : IAsyncDisposable
{
    private readonly object _sync = new object();
    private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _openGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _claimGate = new SemaphoreSlim(1, 1);

    // 只用做等待唤醒；不释放该信号量，避免"停止"与"唤醒"竞态下出现 ObjectDisposedException。
    private readonly SemaphoreSlim _frameSignal = new SemaphoreSlim(0);

    private readonly VisionFrameInbox? _inbox;
    private readonly ResourceFrameSink _sink;

    private EVisionResourceState _state = EVisionResourceState.Created;
    private EVisionConnectionState _connectionState = EVisionConnectionState.Created;
    private string? _connectionMessage;
    private VisionAcquisitionOwner? _holder;
    private IVisionAcquisitionDevice? _device;
    private IVisionAcquisitionStream? _stream;
    private string? _faultKind;
    private string? _faultMessage;
    private bool _streamCompleted;
    private string? _streamFailure;
    private int _epoch;
    private int _activeOperations;
    private TaskCompletionSource<bool>? _idle;
    private long _deviceSequenceGaps;
    private long _callbackFaults;
    private long? _lastDeviceSequence;
    private long _rejectedWhileNotArmed;
    private long _epochStartCount;
    private long _expiredTotal;
    private long _staleEpochTotal;
    private long _overflowCount;

    /// <summary>创建会话。</summary>
    /// <param name="resourceKey">物理资源键。</param>
    /// <param name="inboxPolicy">外部回调缓冲策略；主动单次采集时为空。</param>
    public VisionResourceSession(string resourceKey, VisionFrameInboxPolicy? inboxPolicy)
    {
        ResourceKey = resourceKey ?? throw new ArgumentNullException(nameof(resourceKey));
        _inbox = inboxPolicy is null ? null : new VisionFrameInbox(inboxPolicy);
        _sink = new ResourceFrameSink(this);
    }

    /// <summary>物理资源键。</summary>
    public string ResourceKey { get; }

    /// <summary>是否为外部回调缓冲会话。</summary>
    public bool IsBuffered => _inbox is not null;

    /// <summary>操作互斥门；主动单次采集按共享策略使用。</summary>
    public SemaphoreSlim OperationGate => _operationGate;

    /// <summary>设备打开门；串行化 Open/Close 状态转换。</summary>
    public SemaphoreSlim OpenGate => _openGate;

    /// <summary>当前占用方身份。</summary>
    public VisionAcquisitionOwner? Holder
    {
        get { lock (_sync) return _holder; }
    }

    /// <summary>已打开的会话设备。</summary>
    public IVisionAcquisitionDevice? Device
    {
        get { lock (_sync) return _device; }
    }

    /// <summary>是否已故障。</summary>
    public bool Faulted
    {
        get { lock (_sync) return _state == EVisionResourceState.Faulted; }
    }

    /// <summary>连接状态；连接属于软件生命周期，与取流状态分开维护。</summary>
    public EVisionConnectionState ConnectionState
    {
        get { lock (_sync) return _connectionState; }
    }

    /// <summary>连接诊断说明；成功时报告设备规范身份，失败时报告原因。</summary>
    public string? ConnectionMessage
    {
        get { lock (_sync) return _connectionMessage; }
    }

    /// <summary>故障原因。</summary>
    public string? FaultMessage
    {
        get { lock (_sync) return _faultMessage; }
    }

    /// <summary>当前采集代次。</summary>
    public int Epoch
    {
        get { lock (_sync) return _epoch; }
    }

    /// <summary>设置当前占用方。</summary>
    /// <param name="owner">占用方身份；释放时为空。</param>
    public void SetHolder(VisionAcquisitionOwner? owner)
    {
        lock (_sync)
            _holder = owner;
    }

    /// <summary>设置会话设备；调用方负责在此之前确认设备未被替换。</summary>
    /// <param name="device">已打开的设备。</param>
    public void SetDevice(IVisionAcquisitionDevice device)
    {
        lock (_sync)
            _device = device;
    }

    /// <summary>标记正在打开设备；由Runtime在调用Provider.OpenAsync之前设置。</summary>
    /// <param name="message">面向操作员的连接诊断。</param>
    public void MarkConnecting(string message)
    {
        lock (_sync)
        {
            _connectionState = EVisionConnectionState.Connecting;
            _connectionMessage = message;
        }
    }

    /// <summary>标记设备已连接；由Runtime在打开成功并校验规范身份之后设置。</summary>
    /// <param name="message">面向操作员的连接诊断，报告设备规范身份。</param>
    public void MarkConnected(string message)
    {
        lock (_sync)
        {
            _connectionState = EVisionConnectionState.Connected;
            _connectionMessage = message;
        }
    }

    /// <summary>
    /// 把会话标记为故障并释放待领取帧。
    /// <para>
    /// 释放待领取帧是必要的：故障后不再有领取路径，留着它们就是永久泄漏。
    /// </para>
    /// </summary>
    /// <param name="kind">故障类别。</param>
    /// <param name="message">面向操作员的诊断说明。</param>
    public void MarkFaulted(string kind, string message)
    {
        List<VisionFrameInboxEntry> drained = new List<VisionFrameInboxEntry>();
        bool changed = false;
        lock (_sync)
        {
            if (_state == EVisionResourceState.Disposed || _state == EVisionResourceState.Stopping)
                return;
            _state = EVisionResourceState.Faulted;
            _faultKind = kind;
            _faultMessage = message;
            _connectionState = EVisionConnectionState.Faulted;
            _connectionMessage = message;
            changed = true;
        }

        if (!changed)
            return;
        if (_inbox is not null)
            drained.AddRange(_inbox.Drain());
        foreach (var entry in drained)
            entry.Frame.Dispose();

        WakeWaiters();
    }

    /// <summary>开始新一轮采集代次并清退上一轮遗留的未领取帧。</summary>
    /// <param name="epoch">新的采集代次；必须严格递增。</param>
    /// <returns>被清退的帧数。</returns>
    /// <exception cref="InvalidOperationException">代次没有严格递增。</exception>
    public int BeginEpoch(int epoch)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (epoch <= _epoch)
                throw new InvalidOperationException(
                    $"采集代次必须严格递增：当前 {_epoch}，收到 {epoch}；旧帧不允许进入新代次。");
            _epoch = epoch;
            _epochStartCount++;
        }

        var dropped = _inbox is null ? 0 : _inbox.DropStaleEpochs(epoch);
        _staleEpochTotal += dropped;
        return dropped;
    }

    /// <summary>打开设备并布防接收流。</summary>
    /// <param name="openDevice">打开设备的委托；由 Runtime 提供以复用设备会话。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>布防完成时结束的异步操作。</returns>
    /// <exception cref="VisionSourceConfigurationException">设备不支持持续接收能力。</exception>
    public async ValueTask ArmAsync(
        Func<CancellationToken, ValueTask<IVisionAcquisitionDevice>> openDevice,
        CancellationToken cancellationToken)
    {
        if (openDevice is null)
            throw new ArgumentNullException(nameof(openDevice));

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_state == EVisionResourceState.Faulted)
                throw new VisionDeviceOfflineException(
                    $"资源键 {ResourceKey} 已标记为故障（{_faultKind}）：{_faultMessage}；请先排除故障再布防。");
            if (_stream is not null)
                return;
            _state = EVisionResourceState.Opening;
        }

        try
        {
            var device = await openDevice(cancellationToken).ConfigureAwait(false);
            if (!(device is IVisionStreamingAcquisitionDevice streaming))
            {
                throw new VisionSourceConfigurationException(
                    $"资源键 {ResourceKey} 的设备没有声明持续接收能力，无法作为外部回调缓冲源；"
                    + "请把该 Source 改回 OnDemand，或更换支持长连接回调的 Provider 设备适配。");
            }

            lock (_sync)
                _device = device;

            var stream = await streaming.StartStreamAsync(_sink, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                ThrowIfDisposed();
                _stream = stream;
                _streamCompleted = false;
                _streamFailure = null;
                _state = EVisionResourceState.Armed;
            }
        }
        catch
        {
            lock (_sync)
            {
                if (_state == EVisionResourceState.Opening)
                    _state = EVisionResourceState.Created;
            }

            throw;
        }
    }

    /// <summary>停止接收流并释放未领取帧；设备保持打开以便下次布防复用。</summary>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>停流完成时结束的异步操作。</returns>
    public async ValueTask DisarmAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IVisionAcquisitionStream? stream;
        lock (_sync)
        {
            if (_state == EVisionResourceState.Disposed)
                return;
            stream = _stream;
            _stream = null;
            if (_state == EVisionResourceState.Armed)
                _state = EVisionResourceState.Created;
        }

        // 停流必须等待已经进入的回调退出，这是接收流的契约而不是可选优化。
        if (stream is not null)
            await stream.DisposeAsync().ConfigureAwait(false);

        if (_inbox is not null)
        {
            foreach (var entry in _inbox.Drain())
                entry.Frame.Dispose();
        }
    }

    /// <summary>
    /// 领取最早的未领取帧。同一会话同一时刻只允许一个等待中的领取，第二个请求确定性冲突。
    /// </summary>
    /// <param name="timeout">等待新帧的最长时间。</param>
    /// <param name="cancellationToken">协作取消；取消不会吞掉已经到达的帧。</param>
    /// <returns>领取到的条目；所有权随之转给调用方。</returns>
    /// <exception cref="VisionResourceConflictException">已有等待中的领取。</exception>
    /// <exception cref="VisionCaptureTimeoutException">超时仍没有可用帧。</exception>
    /// <exception cref="VisionDeviceOfflineException">会话故障或接收流意外结束。</exception>
    public async ValueTask<VisionFrameInboxEntry> ClaimAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_inbox is null)
            throw new VisionSourceConfigurationException($"资源键 {ResourceKey} 不是外部回调缓冲会话，不能按队列领取。");

        // 不做排队：同一 Source 同时只允许一个等待中的 Claim，第二个请求必须确定性失败。
        if (!_claimGate.Wait(0))
            throw new VisionResourceConflictException(new VisionResourceConflictDiagnostics(
                ResourceKey,
                ResourceKey,
                "claim",
                "claim",
                null,
                null,
                EVisionSourceSharingPolicy.ExclusiveRun,
                "同一 Source 同一时刻只允许一个等待中的领取；并行第二个请求确定性冲突。"));

        try
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureClaimable();

                var claim = _inbox.TryClaim(DateTimeOffset.UtcNow, Epoch);
                _expiredTotal += claim.ExpiredCount;
                _staleEpochTotal += claim.StaleEpochCount;
                if (claim.Entry is not null)
                    return claim.Entry;

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new VisionCaptureTimeoutException(
                        $"逻辑源（资源键 {ResourceKey}）在 {timeout.TotalMilliseconds:0} ms 内没有可领取的外部触发帧；"
                        + "请检查触发接线、触发源配置与相机是否真的产生了回调。");

                await _frameSignal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _claimGate.Release();
        }
    }

    /// <summary>读取运行指标与故障诊断快照。</summary>
    /// <param name="sourceId">逻辑源标识。</param>
    /// <param name="providerId">Provider 身份。</param>
    /// <returns>诊断快照。</returns>
    public VisionSourceDiagnostics Snapshot(string sourceId, string providerId)
    {
        lock (_sync)
        {
            return new VisionSourceDiagnostics(
                sourceId,
                ResourceKey,
                providerId,
                _state.ToString(),
                _epoch,
                _inbox?.ReceivedCount ?? 0,
                _inbox?.ClaimedCount ?? 0,
                _expiredTotal,
                (_inbox?.RejectedCount ?? 0) + _rejectedWhileNotArmed,
                _inbox?.Count ?? 0,
                _inbox?.Bytes ?? 0,
                _inbox?.HighWatermark ?? 0,
                _inbox?.BytesHighWatermark ?? 0,
                _deviceSequenceGaps,
                _callbackFaults,
                _faultKind,
                _faultMessage,
                _connectionState,
                _connectionMessage);
        }
    }

    /// <summary>
    /// 尝试进入一次采集或领取操作。返回 <see langword="false"/> 表示会话正在停止或已释放。
    /// </summary>
    /// <returns>已进入时返回 <see langword="true"/>；调用方必须在 finally 中调用 <see cref="ExitOperation"/>。</returns>
    public bool TryEnterOperation()
    {
        lock (_sync)
        {
            if (_state == EVisionResourceState.Stopping || _state == EVisionResourceState.Disposed)
                return false;
            _activeOperations++;
            return true;
        }
    }

    /// <summary>退出一次采集或领取操作。</summary>
    public void ExitOperation()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (_sync)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                idle = _idle;
                _idle = null;
            }
        }

        idle?.TrySetResult(true);
    }

    /// <summary>接收一帧；由 Provider 回调线程直接调用，不得抛出。</summary>
    /// <param name="frame">Provider 交付的中立帧；所有权已转移给本会话。</param>
    public void Publish(VisionProviderFrame frame)
    {
        try
        {
            PublishCore(frame);
        }
        catch (Exception exception)
        {
            // 厂商回调线程上抛异常通常直接崩进程；这里吞掉并计入诊断。
            Interlocked.Increment(ref _callbackFaults);
            _faultKind ??= "CallbackFault";
            _faultMessage ??= exception.GetType().Name + ": " + exception.Message;
            try
            {
                frame.Dispose();
            }
            catch (Exception)
            {
                // 释放失败无可挽回，只保证不再向上传播。
            }
        }
    }

    /// <summary>声明接收流已结束；所有等待者必须立即失败而不是继续等到超时。</summary>
    /// <param name="failure">意外结束的原因；正常停止时为空。</param>
    public void Complete(Exception? failure)
    {
        lock (_sync)
        {
            _streamCompleted = true;
            _streamFailure = failure?.Message;
        }

        if (failure is not null)
        {
            // 取流意外结束（断线、设备故障）后不会再有帧到达：把会话标为故障，
            // 使后续领取拿到带原因的诊断，而不是继续等到超时后收到一句"没有帧到达"。
            // 正常停止（状态为 Stopping）时 MarkFaulted 会直接返回，不会把退役误判成故障。
            MarkFaulted("StreamFailure", $"接收流意外结束：{failure.Message}");
        }

        WakeWaiters();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Task? idleTask = null;
        IVisionAcquisitionStream? stream;
        IVisionAcquisitionDevice? device;
        lock (_sync)
        {
            if (_state == EVisionResourceState.Disposed)
                return;
            _state = EVisionResourceState.Stopping;
            _connectionState = EVisionConnectionState.Disconnecting;
            _connectionMessage = "正在按关闭顺序释放设备…";
            stream = _stream;
            _stream = null;
            device = _device;
            _device = null;
            if (_activeOperations > 0)
            {
                _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                idleTask = _idle.Task;
            }
        }

        // 1. 唤醒等待者，让它们看到"正在停止"而不是继续等到超时。
        WakeWaiters();

        // 2. 停止接收流：契约保证等待已经进入的回调退出，之后不会再有 Publish。
        if (stream is not null)
            await stream.DisposeAsync().ConfigureAwait(false);

        // 3. 释放未领取帧。
        if (_inbox is not null)
        {
            foreach (var entry in _inbox.Drain())
                entry.Frame.Dispose();
        }

        // 4. 等待在途采集与领取退出，再释放设备：不能与活动操作并发释放设备。
        if (idleTask is not null)
            await idleTask.ConfigureAwait(false);

        if (device is not null)
            await device.DisposeAsync().ConfigureAwait(false);

        lock (_sync)
        {
            _state = EVisionResourceState.Disposed;
            _connectionState = EVisionConnectionState.Disposed;
            _connectionMessage = "设备已关闭，会话已释放。";
        }
    }

    private void PublishCore(VisionProviderFrame frame)
    {
        if (frame is null)
            throw new ArgumentNullException(nameof(frame));

        long? previous;
        lock (_sync)
        {
            if (_state != EVisionResourceState.Armed || _inbox is null)
            {
                // 停止、故障或未布防期间不接受生产帧；所有权已转移，必须由这里释放。
                _rejectedWhileNotArmed++;
                frame.Dispose();
                return;
            }

            previous = _lastDeviceSequence;
            var current = frame.DeviceSequence;
            if (previous.HasValue && current.HasValue && current.Value > previous.Value + 1)
                _deviceSequenceGaps++;
            if (current.HasValue)
                _lastDeviceSequence = current;
        }

        if (!_inbox.TryEnqueue(frame, Epoch, DateTimeOffset.UtcNow, out _, out var overflowReason))
        {
            // 帧已由队列释放；溢出策略固定为 FaultSource，不做 DropOldest 或静默覆盖。
            Interlocked.Increment(ref _overflowCount);
            MarkFaulted(
                "InboxOverflow",
                $"逻辑源（资源键 {ResourceKey}）的外部回调队列溢出：{overflowReason}"
                + "V1 策略为 FaultSource，已停止接收并拒绝后续领取；请排除消费端阻塞后显式恢复。");
            return;
        }

        _frameSignal.Release();
    }

    private void EnsureClaimable()
    {
        lock (_sync)
        {
            switch (_state)
            {
                case EVisionResourceState.Disposed:
                    throw new ObjectDisposedException(nameof(VisionResourceSession));
                case EVisionResourceState.Stopping:
                    throw new VisionDeviceOfflineException(
                        $"资源键 {ResourceKey} 正在停止，不再接受新的领取。");
                case EVisionResourceState.Faulted:
                    throw new VisionDeviceOfflineException(
                        $"资源键 {ResourceKey} 已标记为故障（{_faultKind}）：{_faultMessage}；请先排除故障再领取。");
                case EVisionResourceState.Armed:
                    break;
                default:
                    throw new VisionSourceConfigurationException(
                        $"资源键 {ResourceKey} 的外部回调源尚未布防；请先由根运行取得所有权，再领取帧。");
            }

            if (_streamCompleted)
            {
                throw new VisionDeviceOfflineException(
                    $"资源键 {ResourceKey} 的接收流已结束"
                    + (_streamFailure is null ? "（正常停止）。" : $"（{_streamFailure}）。")
                    + "不会再有新帧到达。");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_state == EVisionResourceState.Disposed)
            throw new ObjectDisposedException(nameof(VisionResourceSession));
    }

    private void WakeWaiters()
    {
        try
        {
            _frameSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 信号已经饱和，说明已有等待者会被唤醒；无需再补。
        }
        catch (ObjectDisposedException)
        {
            // 信号量从不释放，这里只是防御性处理。
        }
    }

    private sealed class ResourceFrameSink : IVisionProviderFrameSink
    {
        private readonly VisionResourceSession _session;

        public ResourceFrameSink(VisionResourceSession session) => _session = session;

        public void Publish(VisionProviderFrame frame) => _session.Publish(frame);

        public void Complete(Exception? failure) => _session.Complete(failure);
    }
}
