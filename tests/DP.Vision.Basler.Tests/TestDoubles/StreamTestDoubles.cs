using System;
using System.Collections.Generic;
using System.Threading;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler.Tests;

/// <summary>
/// 可控的假长连接设备：记录设备侧动作，并由测试逐帧推动回调，不使用计时器。
/// <para>
/// 它模拟厂商 SDK 的两个关键行为：回调由 SDK 线程直接调用、以及"停流后可能仍有一个在途回调"。
/// </para>
/// </summary>
internal sealed class FakeStreamCamera : IBaslerStreamCamera
{
    private readonly object _sync = new object();
    private readonly List<string> _events = new List<string>();

    private Action<IBaslerGrabFrame>? _onFrame;
    private Action<Exception>? _onFailure;
    private bool _open;

    /// <summary>打开次数。</summary>
    public int OpenCount { get; private set; }

    /// <summary>关闭次数。</summary>
    public int CloseCount { get; private set; }

    /// <summary>释放次数。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>开始持续取流次数。</summary>
    public int StartGrabCount { get; private set; }

    /// <summary>停止持续取流次数。</summary>
    public int StopGrabCount { get; private set; }

    /// <summary>最近一次写入设备的触发模式。</summary>
    public EVisionTriggerMode? AppliedTriggerMode { get; private set; }

    /// <summary>最近一次写入设备的曝光。</summary>
    public double? AppliedExposure { get; private set; }

    /// <summary>最近一次写入设备的增益。</summary>
    public double? AppliedGain { get; private set; }

    /// <summary>注入 <c>StartContinuousGrab</c> 的失败；用于验证取流没起来时不留下半布防状态。</summary>
    public Exception? StartGrabFailure { get; set; }

    /// <summary>注入 <c>ApplyArmParameters</c> 的失败。</summary>
    public Exception? ApplyFailure { get; set; }

    /// <summary>
    /// 为真时，停流后仍允许回调进入——模拟"SDK 在 Stop 过程中交付了一个在途帧"的真实情形。
    /// 会话必须自己挡住它，而不是指望设备守约。
    /// </summary>
    public bool DeliverAfterStop { get; set; }

    /// <summary>是否已打开。</summary>
    public bool IsOpen
    {
        get { lock (_sync) return _open; }
    }

    /// <summary>是否处于持续取流状态。</summary>
    public bool Grabbing { get; private set; }

    /// <summary>按发生顺序记录的生命周期事件（<c>open</c>/<c>start-grab</c>/<c>stop-grab</c>/<c>close</c>）。</summary>
    public IReadOnlyList<string> Events
    {
        get { lock (_sync) return _events.ToArray(); }
    }

