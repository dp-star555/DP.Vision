#if HALCON_SDK
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Acquisition;
using HalconDotNet;

namespace DP.Vision.Halcon;

/// <summary>
/// 借用或接管一张 HALCON 图像的中立视图。
/// <para>
/// 与 pylon 的**关键差异**：HALCON 的 <c>grab_image_async</c> 把图像对象的所有权交给调用方
/// （pylon 的事件回调里抓图结果由 SDK 在事件返回后自行释放）。因此这里用两个具名入口把
/// "谁负责释放"写在调用点上，而不是靠一个布尔参数——漏释放或重复释放都会在现场表现为句柄泄漏或崩溃。
/// </para>
/// <para>
/// 元数据（尺寸、像素类型、通道数）在构造期读取一次；像素复制时重新取一次指针。
/// <c>HObject</c> 不可变、指针稳定，这样换来的是不缓存任何需要显式释放的 <c>HTuple</c>。
/// </para>
/// </summary>
internal sealed class HObjectGrabFrame : IHalconGrabFrame
{
    private readonly HObject _image;
    private readonly bool _ownsImage;
    private int _disposed;

    private HObjectGrabFrame(HObject image, bool ownsImage, DateTimeOffset capturedAtUtc)
    {
        HOperatorSet.CountObj(image, out var objectCount);
        using (objectCount)
        {
            if (objectCount.I != 1)
                throw new ArgumentException("Exactly one image is required.", nameof(image));
        }

        HOperatorSet.CountChannels(image, out var channels);
        using (channels)
        {
            ChannelCount = channels.I;
            if (ChannelCount == 1)
            {
                HOperatorSet.GetImagePointer1(image, out var pointer, out var type, out var width, out var height);
                using (pointer) using (type) using (width) using (height)
                {
                    PixelTypeName = type.S;
                    Width = width.I;
                    Height = height.I;
                }
            }
            else
            {
                HOperatorSet.GetImagePointer3(
                    image, out var red, out var green, out var blue, out var pixelType, out var width, out var height);
                using (red) using (green) using (blue) using (pixelType) using (width) using (height)
                {
                    PixelTypeName = pixelType.S;
                    Width = width.I;
                    Height = height.I;
                }
            }
        }

        _image = image;
        _ownsImage = ownsImage;
        CapturedAtUtc = capturedAtUtc;
    }

    /// <summary>借用调用方拥有的图像：本视图不释放它。</summary>
    /// <param name="image">调用方拥有的图像；必须恰好包含一张图。</param>
    /// <returns>设备帧视图。</returns>
    /// <exception cref="ArgumentException">图像为空或不恰好包含一张图。</exception>
    public static HObjectGrabFrame Borrow(HObject image)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        return new HObjectGrabFrame(image, ownsImage: false, DateTimeOffset.UtcNow);
    }

    /// <summary>接管图像所有权：释放本视图时一并释放它。</summary>
    /// <param name="image">由抓取返回、所有权归调用方的图像。</param>
    /// <param name="capturedAtUtc">观测时刻。</param>
    /// <returns>设备帧视图。</returns>
    /// <exception cref="ArgumentException">图像为空或不恰好包含一张图。</exception>
    public static HObjectGrabFrame Own(HObject image, DateTimeOffset capturedAtUtc)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        return new HObjectGrabFrame(image, ownsImage: true, capturedAtUtc);
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

    /// <inheritdoc/>
    public void CopyChannelInto(int channel, byte[] destination)
    {
        if (destination is null)
            throw new ArgumentNullException(nameof(destination));
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(HObjectGrabFrame));

        var bytesPerPixel = PixelTypeName == "uint2" ? 2 : 1;
        var expected = checked(Width * Height * bytesPerPixel);
        if (destination.Length != expected)
        {
            throw new ArgumentException(
                $"目标缓冲长度必须等于 Width*Height*每像素字节数（{expected}），实得 {destination.Length}。",
                nameof(destination));
        }

        if (ChannelCount == 1)
        {
            if (channel != 0)
                throw new ArgumentOutOfRangeException(nameof(channel), "单通道图像只接受通道 0。");
            HOperatorSet.GetImagePointer1(_image, out var pointer, out var type, out var width, out var height);
            using (pointer) using (type) using (width) using (height)
            {
                Marshal.Copy(pointer.IP, destination, 0, destination.Length);
            }

            return;
        }

        HOperatorSet.GetImagePointer3(
            _image, out var red, out var green, out var blue, out var pixelType, out var w, out var h);
        using (pixelType) using (w) using (h)
        {
            var plane = channel switch
            {
                0 => red,
                1 => green,
                2 => blue,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), "三通道图像的通道下标只能是 0、1、2。")
            };

            using (red) using (green) using (blue)
            {
                Marshal.Copy(plane.IP, destination, 0, destination.Length);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>只有通过 <see cref="Own"/> 创建的视图才真的释放底层图像。</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_ownsImage)
            _image.Dispose();
    }
}

