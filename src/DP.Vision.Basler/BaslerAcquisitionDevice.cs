using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
#if BASLER_SDK
using Basler.Pylon;
#endif

namespace DP.Vision.Basler;

/// <summary>
/// Basler pylon 采集设备适配器。
///
/// 与 HALCON Adapter 一致：每次采集独立打开/关闭设备，不承诺长连接；像素一律复制为中立图像，
/// 不把 pylon 的缓冲或指针交给调用方。设备选择要求唯一匹配，不做"取第一台"的回退。
/// </summary>
public sealed class BaslerAcquisitionDevice : IVisionAcquisitionDevice
{
    private readonly BaslerAcquisitionBinding _binding;
    private bool _disposed;

    /// <summary>创建设备Adapter。</summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <exception cref="ArgumentNullException">绑定为空。</exception>
    public BaslerAcquisitionDevice(BaslerAcquisitionBinding binding)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Identity = new VisionDeviceIdentity(
            BaslerAcquisitionProvider.ProviderIdentity,
            binding.BindingId,
            binding.CanonicalKey,
            binding.SerialNumber,
            VendorName: "Basler");
    }

    /// <inheritdoc/>
    public VisionDeviceIdentity Identity { get; }

    /// <inheritdoc/>
    public ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (_disposed)
            throw new ObjectDisposedException(nameof(BaslerAcquisitionDevice));
        cancellationToken.ThrowIfCancellationRequested();
#if BASLER_SDK
        // pylon 的打开/抓图是阻塞调用，隔离到线程池；取消在调用边界检查。
        return new ValueTask<VisionProviderFrame>(
            Task.Run(() => Capture(request, cancellationToken), cancellationToken));
#else
        throw new VisionProviderUnavailableException(
            BaslerAcquisitionProvider.ProviderIdentity,
            "本程序集未装配 Basler pylon 支持（BASLER_SDK 未定义）；请以默认配置重新构建 DP.Vision.Basler。");
#endif
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return default;
    }

