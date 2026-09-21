#if HALCON_SDK
using System;
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
/// 基于 <see cref="HFramegrabber"/> 的真实长连接设备。
/// <para>
/// 采集循环遵循 HALCON 官方示例 <c>genicamtl_simple.hdev</c> 的写法：
/// <c>grab_image_start</c> 激活持续取流，随后每次 <c>grab_image_async</c> 在返回前自动启动下一轮，
/// 停止时用 <c>set_framegrabber_param('do_abort_grab', -1)</c> 打断正在进行的抓取。
/// </para>
/// <para>
/// 触发设置按"先打开、后写参数"的顺序在 <see cref="ApplyArmParameters"/> 里写入，
/// 与 Basler 侧保持同构；差异只在写入手段上（HALCON 只能走 <c>set_framegrabber_param</c>）。
/// </para>
/// </summary>
internal sealed class HalconFramegrabberCamera : IHalconStreamCamera
{
    private readonly HalconAcquisitionBinding _binding;

    private HFramegrabber? _camera;
    private bool _grabbing;
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
        var camera = _camera;
        _camera = null;
        _grabbing = false;
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
    public void ApplyArmParameters(
        EVisionTriggerMode triggerMode,
        string? triggerSource,
        int grabTimeoutMilliseconds)
    {
        var camera = RequireOpen();
        if (grabTimeoutMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(grabTimeoutMilliseconds));

        try
        {
            SetIntegerParam(camera, "grab_timeout", grabTimeoutMilliseconds);

            switch (triggerMode)
            {
                case EVisionTriggerMode.KeepCurrent:
                    // 保持设备当前触发设置：一个触发参数都不写，也不猜物理接线。
                    break;

                case EVisionTriggerMode.FreeRun:
                    camera.SetFramegrabberParam("external_trigger", "false");
                    break;

                case EVisionTriggerMode.External:
                    if (string.IsNullOrWhiteSpace(triggerSource))
                    {
                        throw new VisionSourceConfigurationException(
                            "HALCON 外部触发必须由Provider私有配置显式声明触发源（triggerSource，例如 Line1）；"
                            + "不猜物理接线，也不静默退回自由运行。");
                    }

                    camera.SetFramegrabberParam("external_trigger", "true");

                    // GenICam SFNC 标准节点名。设备不支持这些节点时会明确失败（H_ERR_FGPARAM 等），
                    // 这正是想要的：写不进触发配置比"以为在等触发其实在自由运行"安全得多。
                    camera.SetFramegrabberParam("TriggerSelector", "FrameStart");
                    camera.SetFramegrabberParam("TriggerSource", triggerSource!);
                    camera.SetFramegrabberParam("TriggerMode", "On");
                    break;

                default:
                    throw new VisionParameterNotSupportedException(
                        $"HALCON Adapter 不支持触发模式 {triggerMode}；请在Provider私有配置中设置触发源。");
            }
        }
        catch (HOperatorException exception)
        {
            throw HalconStreamFaults.Classify(exception.GetErrorCode(), exception.GetErrorMessage(), exception);
        }
    }

    /// <inheritdoc/>
    public IHalconGrabFrame GrabOnce()
    {
        var camera = RequireOpen();

        try
        {
            if (!_grabbing)
            {
                // 官方示例的持续取流写法：start 激活一次，之后 async 在返回前自动启动下一轮。
                camera.GrabImageStart(-1.0);
                _grabbing = true;
            }

            // MaxDelay 取 -1（停用"图像太旧就丢"的机制）：缓冲源要的是每一帧，而不是最新的一帧。
            var image = camera.GrabImageAsync(-1.0);
            return HObjectGrabFrame.Own(image, DateTimeOffset.UtcNow);
        }
        catch (HOperatorException exception)
        {
            // 抓取链已经断了：下一轮必须重新 start，否则后续调用会一直失败。
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
        var camera = _camera;
        if (camera is null)
            return;

        try
        {
            SetIntegerParam(camera, "do_abort_grab", -1);
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

    private static void SetIntegerParam(HFramegrabber camera, string name, int value)
    {
        // 必须把两个实参都显式写成 HTuple：HTuple 与 string 之间存在双向隐式转换，
        // 只写 (string, HTuple) 会让 (string, string) 与 (HTuple, HTuple) 两个重载同时可用而产生二义性。
        using var nameTuple = (HTuple)name;
        using var valueTuple = (HTuple)value;
        camera.SetFramegrabberParam(nameTuple, valueTuple);
    }
}
#endif