/// <summary>
/// 基于 <see cref="HFramegrabber"/> 的真实设备：持有唯一句柄，同时驱动主动单次采集与持续取流。
/// <para>
/// 句柄在设备被释放之前保持打开（单次采集之间、两次布防之间都不关闭），因此不会重复打开同一台相机，
/// 两种采集模式也共用同一条取流通道。
/// </para>
/// <para>
/// 两条取流路径的 HALCON 写法**不同**，这是厂商差异而不是不一致：
/// 主动单次采集用 <c>grab_image</c>（自行完成"启动—等一帧—停止"），等待上限由 <c>grab_timeout</c> 给出；
/// 持续取流遵循官方示例 <c>genicamtl_simple.hdev</c>，先用 <c>grab_image_start</c> 激活一次，
/// 之后每次 <c>grab_image_async</c> 在返回前自动启动下一轮，停止时用
/// <c>set_framegrabber_param('do_abort_grab', -1)</c> 打断正在进行的抓取。
/// </para>
/// <para>
/// 触发与曝光/增益按"先打开、后写参数"的顺序在 <see cref="ApplyArmParameters"/> 里写入，
/// 与 Basler 侧保持同构；差异只在写入手段上（HALCON 只能走 <c>set_framegrabber_param</c>）。
/// 参数到设备参数的翻译全部交给 <see cref="HalconFramegrabberParameters"/>，本类型只负责执行与报错。
/// </para>
/// </summary>
internal sealed class HalconFramegrabberCamera : IHalconStreamCamera
{
    private readonly HalconAcquisitionBinding _binding;
    private readonly object _sync = new object();

    private HFramegrabber? _camera;
    private bool _grabbing;
    private bool _captureInFlight;
    private int _disposed;

