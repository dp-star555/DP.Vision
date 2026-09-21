using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// 一次 HALCON 外部回调接收会话：用自建的采集线程把设备帧转成中立帧并交给接收方。
///
/// 三条纪律对应公共流式契约：
/// <list type="number">
/// <item>所有权：帧一旦进入 <see cref="IVisionProviderFrameSink.Publish"/> 就不再由本会话释放；
/// 像素落地失败时帧从未离开本会话，由本会话释放。</item>
/// <item>线程：采集线程上的任何异常都不得逃逸，这里全部吞掉并计入诊断。</item>
/// <item>停止：<see cref="DisposeAsync"/> 先关交付口，再请求停止并尽力中止阻塞中的抓取，
/// 然后等待采集线程退出，之后不再交付任何帧。</item>
/// </list>
/// <para>
/// 与 Basler 侧的**结构差异**：pylon 由 SDK 在回调线程上推送帧，本类只需守纪律；
/// HALCON 没有事件回调，采集循环与线程都由本类建立，因此"线程所有权、停止等待、
/// 停流后不得再交付"这三件事在这里是**自建**的，不依赖 SDK 的任何保证。
/// </para>
/// </summary>
internal sealed class HalconStreamSession : IVisionAcquisitionStream
{
    private readonly IHalconStreamCamera _camera;
    private readonly IVisionProviderFrameSink _sink;
    private readonly object _sync = new object();
    private readonly TaskCompletionSource<bool> _loopExited =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _loop;
    private volatile bool _stopRequested;
    private bool _stopping;
    private bool _completed;
    private int _disposed;
    private string? _failure;

    private long _delivered;
    private long _grabTimeouts;
    private long _rejectedAfterStop;
    private long _conversionFaults;
    private long _sinkFaults;

    /// <summary>创建接收会话。</summary>
    /// <param name="camera">设备侧动作；生命周期由调用方（设备适配器）拥有。</param>
    /// <param name="sink">帧接收方。</param>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    public HalconStreamSession(IHalconStreamCamera camera, IVisionProviderFrameSink sink)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>已交付给接收方的帧数。</summary>
    public long DeliveredCount => Interlocked.Read(ref _delivered);

    /// <summary>抓取超时次数；外部触发下"这一轮没有等到帧"是正常现象，不计为故障。</summary>
    public long GrabTimeoutCount => Interlocked.Read(ref _grabTimeouts);

    /// <summary>停流开始后到达、被拒绝的帧数（真实相机在停止过程中仍可能交付在途帧）。</summary>
    public long RejectedAfterStopCount => Interlocked.Read(ref _rejectedAfterStop);

    /// <summary>像素落地失败次数；不为 0 时本次接收已被结束。</summary>
    public long ConversionFaultCount => Interlocked.Read(ref _conversionFaults);

    /// <summary>接收方违约次数（在交付中抛异常，或 <c>Complete</c> 抛异常）。</summary>
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
    /// 布防：打开设备、写入布防参数、启动自建采集线程。
    /// <para>
    /// 打开失败会原样抛出；参数或线程启动失败不会关闭设备——设备在两次布防之间保持打开以便复用，
    /// 因此下一次布防可以重试，而不是被迫重新走一遍打开流程。
    /// </para>
    /// </summary>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示保持设备当前设置。缓冲源不接受节点级覆盖，通常传空。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示保持设备当前设置。</param>
    /// <param name="grabTimeoutMilliseconds">单次抓取等待上限；它同时是停止等待的上界。</param>
    public void Arm(
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels,
        int grabTimeoutMilliseconds)
    {
        try
        {
            _camera.Open();
            _camera.ApplyArmParameters(
                triggerMode,
                exposureMicroseconds,
                gainDecibels,
                grabTimeoutMilliseconds);

            var loop = new Thread(RunGrabLoop)
            {
                IsBackground = true,
                Name = "DP.Vision.Halcon.GrabLoop"
            };
            _loop = loop;
            loop.Start();
        }
        catch
        {
            // 布防没起来：立刻关掉交付口，并让停止路径知道没有采集线程要等，
            // 否则 DisposeAsync 会永远等一个从未启动的线程。
            lock (_sync)
                _stopping = true;
            _loopExited.TrySetResult(true);
            throw;
        }
    }

