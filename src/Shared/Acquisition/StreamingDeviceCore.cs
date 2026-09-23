using System;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition.Drivers;

/// <summary>驱动内部的持续取流会话在设备生命周期里需要被观察的状态。</summary>
internal interface IStreamingDeviceSession : IVisionAcquisitionStream
{
    /// <summary>接收是否已经结束（正常停止或故障结束）。</summary>
    bool Stopped { get; }

    /// <summary>结束原因；正常停止时为空。</summary>
    string? Failure { get; }
}

/// <summary>
/// 相机驱动设备适配器共用的生命周期规则，以源码链接方式编入各驱动程序集，不属于公共契约。
/// <para>
/// 主动单次采集与外部回调持续取流共用<b>同一个已连接相机</b>：相机在第一次使用时打开一次，
/// 之后跨采集请求与跨布防复用，只有设备被释放时才关闭；因此一台物理设备上只存在一条取流通道，两种模式不能同时进行。
/// </para>
/// </summary>
/// <typeparam name="TCamera">驱动私有的长连接相机。</typeparam>
/// <typeparam name="TSession">驱动私有的持续取流会话。</typeparam>
internal sealed class StreamingDeviceCore<TCamera, TSession>
    where TCamera : class, IDisposable
    where TSession : class, IStreamingDeviceSession
{
    private readonly object _sync = new object();
    private readonly Func<TCamera> _cameraFactory;
    private readonly string _vendorName;
    private readonly string _bindingId;
    private readonly string _objectName;

    private TCamera? _camera;
    private TSession? _session;
    private bool _arming;
    private bool _disposed;

    /// <summary>创建生命周期核心。</summary>
    /// <param name="cameraFactory">相机工厂；一台设备只会调用一次，两种采集模式共用返回的对象。未装配SDK时由它明确失败。</param>
    /// <param name="vendorName">诊断消息中的厂商/SDK名称。</param>
    /// <param name="bindingId">诊断消息中的设备绑定标识。</param>
    /// <param name="objectName">释放后访问时报告的对象名称。</param>
    public StreamingDeviceCore(Func<TCamera> cameraFactory, string vendorName, string bindingId, string objectName)
    {
        _cameraFactory = cameraFactory;
        _vendorName = vendorName;
        _bindingId = bindingId;
        _objectName = objectName;
    }

    /// <summary>
    /// 为单次采集取得共享相机：这里不新建会话、不打开、也不关闭设备。
    /// 单次采集与外部回调共用一条取流通道，因此布防期间（或断线之后）明确拒绝，而不是抢占通道。
    /// </summary>
    /// <returns>共享相机。</returns>
    public TCamera CameraForCapture()
    {
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(_objectName);

            if (_session is not null)
            {
                // 断线比"正在布防"更值得优先报告：设备已经不可用，重试也不会成功。
                if (_session.Failure is { } failure)
                {
                    throw new VisionDeviceOfflineException(
                        $"{_vendorName} 设备 {_bindingId} 的持续取流已因故障结束（{failure}）；"
                        + "请先排除故障并重启采集运行时，再按请求单次采集。");
                }

                throw new VisionSourceConfigurationException(
                    $"{_vendorName} 设备 {_bindingId} 正在作为外部回调缓冲源布防，不能同时按请求单次采集；"
                    + "同一物理设备只有一条取流通道，一次只能有一种采集模式。");
            }

            return _camera ??= _cameraFactory();
        }
    }

    /// <summary>
    /// 布防并返回新的接收流。
    /// <para>
    /// 上一次布防已经停止时允许再次布防（宿主每根根运行都会重新布防），此时先收尾旧会话再建立新会话；
    /// 上一次布防仍在进行中则明确拒绝，不静默替换接收方。
    /// </para>
    /// </summary>
    /// <param name="createSession">在共享相机上创建会话，不做阻塞的设备操作。</param>
    /// <param name="arm">布防新会话；失败时会话被释放，相机保持打开以便下一次重试。</param>
    /// <returns>停止本次接收的句柄。</returns>
    public async ValueTask<IVisionAcquisitionStream> StartStreamAsync(
        Func<TCamera, TSession> createSession,
        Action<TSession> arm)
    {
        TSession? previous;
        TSession session;
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(_objectName);

            // 一台设备同时只能有一条接收流：仍在进行的布防不允许被替换，布防过程本身也不允许重入。
            if (_arming || (_session is not null && !_session.Stopped))
                throw new InvalidOperationException("该设备已经在布防状态；一台设备同时只允许一条接收流。");

            _arming = true;
            previous = _session;
            _session = null;

            _camera ??= _cameraFactory();
            session = createSession(_camera);
        }

        try
        {
            // 旧会话已停止，但它的设备帧可能还没释放完；先收尾再布防（对已释放的会话是空操作）。
            if (previous is not null)
                await previous.DisposeAsync().ConfigureAwait(false);

            // 布防失败时 _session 保持为空，下一次布防可以重试。
            arm(session);

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

    /// <summary>释放设备；顺序是契约的一部分：先停流（并等待在途交付退出），再关闭相机。</summary>
    /// <returns>释放完成。</returns>
    public async ValueTask DisposeAsync()
    {
        TSession? session;
        TCamera? camera;
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
}

/// <summary>驱动设备适配器共用的小工具。</summary>
internal static class StreamingDevices
{
    /// <summary>把请求超时换算为SDK需要的正整数毫秒。</summary>
    /// <param name="timeout">请求超时。</param>
    /// <returns>1至int.MaxValue毫秒。</returns>
    public static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        return (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(timeout.TotalMilliseconds)));
    }
}