    /// <summary>创建设备；此时尚未打开。</summary>
    /// <param name="binding">Provider 私有绑定。</param>
    /// <exception cref="ArgumentNullException">绑定为空。</exception>
    public HalconFramegrabberCamera(HalconAcquisitionBinding binding) =>
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));

    /// <inheritdoc/>
    public bool IsOpen => _camera is not null;

    /// <inheritdoc/>
    public void Open()
    {
        ThrowIfDisposed();
        if (_camera is not null)
            return;

        HFramegrabber created;
        try
        {
            // 触发一律先取接口默认值：真正的触发模式与触发源在 ApplyArmParameters 里写入，
            // 这样"保持当前设置"才真的什么都不改，而不是被打开参数顺手改成自由运行。
            created = new HFramegrabber(
                _binding.InterfaceName,
                1, 1, 0, 0, 0, 0,
                "default", -1, "default", -1.0, "default", "default", _binding.DeviceName, 0, -1);
        }
        catch (HOperatorException exception)
        {
            throw HalconStreamFaults.Classify(exception.GetErrorCode(), exception.GetErrorMessage(), exception);
        }

        _camera = created;
    }

    /// <inheritdoc/>
    /// <remarks>本方法不抛异常：它的调用点都是"释放设备"路径，此时抛异常只会掩盖真正的原因。</remarks>
    public void Close()
    {
        HFramegrabber? camera;
        lock (_sync)
        {
            camera = _camera;
            _camera = null;
            // 句柄没了，两条取流路径的状态也必须一起复位，否则重新打开后会把"仍在取流"带过去。
            _grabbing = false;
            _captureInFlight = false;
        }

        if (camera is null)
            return;

        try
        {
            camera.CloseFramegrabber();
        }
        catch (HOperatorException)
        {
            // 设备已经不再被本对象持有；关闭失败无处可报，交由下层句柄释放兜底。
        }
        finally
        {
            camera.Dispose();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 参数顺序固定为"抓取超时 → 曝光/增益 → 触发"：抓取超时先落地，后续参数写失败时这次调用也
    /// 不会留下"等不到帧却还是无限等待"的状态。翻译与顺序全部由
    /// <see cref="HalconFramegrabberParameters"/> 决定，本方法只负责写入与把厂商异常转成公共异常。
    /// </remarks>
    public void ApplyArmParameters(
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels,
        int grabTimeoutMilliseconds)
    {
        var camera = RequireOpen();
        lock (_sync)
        {
            if (_captureInFlight)
            {
                // 拒绝必须发生在任何设备动作之前：句柄正被一次单次采集占用，
                // 此时写布防参数会把那次采集的设备设置改掉，采集到的就不是请求那一刻的图。
                throw new InvalidOperationException(
                    "HALCON 设备上已经有一次单次采集在占用句柄；布防不能与它并发。");
            }
        }

        ApplyParametersCore(camera, triggerMode, exposureMicroseconds, gainDecibels, grabTimeoutMilliseconds);
    }

    /// <summary>
    /// 实际写入参数；布防与单次采集共用它。
    /// <para>
    /// 单次采集在途时也要写参数，因此不能走带互斥检查的 <see cref="ApplyArmParameters"/>——
    /// 互斥检查的是"另一条取流路径"，不是"本次调用自己"。
    /// </para>
    /// </summary>
    /// <param name="camera">已打开的采集设备。</param>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示不改写。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示不改写。</param>
    /// <param name="grabTimeoutMilliseconds">单次抓取等待上限。</param>
    /// <exception cref="ArgumentOutOfRangeException">抓取超时不为正。</exception>
    private void ApplyParametersCore(
        HFramegrabber camera,
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels,
        int grabTimeoutMilliseconds)
    {
        if (grabTimeoutMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(grabTimeoutMilliseconds));

        WriteParameters(camera, new[]
        {
            new HalconDeviceParameter(HalconFramegrabberParameters.GrabTimeout, grabTimeoutMilliseconds)
        });
        WriteParameters(
            camera,
            HalconFramegrabberParameters.ResolveCaptureParameters(exposureMicroseconds, gainDecibels));
        WriteParameters(
            camera,
            HalconFramegrabberParameters.ResolveTrigger(triggerMode, _binding.TriggerSource));
    }

    /// <inheritdoc/>
    public IHalconGrabFrame CaptureSingleFrame(
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (timeoutMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        cancellationToken.ThrowIfCancellationRequested();

        var camera = RequireOpen();
        lock (_sync)
        {
            if (_grabbing)
            {
                throw new InvalidOperationException(
                    "HALCON 设备正在持续取流；同一台设备只有一条取流通道，不能同时用于单次采集。");
            }

            if (_captureInFlight)
                throw new InvalidOperationException("HALCON 设备上已经有一次单次采集在进行中。");
            _captureInFlight = true;
        }

        try
        {
            ApplyParametersCore(camera, triggerMode, exposureMicroseconds, gainDecibels, timeoutMilliseconds);

            if (HalconFramegrabberParameters.RequiresSoftwareTriggerCommand(triggerMode))
            {
                // 官方示例 genicamtl_software_trigger.hdev 在每次抓图前单独发一条软触发命令；
                // 只把触发方式设成 'Software' 并不会自己产生触发。
                WriteParameters(camera, new[] { HalconFramegrabberParameters.SoftwareTriggerCommand() });
            }

            // 取消只能在调用边界检查：一旦进入 SDK 的阻塞抓取，只能等 grab_timeout 到点。
            cancellationToken.ThrowIfCancellationRequested();

            HImage image;
            try
            {
                // grab_image 自行完成"启动采集—等一张图—停止采集"，不需要先 GrabImageStart；
                // 等待上限由上面写入的 grab_timeout 决定。
                image = camera.GrabImage();
            }
            catch (HOperatorException exception)
            {
                throw TranslateGrabFailure(exception, timeoutMilliseconds);
            }

            // 抓取返回的图像所有权在调用方：由设备帧视图负责释放。
            return HObjectGrabFrame.Own(image, DateTimeOffset.UtcNow);
        }
        finally
        {
            lock (_sync)
                _captureInFlight = false;
        }
    }

    /// <summary>
    /// 把一次抓取失败翻译成公共异常。超时是"这一轮没有等到帧"，与设备故障、许可证故障互不等价。
    /// </summary>
    /// <param name="exception">厂商异常。</param>
    /// <param name="timeoutMilliseconds">本次等待上限；进入诊断文本便于现场核对。</param>
    /// <returns>应当抛出的公共异常。</returns>
    private static Exception TranslateGrabFailure(HOperatorException exception, int timeoutMilliseconds)
    {
        var code = exception.GetErrorCode();
        if (HalconStreamFaults.IsGrabTimeout(code))
        {
            return new VisionCaptureTimeoutException(
                $"HALCON 采集超时（{timeoutMilliseconds} ms）；外部触发未到达或设备未出图。"
                + $"请检查触发接线、触发源配置和曝光时间。底层原因：{exception.GetErrorMessage()}");
        }

        return HalconStreamFaults.Classify(code, exception.GetErrorMessage(), exception);
    }

    /// <summary>逐条写入设备参数；写入失败必须变成可诊断的公共异常，而不是厂商异常越过 Provider 边界。</summary>
    /// <param name="camera">已打开的采集设备。</param>
    /// <param name="parameters">按顺序写入的参数；空序列是空操作，即"保持设备当前设置"。</param>
    /// <exception cref="VisionParameterNotSupportedException">设备不接受该参数名或取值。</exception>
    /// <exception cref="VisionAcquisitionException">设备、许可证或超时类故障。</exception>
    private static void WriteParameters(HFramegrabber camera, IReadOnlyList<HalconDeviceParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            try
            {
                WriteParameter(camera, parameter);
            }
            catch (HOperatorException exception)
            {
                var code = exception.GetErrorCode();
                if (HalconStreamFaults.IsGrabTimeout(code)
                    || HalconStreamFaults.IsLicenseFault(code)
                    || HalconStreamFaults.IsDeviceFault(code))
                {
                    // 已经能判定为设备侧故障（例如设备在写参数时掉线）：不要伪装成"参数不支持"。
                    throw HalconStreamFaults.Classify(code, exception.GetErrorMessage(), exception);
                }

                // 其余一律按"参数不支持"上报，并且把参数名与取值一起带出去：
                // 现场最常见的原因就是采集接口不提供该节点，或该取值超出设备允许范围。
                throw new VisionParameterNotSupportedException(
                    $"HALCON 设备不接受参数 {parameter.Name}={parameter.DisplayValue}"
                    + $"（错误码 {code}）：{exception.GetErrorMessage()}。"
                    + "请核对该参数名与取值是否被当前采集接口支持。");
            }
        }
    }

    private static void WriteParameter(HFramegrabber camera, HalconDeviceParameter parameter)
    {
        // 必须把两个实参都显式写成 HTuple：HTuple 与 string 之间存在双向隐式转换，
        // 只写 (string, HTuple) 会让 (string, string) 与 (HTuple, HTuple) 两个重载同时可用而产生二义性。
        using var name = (HTuple)parameter.Name;
        using var value = parameter.Value switch
        {
            string text => (HTuple)text,
            int number => (HTuple)number,
            double number => (HTuple)number,
            _ => throw new InvalidOperationException(
                $"设备参数 {parameter.Name} 的取值类型不受支持：{parameter.Value?.GetType().FullName ?? "null"}。")
        };
        camera.SetFramegrabberParam(name, value);
    }

    /// <inheritdoc/>
    public IHalconGrabFrame GrabOnce()
    {
        var camera = RequireOpen();

        lock (_sync)
        {
            if (_captureInFlight)
            {
                throw new InvalidOperationException(
                    "HALCON 设备上已经有一次单次采集在进行中；持续取流不能与它并发。");
            }
        }

        try
        {
            lock (_sync)
            {
                if (!_grabbing)
                {
                    // 官方示例的持续取流写法：start 激活一次，之后 async 在返回前自动启动下一轮。
                    camera.GrabImageStart(-1.0);
                    _grabbing = true;
                }
            }

            // MaxDelay 取 -1（停用"图像太旧就丢"的机制）：缓冲源要的是每一帧，而不是最新的一帧。
            var image = camera.GrabImageAsync(-1.0);
            return HObjectGrabFrame.Own(image, DateTimeOffset.UtcNow);
        }
        catch (HOperatorException exception)
        {
            // 抓取链已经断了：下一轮必须重新 start，否则后续调用会一直失败。
            lock (_sync)
                _grabbing = false;
            throw HalconStreamFaults.Classify(exception.GetErrorCode(), exception.GetErrorMessage(), exception);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// HALCON 的 <c>do_abort_grab</c> 是否被支持取决于具体采集接口，
    /// 因此这里只尽力而为：不支持时静默失败，停止退化为"等满抓取超时"。
    /// </remarks>
    public void AbortGrab()
    {
        HFramegrabber? camera;
        lock (_sync)
            camera = _camera;

        if (camera is null)
            return;

        try
        {
            WriteParameter(camera, new HalconDeviceParameter(HalconFramegrabberParameters.AbortGrab, -1));
        }
        catch (HOperatorException)
        {
            // 该接口不支持 do_abort_grab；停止等待由会话侧的超时上限兜底。
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Close();
    }

    private HFramegrabber RequireOpen()
    {
        ThrowIfDisposed();
        return _camera ?? throw new InvalidOperationException("HALCON 采集设备尚未打开。");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(HalconFramegrabberCamera));
    }
}
#endif