#if BASLER_SDK
    private VisionProviderFrame Capture(VisionCaptureRequest request, CancellationToken cancellationToken)
    {
        var cameraInfo = ResolveCamera();
        cancellationToken.ThrowIfCancellationRequested();

        var timeoutMilliseconds = ToTimeoutMilliseconds(request.Timeout);
        using var camera = new Camera(cameraInfo, CameraSelectionStrategy.Unambiguous);
        try
        {
            camera.Open();
        }
        catch (Exception exception) when (exception is not VisionAcquisitionException)
        {
            throw new VisionDeviceOfflineException(
                $"Basler 设备 {_binding.BindingId}（{_binding.SelectorKey}={_binding.SelectorValue}）打开失败："
                + exception.Message,
                exception);
        }

        try
        {
            ApplyRequest(camera, request);
            using var grabResult = Grab(camera, request.TriggerMode, timeoutMilliseconds, cancellationToken);
            return new VisionProviderFrame(
                CopyToNeutralImage(grabResult),
                DateTimeOffset.UtcNow,
                grabResult.ImageNumber);
        }
        finally
        {
            if (camera.IsOpen)
                camera.Close();
        }
    }

    /// <summary>按绑定唯一解析目标相机；匹配不到或多于一台都明确失败，不回退到"第一台"。</summary>
    /// <returns>唯一匹配的相机信息。</returns>
    /// <exception cref="VisionDeviceOfflineException">没有匹配的设备。</exception>
    /// <exception cref="VisionSourceConfigurationException">匹配到多于一台设备，配置无法确定目标。</exception>
    private ICameraInfo ResolveCamera()
    {
        // 选择键使用 pylon 自己的常量，避免依赖字符串字面量与SDK定义恰好一致。
        var key = _binding.SerialNumber is not null ? CameraInfoKey.SerialNumber : CameraInfoKey.UserDefinedName;
        var expected = _binding.SelectorValue;

        var matches = new List<ICameraInfo>();
        foreach (var info in CameraFinder.Enumerate())
        {
            if (info.ContainsKey(key) && string.Equals(info[key], expected, StringComparison.Ordinal))
                matches.Add(info);
        }

        if (matches.Count == 1)
            return matches[0];
        if (matches.Count == 0)
        {
            throw new VisionDeviceOfflineException(
                $"Basler 设备 {_binding.BindingId} 未找到：没有相机满足 {key}={expected}。"
                + "请确认设备已上电联网，或修正Provider私有配置中的绑定。");
        }

        throw new VisionSourceConfigurationException(
            $"Basler 设备 {_binding.BindingId} 的 {key}={expected} 匹配到 {matches.Count} 台相机；"
            + "绑定必须唯一确定一台设备，请改用序列号区分。");
    }

    /// <summary>把公共请求写到设备参数上；只有调用方明确给出数值时才改写设备设置。</summary>
    /// <param name="camera">已打开的相机。</param>
    /// <param name="request">采集请求。</param>
    /// <exception cref="VisionParameterNotSupportedException">设备不接受给定数值或触发模式无法表达。</exception>
    private void ApplyRequest(Camera camera, VisionCaptureRequest request)
    {
        if (request.ExposureMicroseconds is { } exposure)
        {
            // 先关自动曝光：否则手动值会被自动算法覆盖，操作员看到的是"设置了但不生效"。
            camera.Parameters[PLCamera.ExposureAuto].TrySetValue(PLCamera.ExposureAuto.Off);
            SetFloat(camera, PLCamera.ExposureTime, exposure, "曝光");
        }

        if (request.GainDecibels is { } gain)
        {
            camera.Parameters[PLCamera.GainAuto].TrySetValue(PLCamera.GainAuto.Off);
            SetFloat(camera, PLCamera.Gain, gain, "增益");
        }

        ApplyTrigger(camera, request.TriggerMode);
    }

    private static void SetFloat(Camera camera, FloatName name, double value, string label)
    {
        if (!camera.Parameters[name].TrySetValue(value))
        {
            throw new VisionParameterNotSupportedException(
                $"Basler 设备不接受{label} {value}；该值超出设备允许范围或该参数当前不可写。");
        }
    }

    private static void SetEnum(Camera camera, EnumName name, string value, string label)
    {
        if (!camera.Parameters[name].TrySetValue(value))
        {
            throw new VisionParameterNotSupportedException(
                $"Basler 设备不接受{label} {value}；该参数当前不可写或设备不支持该取值。");
        }
    }

    /// <summary>把公共触发模式写到设备上；无法表达的模式明确拒绝，而不是静默按自由运行采集。</summary>
    /// <param name="camera">已打开的相机。</param>
    /// <param name="mode">公共触发模式。</param>
    /// <exception cref="VisionParameterNotSupportedException">外部触发未声明触发源，或模式未知。</exception>
    private void ApplyTrigger(Camera camera, EVisionTriggerMode mode)
    {
        switch (mode)
        {
            case EVisionTriggerMode.KeepCurrent:
                // 保持设备当前设置：不写任何触发参数。
                return;

            case EVisionTriggerMode.FreeRun:
                SetEnum(camera, PLCamera.TriggerSelector, PLCamera.TriggerSelector.FrameStart, "触发选择器");
                SetEnum(camera, PLCamera.TriggerMode, PLCamera.TriggerMode.Off, "触发模式");
                return;

            case EVisionTriggerMode.Software:
                SetEnum(camera, PLCamera.TriggerSelector, PLCamera.TriggerSelector.FrameStart, "触发选择器");
                SetEnum(camera, PLCamera.TriggerSource, PLCamera.TriggerSource.Software, "触发源");
                SetEnum(camera, PLCamera.TriggerMode, PLCamera.TriggerMode.On, "触发模式");
                return;

            case EVisionTriggerMode.External:
                var source = _binding.TriggerSource ?? throw new VisionParameterNotSupportedException(
                    $"Basler 绑定 {_binding.BindingId} 使用外部触发，但Provider私有配置没有声明 triggerSource"
                    + "（例如 \"triggerSource\": \"Line1\"）。为避免猜错物理接线，这里明确拒绝而不是沿用设备当前设置。");
                SetEnum(camera, PLCamera.TriggerSelector, PLCamera.TriggerSelector.FrameStart, "触发选择器");
                SetEnum(camera, PLCamera.TriggerSource, source, "触发源");
                SetEnum(camera, PLCamera.TriggerMode, PLCamera.TriggerMode.On, "触发模式");
                return;

            default:
                throw new VisionParameterNotSupportedException($"Basler Adapter 不支持触发模式 {mode}。");
        }
    }

    private static IGrabResult Grab(
        Camera camera,
        EVisionTriggerMode mode,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        camera.StreamGrabber.Start(GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);
        try
        {
            if (mode == EVisionTriggerMode.Software)
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
                var description = result.ErrorDescription;
                var code = result.ErrorCode;
                result.Dispose();
                throw new VisionDataException($"Basler 采集失败（错误码 {code}）：{description}");
            }

            return result;
        }
        finally
        {
            if (camera.StreamGrabber.IsGrabbing)
                camera.StreamGrabber.Stop();
        }
    }

    /// <summary>
    /// 把设备帧复制为中立图像。
    /// 统一走 pylon 的 <see cref="PixelDataConverter"/>：它同时处理行填充与格式转换，
    /// 目标格式由 <see cref="BaslerPixelFormats"/> 显式决定，不做隐式位深或通道语义改变。
    /// </summary>
    /// <param name="grabResult">已成功抓取、由调用方释放的设备帧。</param>
    /// <returns>所有权交给调用方的中立图像。</returns>
    /// <exception cref="VisionDataException">像素格式不受支持，或目标布局尺寸与转换结果不一致。</exception>
    private static IImageSource CopyToNeutralImage(IGrabResult grabResult)
    {
        var sourceFormat = grabResult.PixelTypeValue.ToString();
        var conversion = BaslerPixelFormats.Resolve(sourceFormat);
        var target = ParsePixelType(conversion.TargetPixelFormat);

        var width = grabResult.Width;
        var height = grabResult.Height;
        var info = new ImageInfo(width, height, conversion.Layout);

        var converter = new PixelDataConverter { OutputPixelFormat = target };
        var expected = converter.GetBufferSizeForConversion(grabResult.PixelTypeValue, width, height);
        if (expected != info.ByteLength)
        {
            throw new VisionDataException(
                $"Basler 像素转换结果尺寸与中立布局不一致：转换报告 {expected} 字节，"
                + $"布局 {conversion.Layout} 需要 {info.ByteLength} 字节。这表示格式映射表有误，已拒绝发布该帧。");
        }

        var pixels = new byte[info.ByteLength];
        converter.Convert(pixels, grabResult);
        return VisionImage.CopyFrom(info, pixels);
    }

    private static PixelType ParsePixelType(string name)
    {
        if (Enum.TryParse<PixelType>(name, ignoreCase: false, out var value) && Enum.IsDefined(typeof(PixelType), value))
            return value;
        throw new VisionDataException($"Basler 像素格式 {name} 不是本Provider所依赖 pylon 版本的已知格式。");
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(timeout.TotalMilliseconds)));
#endif
}
