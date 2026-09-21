#if BASLER_SDK
using System;
using System.Threading;
using Basler.Pylon;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// pylon 抓图结果的中立视图。
/// <para>
/// 缓冲所有权有两条来源，必须区分开，否则要么漏释放（连续取流之外的路径）要么重复释放：
/// <list type="bullet">
/// <item>连续取流的回调帧由 pylon 在事件返回后自行释放，用 <see cref="ForCallback"/> 包装。</item>
/// <item>主动采集 <c>RetrieveResult</c> 得到的抓图结果由调用方释放，用 <see cref="ForRetrieved"/> 包装。</item>
/// </list>
/// 无论哪条路径，本类型只保证"使用期间可读"；需要跨边界保留时必须先复制像素——
/// 这正是 <see cref="BaslerNeutralFrames.Copy"/> 做的事。
/// </para>
/// </summary>
internal sealed class PylonGrabFrame : IBaslerGrabFrame
{
    private readonly IGrabResult _grabResult;
    private readonly bool _ownsGrabResult;

    private PylonGrabFrame(IGrabResult grabResult, bool ownsGrabResult)
    {
        _grabResult = grabResult ?? throw new ArgumentNullException(nameof(grabResult));
        _ownsGrabResult = ownsGrabResult;
    }

    /// <summary>包装连续取流回调里的抓图结果；pylon 在事件返回后释放它。</summary>
    /// <param name="grabResult">本次回调的抓图结果。</param>
    /// <returns>不拥有缓冲的视图。</returns>
    public static PylonGrabFrame ForCallback(IGrabResult grabResult) => new PylonGrabFrame(grabResult, ownsGrabResult: false);

    /// <summary>包装主动采集得到的抓图结果；本视图负责释放它。</summary>
    /// <param name="grabResult">已成功抓取、由调用方拥有的抓图结果。</param>
    /// <returns>拥有缓冲的视图。</returns>
    public static PylonGrabFrame ForRetrieved(IGrabResult grabResult) => new PylonGrabFrame(grabResult, ownsGrabResult: true);

    /// <inheritdoc/>
    public string PixelFormatName => _grabResult.PixelTypeValue.ToString();

    /// <inheritdoc/>
    public int Width => _grabResult.Width;

    /// <inheritdoc/>
    public int Height => _grabResult.Height;

    /// <inheritdoc/>
    /// <remarks>pylon 的 ImageNumber 从 1 开始，并在每次 <c>IStreamGrabber.Start</c> 时复位。</remarks>
    public long ImageNumber => _grabResult.ImageNumber;

    /// <inheritdoc/>
    /// <remarks>
    /// 取回调边界上的观测时刻。设备时间戳（<c>IGrabResult.Timestamp</c>）是相机私有刻度，
    /// 需要配合 GevTimestampTickFrequency 换算且部分相机不支持，本版不引入该换算。
    /// </remarks>
    public DateTimeOffset CapturedAtUtc => DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public long GetConversionBufferSize(string targetPixelFormat)
    {
        var converter = new PixelDataConverter { OutputPixelFormat = ParsePixelType(targetPixelFormat) };
        return converter.GetBufferSizeForConversion(_grabResult.PixelTypeValue, Width, Height);
    }

    /// <inheritdoc/>
    public void ConvertInto(byte[] destination, string targetPixelFormat)
    {
        if (destination is null)
            throw new ArgumentNullException(nameof(destination));

        var converter = new PixelDataConverter { OutputPixelFormat = ParsePixelType(targetPixelFormat) };
        converter.Convert(destination, _grabResult);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsGrabResult)
            _grabResult.Dispose();
    }

    private static PixelType ParsePixelType(string name)
    {
        if (Enum.TryParse<PixelType>(name, ignoreCase: false, out var value) && Enum.IsDefined(typeof(PixelType), value))
            return value;
        throw new VisionDataException($"Basler 像素格式 {name} 不是本Provider所依赖 pylon 版本的已知格式。");
    }
}

/// <summary>
/// 真实 pylon 相机：持有唯一的 <see cref="Camera"/> 对象，同时驱动主动单次采集与持续取流。
/// <para>
/// 相机对象在设备被释放之前保持打开（单次采集之间、两次布防之间都不关闭），
/// 因此真实设备不会因为重复 Open 而失败，两种采集模式也共用同一条 <c>StreamGrabber</c>。
/// </para>
/// </summary>
internal sealed class PylonStreamCamera : IBaslerStreamCamera
{
    private readonly BaslerAcquisitionBinding _binding;
    private readonly object _sync = new object();