    /// <inheritdoc/>
    public void Open()
    {
        lock (_sync)
        {
            if (_open)
                return;
            _open = true;
            OpenCount++;
            _events.Add("open");
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        lock (_sync)
        {
            _events.Add("close");
            CloseCount++;
            _open = false;
            Grabbing = false;
            _onFrame = null;
            _onFailure = null;
        }
    }

    /// <inheritdoc/>
    public void ApplyArmParameters(EVisionTriggerMode triggerMode, double? exposureMicroseconds, double? gainDecibels)
    {
        if (ApplyFailure is not null)
            throw ApplyFailure;

        lock (_sync)
        {
            AppliedTriggerMode = triggerMode;
            AppliedExposure = exposureMicroseconds;
            AppliedGain = gainDecibels;
            _events.Add("apply-parameters");
        }
    }

    /// <inheritdoc/>
    public void StartContinuousGrab(Action<IBaslerGrabFrame> onFrame, Action<Exception> onFailure)
    {
        if (StartGrabFailure is not null)
            throw StartGrabFailure;

        lock (_sync)
        {
            _onFrame = onFrame;
            _onFailure = onFailure;
            Grabbing = true;
            StartGrabCount++;
            _events.Add("start-grab");
        }
    }

    /// <inheritdoc/>
    public void StopContinuousGrab()
    {
        lock (_sync)
        {
            StopGrabCount++;
            Grabbing = false;
            _events.Add("stop-grab");
            if (!DeliverAfterStop)
            {
                _onFrame = null;
                _onFailure = null;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DisposeCount++;
        Close();
    }

    /// <summary>模拟 SDK 交付一帧。</summary>
    /// <param name="frame">设备帧；所有权随调用转移。</param>
    /// <returns>帧是否被交给回调；没有回调时返回 <see langword="false"/> 并释放该帧。</returns>
    public bool Emit(IBaslerGrabFrame frame)
    {
        if (frame is null)
            throw new ArgumentNullException(nameof(frame));

        Action<IBaslerGrabFrame>? onFrame;
        lock (_sync)
            onFrame = _onFrame;

        if (onFrame is null)
        {
            frame.Dispose();
            return false;
        }

        onFrame(frame);
        return true;
    }

    /// <summary>模拟 SDK 报告取流失败（例如断线）。</summary>
    /// <param name="failure">失败原因。</param>
    /// <returns>是否真的上报；没有回调时返回 <see langword="false"/>。</returns>
    public bool ReportFailure(Exception failure)
    {
        Action<Exception>? onFailure;
        lock (_sync)
            onFailure = _onFailure;

        if (onFailure is null)
            return false;

        onFailure(failure);
        return true;
    }
}

/// <summary>可控的设备帧：像素由测试给出，转换行为可观测，释放次数可断言。</summary>
internal sealed class FakeGrabFrame : IBaslerGrabFrame
{
    private readonly byte[] _pixels;
    private readonly long? _conversionSizeOverride;

    /// <summary>创建设备帧。</summary>
    /// <param name="pixelFormatName">设备报告的像素格式名。</param>
    /// <param name="width">帧宽。</param>
    /// <param name="height">帧高。</param>
    /// <param name="pixels">设备侧像素字节；长度应与中立布局一致。</param>
    /// <param name="imageNumber">设备帧序号。</param>
    /// <param name="conversionSizeOverride">覆盖"转换需要多少字节"，用于验证尺寸校验。</param>
    /// <param name="capturedAtUtc">采集时刻。</param>
    public FakeGrabFrame(
        string pixelFormatName,
        int width,
        int height,
        byte[] pixels,
        long imageNumber = 1,
        long? conversionSizeOverride = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        PixelFormatName = pixelFormatName;
        Width = width;
        Height = height;
        _pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        ImageNumber = imageNumber;
        _conversionSizeOverride = conversionSizeOverride;
        CapturedAtUtc = capturedAtUtc ?? DateTimeOffset.UtcNow;
    }

    /// <inheritdoc/>
    public string PixelFormatName { get; }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public long ImageNumber { get; }

    /// <inheritdoc/>
    public DateTimeOffset CapturedAtUtc { get; }

    /// <summary>释放次数；用来证明"每个帧恰好释放一次"。</summary>
    public int DisposeCount { get; private set; }

    /// <summary>是否发生过像素转换。</summary>
    public bool Converted { get; private set; }

    /// <summary>转换时使用的目标格式名。</summary>
    public string? TargetPixelFormat { get; private set; }

    /// <inheritdoc/>
    public long GetConversionBufferSize(string targetPixelFormat) =>
        _conversionSizeOverride ?? _pixels.Length;

    /// <inheritdoc/>
    public void ConvertInto(byte[] destination, string targetPixelFormat)
    {
        Array.Copy(_pixels, destination, Math.Min(_pixels.Length, destination.Length));
        Converted = true;
        TargetPixelFormat = targetPixelFormat;
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeCount++;
}

/// <summary>记录型接收方：可注入阻塞与拒绝，用于观察回调边界上的行为。</summary>
internal sealed class RecordingStreamSink : IVisionProviderFrameSink
{
    private readonly object _sync = new object();
    private readonly List<VisionProviderFrame> _frames = new List<VisionProviderFrame>();
    private readonly List<string?> _completions = new List<string?>();

    /// <summary>注入 <c>Publish</c> 的拒绝；帧会被本接收方释放（守约的拒绝方式）。</summary>
    public Exception? PublishFailure { get; set; }

    /// <summary>在 <c>Publish</c> 内部进入时置位，用于确定性地观察"回调已在途"。</summary>
    public ManualResetEventSlim? Entered { get; set; }

    /// <summary>在 <c>Publish</c> 内部等待它，用于把回调卡在临界区内。</summary>
    public ManualResetEventSlim? Release { get; set; }

    /// <summary>收到的帧（按交付顺序）。</summary>
    public IReadOnlyList<VisionProviderFrame> Frames
    {
        get { lock (_sync) return _frames.ToArray(); }
    }

    /// <summary>收到的结束原因（按发生顺序）。</summary>
    public IReadOnlyList<string?> Completions
    {
        get { lock (_sync) return _completions.ToArray(); }
    }

    /// <inheritdoc/>
    public void Publish(VisionProviderFrame frame)
    {
        if (frame is null)
            throw new ArgumentNullException(nameof(frame));

        if (Entered is not null)
            Entered.Set();
        if (Release is not null)
            Release.Wait(TimeSpan.FromSeconds(10));

        if (PublishFailure is not null)
        {
            // 公共契约要求接收方即使拒绝也必须释放帧；这里模拟一个守约的拒绝。
            frame.Dispose();
            throw PublishFailure;
        }

        lock (_sync)
            _frames.Add(frame);
    }

    /// <inheritdoc/>
    public void Complete(Exception? failure)
    {
        lock (_sync)
            _completions.Add(failure?.Message);
    }

    /// <summary>释放所有已收到的帧。</summary>
    public void DisposeFrames()
    {
        VisionProviderFrame[] frames;
        lock (_sync)
        {
            frames = _frames.ToArray();
            _frames.Clear();
        }

        foreach (var frame in frames)
            frame.Dispose();
    }
}
