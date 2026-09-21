using System;
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
/// 两种模式共用同一份像素落地与参数写入逻辑，但设备生命周期不同：
/// <list type="bullet">
/// <item>OnDemand：每次采集独立打开/关闭设备，不承诺长连接。</item>
/// <item>BufferedExternal：由长连接会话持有相机，跨布防复用，只在设备释放时关闭；
/// 回调帧在回调边界内复制为中立图像，绝不把 pylon 的缓冲或指针交给调用方。</item>
/// </list>
/// 设备选择要求唯一匹配，不做"取第一台"的回退。
/// </summary>
public sealed class BaslerAcquisitionDevice : IVisionAcquisitionDevice, IVisionStreamingAcquisitionDevice
{
    private readonly BaslerAcquisitionBinding _binding;
    private readonly Func<BaslerAcquisitionBinding, IBaslerStreamCamera> _cameraFactory;
    private readonly object _sync = new object();

    private IBaslerStreamCamera? _streamCamera;
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

    /// <summary>创建设备Adapter，并注入长连接设备工厂（供测试替身使用）。</summary>
    /// <param name="binding">Provider私有绑定。</param>
    /// <param name="cameraFactory">长连接设备工厂。</param>
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
    public ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BaslerAcquisitionDevice));
            if (_session is not null)
            {
                throw new VisionSourceConfigurationException(
                    $"Basler 设备 {_binding.BindingId} 正在作为外部回调缓冲源布防，不能同时按请求单次采集；"
                    + "同一物理设备只能有一种采集模式。");
            }
        }

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

    /// <summary>
    /// 布防长连接并把后续回调帧交给接收方。
    /// <para>
    /// 相机在两次布防之间保持打开，因此第二根根运行不会重新打开设备；只有设备被释放时才关闭相机。
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

            _streamCamera ??= _cameraFactory(_binding);
            session = new BaslerStreamSession(_streamCamera, sink);
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
            camera = _streamCamera;
            _streamCamera = null;
        }

        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
        camera?.Dispose();
    }

#if BASLER_SDK
    private VisionProviderFrame Capture(VisionCaptureRequest request, CancellationToken cancellationToken)
    {
        var cameraInfo = BaslerCameraSelection.Resolve(_binding);
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
            BaslerCameraParameters.Apply(
                camera, _binding, request.TriggerMode, request.ExposureMicroseconds, request.GainDecibels);
            using var grabFrame = PylonGrabFrame.ForRetrieved(
                Grab(camera, request.TriggerMode, timeoutMilliseconds, cancellationToken));
            return new VisionProviderFrame(
                BaslerNeutralFrames.Copy(grabFrame),
                grabFrame.CapturedAtUtc,
                grabFrame.ImageNumber);
        }
        finally
        {
            if (camera.IsOpen)
                camera.Close();
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

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(timeout.TotalMilliseconds)));
#endif
}