    private Camera? _camera;
    private Action<IBaslerGrabFrame>? _onFrame;
    private Action<Exception>? _onFailure;

    /// <summary>创建长连接设备。</summary>
    /// <param name="binding">Provider 私有绑定。</param>
    /// <exception cref="ArgumentNullException">绑定为空。</exception>
    public PylonStreamCamera(BaslerAcquisitionBinding binding) =>
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));

    /// <inheritdoc/>
    public bool IsOpen
    {
        get
        {
            lock (_sync)
                return _camera?.IsOpen == true;
        }
    }

    /// <inheritdoc/>
    public void Open()
    {
        lock (_sync)
        {
            if (_camera is not null)
                return;

            var cameraInfo = BaslerCameraSelection.Resolve(_binding);
            var camera = new Camera(cameraInfo, CameraSelectionStrategy.Unambiguous);
            try
            {
                camera.Open();
            }
            catch (Exception exception) when (exception is not VisionAcquisitionException)
            {
                camera.Dispose();
                throw new VisionDeviceOfflineException(
                    $"Basler 设备 {_binding.BindingId}（{_binding.SelectorKey}={_binding.SelectorValue}）打开失败："
                    + exception.Message,
                    exception);
            }

            _camera = camera;
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        Camera? camera;
        lock (_sync)
        {
            camera = _camera;
            _camera = null;
        }

        if (camera is null)
            return;

        if (camera.IsOpen)
            camera.Close();
        camera.Dispose();
    }

    /// <inheritdoc/>
    public void ApplyArmParameters(EVisionTriggerMode triggerMode, double? exposureMicroseconds, double? gainDecibels)
    {
        Camera camera;
        lock (_sync)
            camera = _camera ?? throw new InvalidOperationException("Basler 设备尚未打开，无法写入布防参数。");

        BaslerCameraParameters.Apply(camera, _binding, triggerMode, exposureMicroseconds, gainDecibels);
    }

    /// <inheritdoc/>
    public void StartContinuousGrab(Action<IBaslerGrabFrame> onFrame, Action<Exception> onFailure)
    {
        if (onFrame is null)
            throw new ArgumentNullException(nameof(onFrame));

        Camera camera;
        lock (_sync)
        {
            camera = _camera ?? throw new InvalidOperationException("Basler 设备尚未打开，无法开始持续取流。");
            if (camera.StreamGrabber.IsGrabbing)
            {
                throw new InvalidOperationException(
                    "Basler 设备上已经有取流在运行（持续取流或一次尚未结束的单次采集）；"
                    + "同一物理设备只有一条取流通道，不能同时开始第二条。");
            }

            _onFrame = onFrame;
            _onFailure = onFailure;
            camera.StreamGrabber.ImageGrabbed += OnImageGrabbed;
        }

        try
        {
            camera.StreamGrabber.Start(GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);
        }
        catch
        {
            // 取流没起来：摘掉刚挂上的钩子，避免把"没开始过"的接收方留在事件上。
            lock (_sync)
            {
                camera.StreamGrabber.ImageGrabbed -= OnImageGrabbed;
                _onFrame = null;
                _onFailure = null;
            }

            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 先摘钩子再 Stop：摘钩之后不会再有新的回调进入，Stop 负责让 SDK 停止产帧。
    /// 只有本对象真正开始过持续取流才停流——设备可能正忙于一次单次采集，
    /// 此时停流会把那条取流通道从别人手里抢走。
    /// </remarks>
    public void StopContinuousGrab()
    {
        Camera? camera;
        bool grabbing;
        lock (_sync)
        {
            camera = _camera;
            grabbing = _onFrame is not null;
            _onFrame = null;
            _onFailure = null;
        }

        if (camera is null || !grabbing)
            return;

        camera.StreamGrabber.ImageGrabbed -= OnImageGrabbed;
        if (camera.StreamGrabber.IsGrabbing)
            camera.StreamGrabber.Stop();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 单次采集与持续取流共用同一个 <c>StreamGrabber</c>：开始前先确认设备没有在持续取流，
    /// 结束后一定停掉本次抓图，避免把残留的取流状态留给下一次布防。
    /// </remarks>
    public IBaslerGrabFrame CaptureSingleFrame(
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        Camera camera;
        lock (_sync)
        {
            camera = _camera ?? throw new InvalidOperationException("Basler 设备尚未打开，无法按请求单次采集。");
            if (camera.StreamGrabber.IsGrabbing)
            {
                throw new InvalidOperationException(
                    "Basler 设备正在持续取流；同一条取流通道不能同时用于单次采集。");
            }
        }

        BaslerCameraParameters.Apply(camera, _binding, triggerMode, exposureMicroseconds, gainDecibels);

        camera.StreamGrabber.Start(GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);
        try
        {
            if (triggerMode == EVisionTriggerMode.Software)
            {
                // 软件触发：先发一次触发，再等待这一帧。
                camera.Parameters[PLCamera.TriggerSoftware].Execute();
            }

            cancellationToken.ThrowIfCancellationRequested();
            IGrabResult result;
            try
            {
                result = camera.StreamGrabber.RetrieveResult(timeoutMilliseconds, TimeoutHandling.ThrowException);
            }
            catch (TimeoutException exception)
            {
                // VisionCaptureTimeoutException 只接受诊断文本；把底层消息并入，避免丢失原因。
                throw new VisionCaptureTimeoutException(
                    $"Basler 采集超时（{timeoutMilliseconds} ms）；外部触发未到达或设备未出图。"
                    + $"请检查触发接线、触发源配置和曝光时间。底层原因：{exception.Message}");
            }

            if (!result.GrabSucceeded)
            {
                var code = result.ErrorCode;
                var description = result.ErrorDescription;
                result.Dispose();
                throw new VisionDataException($"Basler 采集失败（错误码 {code}）：{description}");
            }

            // 抓图结果由调用方拥有：像素复制必须在它被释放之前完成。
            return PylonGrabFrame.ForRetrieved(result);
        }
        finally
        {
            try
            {
                if (camera.StreamGrabber.IsGrabbing)
                    camera.StreamGrabber.Stop();
            }
            catch (Exception)
            {
                // 停流失败不改变本次采集的结果：设备对象仍在，下一次采集会重新 Start。
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            StopContinuousGrab();
        }
        catch (Exception)
        {
            // 停流失败不阻碍关设备：继续走关闭流程，避免设备句柄泄漏。
        }

        Close();
    }

    /// <summary>
    /// 抓图回调。
    /// <para>
    /// pylon 明确规定：本回调抛出的异常会向外传播，并且<b>事件通知在抛异常后停止</b>。
    /// 因此这里绝不把异常放出去——帧被拒绝、像素格式不支持、上报失败都只记入诊断。
    /// </para>
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">抓图事件参数；抓图结果在事件返回后由 pylon 释放。</param>
    private void OnImageGrabbed(object? sender, ImageGrabbedEventArgs e)
    {
        Action<IBaslerGrabFrame>? onFrame;
        Action<Exception>? onFailure;
        lock (_sync)
        {
            onFrame = _onFrame;
            onFailure = _onFailure;
        }

        var grabResult = e.GrabResult;
        if (grabResult is null)
            return;

        if (!grabResult.GrabSucceeded)
        {
            // 取流失败（例如断线、丢帧）：上报一次，让宿主把该源标为故障而不是一直等到超时。
            var code = grabResult.ErrorCode;
            var description = grabResult.ErrorDescription;
            ReportFailure(
                onFailure,
                new VisionDataException($"Basler 持续取流失败（错误码 {code}）：{description}"));
            return;
        }

        if (onFrame is null)
            return;

        try
        {
            // 抓图结果的生命周期由 pylon 拥有，因此用回调视图传递，不做释放。
            onFrame(PylonGrabFrame.ForCallback(grabResult));
        }
        catch (Exception exception)
        {
            // 回调实现违约（公共契约要求它不得抛出）：兜底上报，绝不把异常带回 SDK 回调线程。
            ReportFailure(onFailure, exception);
        }
    }

    private void ReportFailure(Action<Exception>? onFailure, Exception failure)
    {
        Action<Exception>? target;
        lock (_sync)
            target = onFailure;

        if (target is null)
            return;

        try
        {
            // 不做"只上报一次"的守卫：结束通知的唯一性由接收会话保证（重复调用是空操作），
            // 放在这里只会多出一条没人验证的分支。
            target(failure);
        }
        catch (Exception)
        {
            // 上报方违约；同样不得回到 SDK 回调线程。
        }
    }
}
#endif
