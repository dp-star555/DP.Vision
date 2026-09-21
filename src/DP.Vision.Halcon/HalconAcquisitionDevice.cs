using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using DP.Vision.Algorithms;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON 采集设备适配器。
///
/// 两种模式共用同一份像素落地与触发映射逻辑，但设备生命周期不同：
/// <list type="bullet">
/// <item>OnDemand：每次采集独立打开/关闭设备，不承诺长连接。</item>
/// <item>BufferedExternal：由长连接会话持有设备，跨布防复用，只在设备释放时关闭；
/// 采集循环运行在自建线程上，设备帧在边界内复制为中立图像，绝不把 <c>HObject</c> 交给调用方。</item>
/// </list>
/// </summary>
public sealed class HalconAcquisitionDevice : IVisionAcquisitionDevice, IVisionStreamingAcquisitionDevice
{
    private readonly HalconAcquisitionBinding _binding;
    private readonly Func<HalconAcquisitionBinding, IHalconStreamCamera> _cameraFactory;
    private readonly object _sync = new object();

    private IHalconStreamCamera? _streamCamera;
    private HalconStreamSession? _session;
    private bool _arming;
    private bool _disposed;

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
        _cameraFactory = cameraFactory ?? throw new ArgumentNullException(nameof(cameraFactory));
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
    public async ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HalconAcquisitionDevice));
            if (_session is not null)
            {
                throw new VisionSourceConfigurationException(
                    $"HALCON 设备 {_binding.BindingId} 正在作为外部回调缓冲源布防，不能同时按请求单次采集；"
                    + "同一物理设备只能有一种采集模式。");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 现有实现每次采集独立打开/关闭设备；grab_timeout 由请求超时驱动，避免无限等待触发。
        var timeout = (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(request.Timeout.TotalMilliseconds)));
        var capture = new HalconCameraCapture(timeout);
        var options = new CameraCaptureOptions(
            request.ExposureMicroseconds ?? 0,
            request.GainDecibels ?? 0);
        IImageSource image;
        try
        {
            image = await capture
                .CaptureWithTriggerAsync(_binding.CameraId, options, request.TriggerMode, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new VisionProviderUnavailableException(
                HalconAcquisitionProvider.ProviderIdentity,
                "HALCON SDK 未部署；请安装 HALCON 运行时并以 HALCONROOT 或 HalconDotNetPath 构建本程序集。",
                exception);
        }

        // HALCON 的通用采集层不提供设备帧序号，DeviceSequence 只能上报空值。
        return new VisionProviderFrame(image, DateTimeOffset.UtcNow, null);
    }

    /// <summary>
    /// 布防长连接并把后续设备帧交给接收方。
    /// <para>
    /// 设备在两次布防之间保持打开，因此第二根根运行不会重新打开设备；只有设备被释放时才关闭它。
    /// </para>
    /// <para>
    /// 上一次布防已经停止时允许再次布防（宿主每根根运行都会重新布防），此时先收尾旧会话再建立新会话；
    /// 上一次布防仍在进行中则明确拒绝，不静默替换接收方。
    /// </para>
    /// </summary>
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

        HalconStreamSession? previous;
        HalconStreamSession session;
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(HalconAcquisitionDevice));

            // 一台设备同时只能有一条接收流：仍在进行的布防不允许被替换，布防过程本身也不允许重入。
            if (_arming || (_session is not null && !_session.Stopped))
                throw new InvalidOperationException("该设备已经在布防状态；一台设备同时只允许一条接收流。");

            _arming = true;
            previous = _session;
            _session = null;

            _streamCamera ??= _cameraFactory(_binding);
            session = new HalconStreamSession(_streamCamera, sink);
        }

        try
        {
            // 旧会话已停止，但它的设备帧可能还没释放完；先收尾再布防（对已释放的会话是空操作）。
            if (previous is not null)
                await previous.DisposeAsync().ConfigureAwait(false);

            // 布防参数来自机器配置而不是节点请求：缓冲源不接受节点级曝光/增益覆盖。
            // 布防失败时 _session 保持为空，下一次布防可以重试，设备保持打开以便复用。
            session.Arm(
                ResolveArmTriggerMode(_binding),
                _binding.TriggerSource,
                _binding.GrabTimeoutMilliseconds);

            lock (_sync)
                _session = session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_sync)
                _arming = false;
        }

        return session;
    }

    /// <inheritdoc/>
    /// <remarks>顺序是契约的一部分：先停流（并等待采集线程与在途交付退出），再关闭设备。</remarks>
    public async ValueTask DisposeAsync()
    {
        HalconStreamSession? session;
        IHalconStreamCamera? camera;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            _session = null;
            camera = _streamCamera;
            _streamCamera = null;
        }

        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
        camera?.Dispose();
    }
}
