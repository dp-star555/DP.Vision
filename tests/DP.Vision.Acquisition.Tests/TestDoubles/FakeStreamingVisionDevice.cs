using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 可控的假流式设备：既能按请求采集一帧，也能布防后由测试逐帧推动回调。
/// <para>
/// 帧的推送完全由测试触发（<see cref="Emit"/>），不使用计时器，因此结果确定可复现。
/// </para>
/// <para>
/// 本假设备模拟厂商SDK的回调语义：<see cref="IVisionProviderFrameSink.Publish"/> 串行执行，
/// 且异常不得抛回调用线程——SDK回调线程上抛异常通常直接崩进程。
/// </para>
/// </summary>
internal sealed class FakeStreamingVisionDevice : IVisionAcquisitionDevice, IVisionStreamingAcquisitionDevice
{
    private readonly object _callbackGate = new object();
    private readonly List<string> _sinkFailures = new List<string>();
    private readonly List<string> _events = new List<string>();
    private readonly Func<byte, IImageSource> _imageFactory;
    private IVisionProviderFrameSink? _sink;
    private bool _streamCompleted;
    private int _captureCount;
    private int _streamStartCount;
    private int _streamDisposeCount;
    private int _disposeCount;

    /// <summary>创建设备。</summary>
    /// <param name="identity">设备报告身份。</param>
    /// <param name="imageFactory">按像素种子创建中立图像；用于让测试观察真实资源释放。</param>
    public FakeStreamingVisionDevice(
        VisionDeviceIdentity identity,
        Func<byte, IImageSource>? imageFactory = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _imageFactory = imageFactory ?? (seed => TestImages.Gray8(seed: seed));
    }

    /// <inheritdoc/>
    public VisionDeviceIdentity Identity { get; }

    /// <summary>主动采集次数。</summary>
    public int CaptureCount => Volatile.Read(ref _captureCount);

    /// <summary>布防次数。</summary>
    public int StreamStartCount => Volatile.Read(ref _streamStartCount);

    /// <summary>
    /// 接收流被**显式**释放的次数。
    /// <para>
    /// 只统计 <c>IVisionAcquisitionStream.DisposeAsync()</c>。设备自身被释放时也会结束接收，
    /// 但那是另一条路径，计入 <see cref="DisposeCount"/> 与 <see cref="Events"/>，不进这个计数——
    /// 否则"运行时是否主动停流"就无法与"设备被关掉顺带停流"区分开。
    /// </para>
    /// </summary>
    public int StreamDisposeCount => Volatile.Read(ref _streamDisposeCount);

    /// <summary>设备自身被释放的次数。</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>
    /// 按发生顺序记录的生命周期事件（<c>stream-start</c> / <c>stream-stop</c> / <c>device-dispose</c>）。
    /// <para>顺序本身是契约的一部分：必须先停流（等在途回调退出）再释放设备。</para>
    /// </summary>
    public IReadOnlyList<string> Events
    {
        get { lock (_callbackGate) return _events.ToArray(); }
    }

    /// <summary>是否处于布防状态。</summary>
    public bool IsStreaming
    {
        get { lock (_callbackGate) return _sink is not null; }
    }

    /// <summary>被接收方抛出的异常信息；异常本身被设备吞掉，不返回SDK回调线程。</summary>
    public IReadOnlyList<string> SinkFailures
    {
        get { lock (_callbackGate) return _sinkFailures.ToArray(); }
    }

    /// <inheritdoc/>
    public ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _captureCount);
        return new ValueTask<VisionProviderFrame>(
            new VisionProviderFrame(_imageFactory(200), DateTimeOffset.UtcNow, null));
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">接收方为空。</exception>
    /// <exception cref="InvalidOperationException">设备已经处于布防状态。</exception>
    public ValueTask<IVisionAcquisitionStream> StartStreamAsync(
        IVisionProviderFrameSink sink,
        CancellationToken cancellationToken)
    {
        if (sink is null)
            throw new ArgumentNullException(nameof(sink));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_callbackGate)
        {
            // 一台设备同时只能有一条接收流；重复布防明确拒绝，不静默替换接收方。
            if (_sink is not null)
                throw new InvalidOperationException("该设备已经在布防状态；一台设备同时只允许一条接收流。");
            _sink = sink;
            _streamCompleted = false;
            _events.Add("stream-start");
            Interlocked.Increment(ref _streamStartCount);
        }

        return new ValueTask<IVisionAcquisitionStream>(new Stream(this));
    }

    /// <summary>模拟一次外部触发回调。</summary>
    /// <param name="deviceSequence">设备报告的帧序号；不支持时为空。</param>
    /// <param name="seed">像素种子，用于区分不同帧。</param>
    /// <param name="observation">Provider 观测到的像素落地耗时与字节；为空表示未观测。</param>
    /// <returns>帧是否被交付给接收方；未布防或已结束时返回 <see langword="false"/>。</returns>
    public bool Emit(long? deviceSequence = null, byte seed = 1, VisionPixelTransferObservation? observation = null)
    {
        IVisionProviderFrameSink? sink;
        lock (_callbackGate)
        {
            if (_sink is null || _streamCompleted)
                return false;
            sink = _sink;

            // 回调与"释放接收流"互斥：释放必须等到已经进入的回调退出。
            var frame = new VisionProviderFrame(_imageFactory(seed), DateTimeOffset.UtcNow, deviceSequence, observation);
            try
            {
                sink.Publish(frame);
            }
            catch (Exception exception)
            {
                // 接收方拒绝时帧已由接收方释放；这里只记录，不把异常抛回SDK回调线程。
                _sinkFailures.Add(exception.GetType().Name + ": " + exception.Message);
            }

            return true;
        }
    }

    /// <summary>模拟SDK报告接收意外结束。</summary>
    /// <param name="failure">结束原因。</param>
    /// <returns>是否真的完成了本次接收；未布防或已结束时返回 <see langword="false"/>。</returns>
    public bool CompleteStream(Exception? failure = null)
    {
        lock (_callbackGate)
        {
            if (_sink is null || _streamCompleted)
                return false;
            _streamCompleted = true;
            _sink.Complete(failure);
            return true;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_callbackGate)
        {
            // 真实SDK关闭设备同样会结束接收，所以这里也清空接收方；但**不计入 StreamDisposeCount**，
            // 否则"运行时是否主动停流"就会被这条兜底掩盖（曾经因此漏掉一处停流缺失）。
            _sink = null;
            _streamCompleted = true;
            _events.Add("device-dispose");
        }

        Interlocked.Increment(ref _disposeCount);
        return default;
    }

    private sealed class Stream : IVisionAcquisitionStream
    {
        private readonly FakeStreamingVisionDevice _device;
        private int _disposed;

        public Stream(FakeStreamingVisionDevice device) => _device = device;

        /// <summary>释放接收流：等待已经进入的回调退出，之后不再交付任何帧。</summary>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return default;

            lock (_device._callbackGate)
            {
                _device._sink = null;
                _device._streamCompleted = true;
                _device._events.Add("stream-stop");
                Interlocked.Increment(ref _device._streamDisposeCount);
            }

            return default;
        }
    }
}