    /// <summary>
    /// 停止接收并等待采集线程退出。
    /// <para>
    /// 顺序不可交换：先关交付口（此后 <c>Publish</c> 一律被拒绝），再请求循环退出并尽力中止
    /// 阻塞中的抓取，最后等线程退出。停止等待的上界是抓取超时——这正是布防时那个超时的第二个用途。
    /// </para>
    /// <para>
    /// <b>交付运行在采集线程上</b>，因此"等采集线程退出"同时就是"等已经进入 <c>Publish</c> 的交付退出"：
    /// 线程的 <c>finally</c> 在最后一次交付返回之后才置位，本方法返回时在途交付必然归零。
    /// 这是本实现**刻意依赖**的结构事实，不是巧合——如果将来把交付挪到别的线程
    /// （例如改用 HALCON 的 <c>set_framegrabber_callback</c>），必须在这里补一个在途交付计数与等待，
    /// 否则"Dispose 返回后不再有任何交付"会静默失效。
    /// </para>
    /// <para>
    /// 这里刻意**没有**额外的"等在途计数归零"循环：在交付与采集同线程的结构下它永远不会生效，
    /// 只会让读者以为存在一道并不存在的额外保护。变异验证专门确认过这一点。
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_sync)
            _stopping = true;

        _stopRequested = true;

        try
        {
            _camera.AbortGrab();
        }
        catch (Exception)
        {
            // 中止抓取是尽力而为：不支持 do_abort_grab 的接口在这里失败，停止退化为等满抓取超时。
        }

        if (_loop is not null)
            await _loopExited.Task.ConfigureAwait(false);
    }

    /// <summary>采集循环；运行在自建线程上，绝不把异常抛出去。</summary>
    private void RunGrabLoop()
    {
        try
        {
            while (true)
            {
                IHalconGrabFrame frame;
                try
                {
                    frame = _camera.GrabOnce();
                }
                catch (HalconGrabTimeoutException)
                {
                    // 外部触发下"这一轮没有等到帧"是正常现象：记一次诊断后继续等，不结束接收。
                    Interlocked.Increment(ref _grabTimeouts);
                    if (_stopRequested)
                        return;
                    continue;
                }
                catch (Exception exception)
                {
                    // 设备故障与许可证故障都在这里：结束接收，让等待中的领取立刻看到原因，
                    // 而不是等到超时后收到一句"没有帧到达"。
                    CompleteInternal(exception);
                    return;
                }

                // 停止后到达的帧同样走交付路径：由它负责拒绝并释放，
                // 避免"停流后仍交付"和"被拒绝的帧漏释放"这两类问题各写一份判断。
                Deliver(frame);

                if (_stopRequested)
                    return;
            }
        }
        catch (Exception exception)
        {
            // 循环自身出错：本线程没有可上报的调用方，只能转成接收结束。
            CompleteInternal(exception);
        }
        finally
        {
            _loopExited.TrySetResult(true);
        }
    }

    /// <summary>交付一帧；运行在采集线程上，绝不抛出。</summary>
    /// <param name="frame">设备帧；所有权随本调用进入，本方法负责释放。</param>
    private void Deliver(IHalconGrabFrame frame)
    {
        if (frame is null)
            return;

        IVisionProviderFrameSink? sink;
        lock (_sync)
        {
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
            try
            {
                image = HalconNeutralFrames.Copy(frame);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _conversionFaults);
                CompleteInternal(exception);
                return;
            }

            // HALCON 的通用采集层不提供设备帧序号，DeviceSequence 只能上报空值。
            // 所有权在进入 Publish 时转移，因此这里不放在 using 里。
            var providerFrame = new VisionProviderFrame(image, frame.CapturedAtUtc, deviceSequence: null);
            try
            {
                sink.Publish(providerFrame);
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
            SafeDisposeFrame(frame);
        }
    }

    /// <summary>结束本次接收；只生效一次，且不把异常抛给采集线程。</summary>
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

    private static void SafeDisposeFrame(IHalconGrabFrame frame)
    {
        try
        {
            frame.Dispose();
        }
        catch (Exception)
        {
            // 设备帧释放失败无可挽回，只保证异常不回到采集线程。
        }
    }
}
