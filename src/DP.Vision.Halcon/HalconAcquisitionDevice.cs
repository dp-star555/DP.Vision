using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using DP.Vision.Acquisition.Drivers;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON 采集设备适配器。
///
/// 主动单次采集与外部回调持续取流共用<b>同一个已连接的 <c>HFramegrabber</c></b>，
/// 生命周期规则见 <see cref="StreamingDeviceCore{TCamera, TSession}"/>。
/// 两条路径共用同一份像素落地与参数写入逻辑：帧一律在设备边界内复制为中立图像，
/// 绝不把 <c>HObject</c> 或裸指针交给调用方。
/// </summary>
public sealed class HalconAcquisitionDevice : IVisionAcquisitionDevice, IVisionStreamingAcquisitionDevice
{
    private readonly HalconAcquisitionBinding _binding;
    private readonly StreamingDeviceCore<IHalconStreamCamera, HalconStreamSession> _core;

    /// <summary>创建设备Adapter。</summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <exception cref="ArgumentNullException">绑定为空。</exception>
    public HalconAcquisitionDevice(HalconAcquisitionBinding binding)
        : this(binding, HalconStreamCameras.Create)
    {
    }

    /// <summary>创建设备Adapter，并注入长连接设备工厂（供测试替身使用）。</summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <param name="cameraFactory">长连接设备工厂。</param>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    internal HalconAcquisitionDevice(
        HalconAcquisitionBinding binding,
        Func<HalconAcquisitionBinding, IHalconStreamCamera> cameraFactory)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (cameraFactory is null)
            throw new ArgumentNullException(nameof(cameraFactory));
        _core = new StreamingDeviceCore<IHalconStreamCamera, HalconStreamSession>(
            () => cameraFactory(binding), "HALCON", binding.BindingId, nameof(HalconAcquisitionDevice));
        Identity = new VisionDeviceIdentity(
            HalconAcquisitionProvider.ProviderIdentity,
            binding.BindingId,
            binding.CanonicalKey,
            binding.SerialNumber);
    }

    /// <inheritdoc/>
    public VisionDeviceIdentity Identity { get; }

    /// <summary>
    /// 外部回调缓冲源的布防触发模式：绑定声明了触发源就显式设为外部触发；
    /// 否则保持设备当前设置，不猜物理接线。
    /// </summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <returns>布防时写入设备的触发模式。</returns>
    internal static EVisionTriggerMode ResolveArmTriggerMode(HalconAcquisitionBinding binding)
    {
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));
        return binding.TriggerSource is null ? EVisionTriggerMode.KeepCurrent : EVisionTriggerMode.External;
    }

    /// <inheritdoc/>
    /// <remarks>复用同一个已连接的 <c>HFramegrabber</c>；布防期间（或断线之后）明确拒绝，而不是抢占取流通道。</remarks>
    public ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        var camera = _core.CameraForCapture();

        // HALCON 的打开与抓图都是阻塞调用，隔离到线程池；取消在调用边界检查。
        return new ValueTask<VisionProviderFrame>(
            Task.Run(() => Capture(camera, request, cancellationToken), cancellationToken));
    }

    /// <summary>布防长连接并把后续设备帧交给接收方；设备在两次布防之间保持打开，只有设备被释放时才关闭。</summary>
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
                camera => new HalconStreamSession(camera, sink),
                session => session.Arm(
                    ResolveArmTriggerMode(_binding),
                    exposureMicroseconds: null,
                    gainDecibels: null,
                    _binding.GrabTimeoutMilliseconds))
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>顺序是契约的一部分：先停流（并等待采集线程与在途交付退出），再关闭设备。</remarks>
    public ValueTask DisposeAsync()
    {
        return _core.DisposeAsync();
    }

    /// <summary>按请求抓取一帧；设备句柄的打开与参数写入全部由共享设备负责。</summary>
    /// <param name="camera">两种采集模式共用的设备。</param>
    /// <param name="request">采集请求。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>像素已落地为中立图像的帧。</returns>
    private static VisionProviderFrame Capture(
        IHalconStreamCamera camera,
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

        // HALCON 的通用采集层不提供设备帧序号，DeviceSequence 只能上报空值。
        var (image, observation) = HalconNeutralFrames.CopyObserved(grabFrame);
        return new VisionProviderFrame(
            image,
            grabFrame.CapturedAtUtc,
            deviceSequence: null,
            transferObservation: observation);
    }
}
