using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using DP.Vision.Acquisition.Drivers;

namespace DP.Vision.Basler;

/// <summary>
/// Basler pylon 采集设备适配器。
///
/// 主动单次采集与外部回调持续取流共用<b>同一个已连接相机</b>，生命周期规则见 <see cref="StreamingDeviceCore{TCamera, TSession}"/>。
/// 两条路径也共用同一份像素落地逻辑：帧一律在设备边界内复制为中立图像，
/// 绝不把 pylon 的缓冲或指针交给调用方。
/// 设备选择要求唯一匹配，不做"取第一台"的回退。
/// </summary>
public sealed class BaslerAcquisitionDevice : IVisionAcquisitionDevice, IVisionStreamingAcquisitionDevice
{
    private readonly BaslerAcquisitionBinding _binding;
    private readonly StreamingDeviceCore<IBaslerStreamCamera, BaslerStreamSession> _core;

    /// <summary>创建设备Adapter。</summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <exception cref="ArgumentNullException">绑定为空。</exception>
    public BaslerAcquisitionDevice(BaslerAcquisitionBinding binding)
        : this(binding, BaslerStreamCameras.Create)
    {
    }

    /// <summary>创建设备Adapter，并注入相机工厂（供测试替身使用）。</summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <param name="cameraFactory">相机工厂；一台设备只会调用一次，两种采集模式共用返回的对象。</param>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    internal BaslerAcquisitionDevice(
        BaslerAcquisitionBinding binding,
        Func<BaslerAcquisitionBinding, IBaslerStreamCamera> cameraFactory)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (cameraFactory is null)
            throw new ArgumentNullException(nameof(cameraFactory));
        _core = new StreamingDeviceCore<IBaslerStreamCamera, BaslerStreamSession>(
            () => cameraFactory(binding), "Basler", binding.BindingId, nameof(BaslerAcquisitionDevice));
        Identity = new VisionDeviceIdentity(
            BaslerAcquisitionProvider.ProviderIdentity,
            binding.BindingId,
            binding.CanonicalKey,
            binding.SerialNumber,
            VendorName: "Basler");
    }

    /// <inheritdoc/>
    public VisionDeviceIdentity Identity { get; }

    /// <summary>
    /// 外部回调缓冲源的布防触发模式：绑定声明了触发源就显式设为外部触发；
    /// 否则保持设备当前设置，不猜物理接线。
    /// </summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <returns>布防时写入设备的触发模式。</returns>
    internal static EVisionTriggerMode ResolveArmTriggerMode(BaslerAcquisitionBinding binding)
    {
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));
        return binding.TriggerSource is null ? EVisionTriggerMode.KeepCurrent : EVisionTriggerMode.External;
    }

    /// <inheritdoc/>
    /// <remarks>复用同一个已连接相机；布防期间（或断线之后）明确拒绝，而不是抢占取流通道。</remarks>
    public ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        var camera = _core.CameraForCapture();

        // pylon 的打开/抓图是阻塞调用，隔离到线程池；取消在调用边界检查。
        return new ValueTask<VisionProviderFrame>(
            Task.Run(() => Capture(camera, request, cancellationToken), cancellationToken));
    }

    /// <summary>布防并把后续回调帧交给接收方；相机在两次布防之间保持打开，只有设备被释放时才关闭。</summary>
    /// <param name="sink">帧接收方。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>停止本次接收的句柄。</returns>
    /// <exception cref="ArgumentNullException">接收方为空。</exception>
    /// <exception cref="InvalidOperationException">设备已经在布防状态，或已被释放。</exception>
    public async ValueTask<IVisionAcquisitionStream> StartStreamAsync(
        IVisionProviderFrameSink sink,
        CancellationToken cancellationToken)
    {
        if (sink is null)
            throw new ArgumentNullException(nameof(sink));
        cancellationToken.ThrowIfCancellationRequested();

        // 布防参数来自机器配置而不是节点请求：缓冲源不接受节点级曝光/增益覆盖。
        return await _core.StartStreamAsync(
                camera => new BaslerStreamSession(camera, sink),
                session => session.Arm(ResolveArmTriggerMode(_binding), exposureMicroseconds: null, gainDecibels: null))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>顺序是契约的一部分：先停流（并等待已进入的回调退出），再关闭相机。</remarks>
    public ValueTask DisposeAsync()
    {
        return _core.DisposeAsync();
    }

    /// <summary>按请求抓取一帧；设备对象的打开与参数写入全部由共享相机负责。</summary>
    /// <param name="camera">两种采集模式共用的相机。</param>
    /// <param name="request">采集请求。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>像素已落地为中立图像的帧。</returns>
    private static VisionProviderFrame Capture(
        IBaslerStreamCamera camera,
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        camera.Open();
        using var grabFrame = camera.CaptureSingleFrame(
            request.TriggerMode,
            request.ExposureMicroseconds,
            request.GainDecibels,
            StreamingDevices.ToTimeoutMilliseconds(request.Timeout),
            cancellationToken);

        var (image, observation) = BaslerNeutralFrames.CopyObserved(grabFrame);
        return new VisionProviderFrame(
            image,
            grabFrame.CapturedAtUtc,
            grabFrame.ImageNumber,
            observation);
    }
}
