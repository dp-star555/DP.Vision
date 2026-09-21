using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// Basler pylon 采集设备适配器。
///
/// 主动单次采集与外部回调持续取流共用<b>同一个已连接相机</b>：
/// 相机在第一次使用时打开一次，之后跨采集请求与跨布防复用，只有设备被释放时才关闭；
/// 因此一台物理设备上只存在一条取流通道，两种模式不能同时进行。
/// 两条路径也共用同一份像素落地逻辑：帧一律在设备边界内复制为中立图像，
/// 绝不把 pylon 的缓冲或指针交给调用方。
/// 设备选择要求唯一匹配，不做"取第一台"的回退。
/// </summary>
public sealed class BaslerAcquisitionDevice : IVisionAcquisitionDevice, IVisionStreamingAcquisitionDevice
{
    private readonly BaslerAcquisitionBinding _binding;
    private readonly Func<BaslerAcquisitionBinding, IBaslerStreamCamera> _cameraFactory;
    private readonly object _sync = new object();

    private IBaslerStreamCamera? _camera;
    private BaslerStreamSession? _session;
    private bool _arming;
    private bool _disposed;

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
        _cameraFactory = cameraFactory ?? throw new ArgumentNullException(nameof(cameraFactory));
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
    /// <remarks>
    /// 复用同一个已连接相机：这里不新建、不打开、也不关闭设备，相机只在第一次使用时打开一次。
    /// 单次采集与外部回调共用一条取流通道，因此布防期间（或断线之后）明确拒绝，而不是抢占通道。
    /// </remarks>
    public ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        IBaslerStreamCamera camera;
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BaslerAcquisitionDevice));

            if (_session is not null)
            {
                // 断线比"正在布防"更值得优先报告：设备已经不可用，重试也不会成功。
                if (_session.Failure is { } failure)
                {
                    throw new VisionDeviceOfflineException(
                        $"Basler 设备 {_binding.BindingId} 的持续取流已因故障结束（{failure}）；"
                        + "请先排除故障并重启采集运行时，再按请求单次采集。");
                }

                throw new VisionSourceConfigurationException(
                    $"Basler 设备 {_binding.BindingId} 正在作为外部回调缓冲源布防，不能同时按请求单次采集；"
                    + "同一物理设备只有一条取流通道，一次只能有一种采集模式。");
            }

            // 未装配 pylon 支持时相机工厂自行明确失败，不做静默降级。
            camera = _camera ??= _cameraFactory(_binding);
        }

        // pylon 的打开/抓图是阻塞调用，隔离到线程池；取消在调用边界检查。
        return new ValueTask<VisionProviderFrame>(
            Task.Run(() => Capture(camera, request, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// 布防并把后续回调帧交给接收方。
    /// <para>
    /// 相机在整个设备生命周期内保持打开（单次采集与布防共用同一个设备对象），
    /// 因此第二根根运行不会重新打开设备；只有设备被释放时才关闭相机。
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

        BaslerStreamSession? previous;
        BaslerStreamSession session;
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BaslerAcquisitionDevice));

            // 一台设备同时只能有一条接收流：仍在进行的布防不允许被替换，布防过程本身也不允许重入。
            if (_arming || (_session is not null && !_session.Stopped))
                throw new InvalidOperationException("该设备已经在布防状态；一台设备同时只允许一条接收流。");

            _arming = true;
            previous = _session;
            _session = null;

            _camera ??= _cameraFactory(_binding);
            session = new BaslerStreamSession(_camera, sink);
        }

        try
        {
            // 旧会话已停止，但它的设备帧可能还没释放完；先收尾再布防（对已释放的会话是空操作）。
            if (previous is not null)
                await previous.DisposeAsync().ConfigureAwait(false);

            // 布防参数来自机器配置而不是节点请求：缓冲源不接受节点级曝光/增益覆盖。
            // 布防失败时 _session 保持为空，下一次布防可以重试，相机保持打开以便复用。
            session.Arm(ResolveArmTriggerMode(_binding), exposureMicroseconds: null, gainDecibels: null);

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
    /// <remarks>顺序是契约的一部分：先停流（并等待已进入的回调退出），再关闭相机。</remarks>
    public async ValueTask DisposeAsync()
    {
        BaslerStreamSession? session;
        IBaslerStreamCamera? camera;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            _session = null;
            camera = _camera;
            _camera = null;
        }

        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
        camera?.Dispose();
    }

    /// <summary>按请求抓取一帧；设备对象的打开与参数写入全部由共享相机负责。</summary>
    /// <param name="camera">两种采集模式共用的相机。</param>
    /// <param name="request">采集请求。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>像素已落地为中立图像的帧。</returns>
    private VisionProviderFrame Capture(
        IBaslerStreamCamera camera,
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        camera.Open();
        using var grabFrame = camera.CaptureSingleFrame(
            request.TriggerMode,
            request.ExposureMicroseconds,
            request.GainDecibels,
            ToTimeoutMilliseconds(request.Timeout),
            cancellationToken);

        return new VisionProviderFrame(
            BaslerNeutralFrames.Copy(grabFrame),
            grabFrame.CapturedAtUtc,
            grabFrame.ImageNumber);
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(timeout.TotalMilliseconds)));
}
