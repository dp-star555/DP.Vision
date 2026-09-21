using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// 一次 Basler 外部回调接收会话：把 SDK 回调线程上的设备帧转成中立帧并交给接收方。
///
/// 三条纪律对应公共流式契约：
/// <list type="number">
/// <item>所有权：帧一旦进入 <see cref="IVisionProviderFrameSink.Publish"/> 就不再由本会话释放；
/// 像素落地失败时帧从未离开本会话，由本会话释放。</item>
/// <item>线程：回调内任何异常都不得抛回 SDK 线程，这里全部吞掉并计入诊断。</item>
/// <item>停止：<see cref="DisposeAsync"/> 先停流，再等待已进入的回调退出，之后不再交付任何帧。</item>
/// </list>
/// </summary>
internal sealed class BaslerStreamSession : IVisionAcquisitionStream
{
    private readonly IBaslerStreamCamera _camera;
    private readonly IVisionProviderFrameSink _sink;
    private readonly object _sync = new object();

    private TaskCompletionSource<bool>? _idle;
    private int _inFlight;
    private bool _stopping;
    private bool _completed;
    private int _disposed;
    private string? _failure;

    private long _delivered;
    private long _rejectedAfterStop;
    private long _conversionFaults;
    private long _sinkFaults;

    /// <summary>创建接收会话。</summary>
    /// <param name="camera">设备侧动作；生命周期由调用方（设备适配器）拥有。</param>
    /// <param name="sink">帧接收方。</param>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    public BaslerStreamSession(IBaslerStreamCamera camera, IVisionProviderFrameSink sink)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>已交付给接收方的帧数。</summary>
    public long DeliveredCount => Interlocked.Read(ref _delivered);

    /// <summary>停流开始后到达、被拒绝的帧数（真实相机在停止过程中仍可能交付在途帧）。</summary>
    public long RejectedAfterStopCount => Interlocked.Read(ref _rejectedAfterStop);

    /// <summary>像素落地失败次数；不为 0 时本次接收已被结束。</summary>
    public long ConversionFaultCount => Interlocked.Read(ref _conversionFaults);

    /// <summary>接收方违约次数（在回调内抛异常，或 <c>Complete</c> 抛异常）。</summary>
    public long SinkFaultCount => Interlocked.Read(ref _sinkFaults);

    /// <summary>接收是否已经结束（正常停止或故障结束）。</summary>
    public bool Stopped
    {
        get { lock (_sync) return _stopping || _completed; }
    }

    /// <summary>结束原因；正常停止时为空。</summary>
    public string? Failure
    {
        get { lock (_sync) return _failure; }
    }

    /// <summary>
    /// 布防：打开设备、写入布防参数、开始持续取流。
    /// <para>
    /// 打开失败会原样抛出；参数或取流失败不会关闭设备——设备在两次布防之间保持打开以便复用，
    /// 因此下一次布防可以重试，而不是被迫重新走一遍打开流程。
    /// </para>
    /// </summary>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示不改写设备设置。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示不改写设备设置。</param>
    public void Arm(EVisionTriggerMode triggerMode, double? exposureMicroseconds, double? gainDecibels)
    {
        _camera.Open();
        _camera.ApplyArmParameters(triggerMode, exposureMicroseconds, gainDecibels);

        try
        {
            _camera.StartContinuousGrab(OnFrame, OnStreamFailure);
        }
        catch
        {
            // 取流没起来：立刻关掉交付口，避免调用方以为还会收到帧。
            lock (_sync)
                _stopping = true;
            throw;
        }
    }

    /// <summary>
    /// 停流并等待已经进入的回调退出。
    /// <para>
    /// 顺序不可交换：先关交付口，再停流（此后不会再有新的回调进入），最后等计数归零。
    /// 计数归零是终态，因为此后不存在能再次自增的回调。
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_sync)
            _stopping = true;

        try
        {
            _camera.StopContinuousGrab();
        }
        catch (Exception)
        {
            // 停流失败不改变"之后不再交付"这一保证：交付口已经被 _stopping 关闭。
        }

        while (true)
        {
            Task? wait;
            lock (_sync)
            {
                if (_inFlight == 0)
                    return;
                _idle ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _idle.Task;
            }

            await wait.ConfigureAwait(false);
        }
    }

    /// <summary>接收一帧；由 SDK 回调线程直接调用，绝不抛出。</summary>
    /// <param name="grab">设备帧；所有权随本调用进入，本方法负责释放。</param>
    private void OnFrame(IBaslerGrabFrame grab)
    {
        if (grab is null)
            return;

        IVisionProviderFrameSink? sink;
        lock (_sync)
        {
            _inFlight++;
            if (_stopping || _completed)
            {
                sink = null;
                Interlocked.Increment(ref _rejectedAfterStop);
            }
            else
            {
                sink = _sink;
            }
        }

        try
        {
            if (sink is null)
                return;

            IImageSource image;
            VisionPixelTransferObservation observation;
            try
            {
                (image, observation) = BaslerNeutralFrames.CopyObserved(grab);
            }
            catch (Exception exception)
            {
                // 像素落地失败不能静默丢帧：结束本次接收，让等待中的领取立刻看到原因，
                // 而不是等到超时后收到一句"没有帧到达"。
                Interlocked.Increment(ref _conversionFaults);
                CompleteInternal(exception);
                return;
            }

            // 所有权在进入 Publish 时转移，因此这里不放在 using 里。
            var frame = new VisionProviderFrame(image, grab.CapturedAtUtc, grab.ImageNumber, observation);
            try
            {
                sink.Publish(frame);
                Interlocked.Increment(ref _delivered);
            }
            catch (Exception)
            {
                // 接收方违约：公共契约要求它即使拒绝也必须释放帧，因此这里不得再次释放。
                Interlocked.Increment(ref _sinkFaults);
            }
        }
        finally
        {
            TaskCompletionSource<bool>? idle = null;
            lock (_sync)
            {
                _inFlight--;
                if (_inFlight == 0)
                {
                    idle = _idle;
                    _idle = null;
                }
            }

            idle?.TrySetResult(true);

            try
            {
                grab.Dispose();
            }
            catch (Exception)
            {
                // 设备帧释放失败无可挽回，只保证异常不回到 SDK 回调线程。
            }
        }
    }

    /// <summary>设备侧报告取流失败；结束本次接收，让等待者立刻失败而不是等到超时。</summary>
    /// <param name="failure">失败原因。</param>
    private void OnStreamFailure(Exception failure) =>
        CompleteInternal(failure ?? new VisionDeviceOfflineException("Basler 持续取流意外结束。"));

    /// <summary>结束本次接收；只生效一次，且不把异常抛给 SDK 线程。</summary>
    /// <param name="failure">结束原因。</param>
    private void CompleteInternal(Exception failure)
    {
        lock (_sync)
        {
            if (_completed || _stopping)
                return;
            _completed = true;
            _failure = failure.Message;
        }

        try
        {
            _sink.Complete(failure);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _sinkFaults);
        }
    }
}
