using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition;

/// <summary>
/// 站点级采集运行时；拥有Provider实例、设备Session、互斥与路由，Provider拥有硬件，运行拥有图像。
/// <para>
/// 它同时实现 <see cref="IVisionAcquisitionRunOwner"/>：外部回调缓冲源需要"哪一根运行的帧"这一界定，
/// 而该界定只能由根运行给出。
/// </para>
/// </summary>
public sealed class VisionAcquisitionRuntime : IVisionAcquisition, IVisionAcquisitionRunOwner, IAsyncDisposable
{
    private readonly VisionAcquisitionProviderComposition _composition;
    private readonly Dictionary<string, IVisionAcquisitionProvider> _providers =
        new Dictionary<string, IVisionAcquisitionProvider>(StringComparer.Ordinal);
    private readonly Dictionary<string, VisionResourceSession> _resources =
        new Dictionary<string, VisionResourceSession>(StringComparer.Ordinal);
    private readonly object _gate = new object();
    private bool _disposed;
    private EVisionRuntimeState _runtimeState = EVisionRuntimeState.Created;
    private int _epoch;
    private readonly int _machineConfigurationRevision;
    private RunLease? _activeLease;

    /// <summary>创建运行时。</summary>
    /// <param name="composition">已发布的不可变Provider组合。</param>
    /// <param name="machineConfigurationRevision">生成该组合的机器配置修订号；运行制品用它回答"这根运行跑的是哪一版配置"。</param>
    /// <exception cref="ArgumentNullException">组合为空。</exception>
    public VisionAcquisitionRuntime(
        VisionAcquisitionProviderComposition composition,
        int machineConfigurationRevision = 0)
    {
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
        _machineConfigurationRevision = machineConfigurationRevision;
    }

    /// <summary>本运行时持有的组合身份；与Workflow Runtime组合身份分别记录。</summary>
    public string CompositionId => _composition.CompositionId;

    /// <summary>运行时整体生命周期状态：Ready表示可接受采集与根运行，Degraded/NotReady表示部分或全部设备未就绪。</summary>
    public EVisionRuntimeState RuntimeState
    {
        get { lock (_gate) return _runtimeState; }
    }

    /// <summary>全部Required源已连接，Runtime可进入可运行状态。</summary>
    public bool IsReady => RuntimeState == EVisionRuntimeState.Ready;

    /// <summary>Required源已连接但存在Optional源失败，Runtime降级运行。</summary>
    public bool IsDegraded => RuntimeState == EVisionRuntimeState.Degraded;

    /// <summary>已发布的逻辑源清单；供属性编辑器和运行准备列出候选。</summary>
    public IReadOnlyList<VisionAcquisitionSourceBinding> Sources => _composition.Sources;

    /// <summary>需要根运行所有权的逻辑源；只有外部回调缓冲源需要。</summary>
    public IReadOnlyList<VisionAcquisitionSourceBinding> BufferedSources =>
        _composition.Sources.Where(binding => binding.AcquisitionMode == EVisionAcquisitionMode.BufferedExternal).ToArray();

    /// <summary>
    /// 启动应用级连接生命周期：按ResourceKey为每个启用Source创建唯一ResourceSession并真正打开设备。
    /// <para>
    /// 同一ResourceKey只创建一个Device Adapter和一个SDK对象（§21.4）；任一失败都不得自动选择其他Provider。
    /// Required源打开失败时Runtime进入NotReady，Optional源失败只降级为Degraded且对应Source保留完整诊断。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>启动后的运行时状态（Ready/Degraded/NotReady）。</returns>
    /// <exception cref="InvalidOperationException">运行时已经启动过。</exception>
    public async ValueTask<EVisionRuntimeState> StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VisionAcquisitionRuntime));
            if (_runtimeState != EVisionRuntimeState.Created)
                throw new InvalidOperationException(
                    $"采集运行时只能启动一次；当前状态 {_runtimeState}。请先停止并释放本实例，再创建新运行时。");
            _runtimeState = EVisionRuntimeState.Starting;
        }

        var sources = _composition.Sources;
        var handledKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var binding in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 同一物理资源只打开一次；多个Source有意映射同一ResourceKey时必须共享同一设备。
                if (!handledKeys.Add(binding.ResourceKey))
                    continue;
                await StartResourceAsync(binding, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (_runtimeState == EVisionRuntimeState.Starting)
                    _runtimeState = EVisionRuntimeState.Created;
            }

            throw;
        }

        lock (_gate)
        {
            _runtimeState = ComputeStartState(sources);
            return _runtimeState;
        }
    }

    /// <summary>
    /// 按关闭顺序停止运行时：关闭接受门、拒绝新采集与根运行、停流并清退未领取帧、
    /// 等待在途操作退出、释放唯一SDK设备对象、释放Provider。重复调用无副作用。
    /// </summary>
    public async ValueTask StopAsync()
    {
        VisionResourceSession[] sessions;
        IVisionAcquisitionProvider[] providers;
        lock (_gate)
        {
            if (_runtimeState == EVisionRuntimeState.Stopped)
                return;
            _runtimeState = EVisionRuntimeState.Stopped;
            _activeLease = null;
            sessions = _resources.Values.ToArray();
            providers = _providers.Values.ToArray();
            _resources.Clear();
            _providers.Clear();
        }

        // 每个会话先关接受门、再停流（等待已进入的回调退出）、再等在途操作退出，最后释放设备。
        foreach (var session in sessions)
            await session.DisposeAsync().ConfigureAwait(false);
        foreach (var provider in providers)
            await provider.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<VisionCapturedImage> CaptureAsync(
        VisionSourceReference source,
        VisionCaptureRequest request,
        VisionAcquisitionOwner owner,
        CancellationToken cancellationToken)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (owner is null)
            throw new ArgumentNullException(nameof(owner));
        if (_disposed)
            throw new ObjectDisposedException(nameof(VisionAcquisitionRuntime));

        if (!_composition.TryGetSource(source.SourceId, out var binding) || binding is null)
            throw new VisionSourceConfigurationException(
                $"逻辑源 {source.SourceId} 未在已发布的机器配置中绑定；Provider失败时不会自动尝试其他Provider。");
        if (!_composition.TryGetProvider(binding.ProviderId, out var registration) || registration is null)
            throw new VisionSourceConfigurationException(
                $"逻辑源 {source.SourceId} 绑定的Provider {binding.ProviderId} 不在当前组合中。");

        if (RuntimeState == EVisionRuntimeState.Created)
            throw new VisionDeviceOfflineException(
                $"逻辑源 {source.SourceId} 所在采集运行时尚未启动；宿主必须在进入可运行状态前调用 StartAsync。");

        var session = GetSession(binding);
        if (!session.TryEnterOperation())
            throw new VisionDeviceOfflineException(
                $"逻辑源 {source.SourceId} 所在的采集运行时正在停止，不再接受新的采集请求。");

        try
        {
            return binding.AcquisitionMode == EVisionAcquisitionMode.BufferedExternal
                ? await ClaimBufferedAsync(session, binding, request, cancellationToken).ConfigureAwait(false)
                : await CaptureOnDemandAsync(session, binding, request, owner, cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            session.ExitOperation();
        }
    }

    /// <inheritdoc/>
    /// <exception cref="VisionResourceConflictException">已有根运行持有采集所有权。</exception>
    public ValueTask<IVisionAcquisitionRunLease> BeginRunAsync(string runId, CancellationToken cancellationToken) =>
        BeginRunAsync(runId, workflowCompositionId: null, cancellationToken);

    /// <summary>
    /// 启动一根根运行，并把工作流侧组合身份一并登记进运行制品。
    /// <para>
    /// 独立重载而不是改接口签名：<see cref="IVisionAcquisitionRunOwner"/> 是所有宿主都要实现的契约，
    /// 制品信息只有需要归档的宿主才提供，缺省为空不影响采集行为。
    /// </para>
    /// </summary>
    /// <param name="runId">根运行身份。</param>
    /// <param name="workflowCompositionId">工作流侧组合身份；为空表示宿主不提供。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>根运行租约；同时实现 <see cref="IVisionAcquisitionRunArtifactSource"/>。</returns>
    /// <exception cref="ArgumentException">根运行身份为空。</exception>
    /// <exception cref="VisionDeviceOfflineException">运行时尚未启动。</exception>
    /// <exception cref="VisionResourceConflictException">已有根运行持有采集所有权。</exception>
    public async ValueTask<IVisionAcquisitionRunLease> BeginRunAsync(
        string runId,
        string? workflowCompositionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("根运行身份不能为空。", nameof(runId));
        if (_disposed)
            throw new ObjectDisposedException(nameof(VisionAcquisitionRuntime));
        if (RuntimeState == EVisionRuntimeState.Created)
            throw new VisionDeviceOfflineException(
                "采集运行时尚未启动；宿主必须在进入可运行状态前调用 StartAsync。");

        RunLease lease;
        lock (_gate)
        {
            if (_activeLease is not null)
                throw BuildOwnershipConflict(_activeLease, runId.Trim());
            _epoch++;
            lease = new RunLease(
                this,
                runId.Trim(),
                _epoch,
                workflowCompositionId,
                _machineConfigurationRevision);
            _activeLease = lease;
        }

        try
        {
            await lease.ArmAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeLease, lease))
                    _activeLease = null;
            }

            throw;
        }
    }

    /// <summary>读取一个逻辑源的运行诊断快照。</summary>
    /// <param name="sourceId">逻辑源标识。</param>
    /// <returns>诊断快照；源未发布时为空。</returns>
    public VisionSourceDiagnostics? GetDiagnostics(string sourceId)
    {
        if (!_composition.TryGetSource(sourceId, out var binding) || binding is null)
            return null;

        var diagnostics = TryGetSession(binding.ResourceKey, out var session)
            ? session.Snapshot(binding.SourceId, binding.ProviderId)
            : new VisionSourceDiagnostics(
                binding.SourceId, binding.ResourceKey, binding.ProviderId,
                "Created", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null,
                EVisionConnectionState.Created, null);

        // 来源身份只能由组合回答（会话只认识资源键），因此在返回前补齐；
        // 现场只有拿到 "哪个Type哪个插件版本" 才能判断"是配置错了还是插件旧了"。
        var acquisitionTypeId = _composition.TryGetSourceEntry(binding.SourceId, out var info) && info is not null
            ? info.AcquisitionTypeId
            : null;
        string? pluginVersion = null;
        if (_composition.TryGetProvider(binding.ProviderId, out var registration) && registration is not null)
            pluginVersion = registration.Version;

        return diagnostics with
        {
            AcquisitionTypeId = acquisitionTypeId,
            PluginId = binding.ProviderId,
            PluginVersion = pluginVersion,
            TransferState = diagnostics.TransferState ?? "NotStarted",
        };
    }

    /// <summary>读取全部已发布源的运行诊断快照。</summary>
    /// <returns>按SourceId排序的快照；从未被使用过的源也会出现。</returns>
    public IReadOnlyList<VisionSourceDiagnostics> GetDiagnostics() =>
        _composition.Sources
            .Select(binding => GetDiagnostics(binding.SourceId))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private async ValueTask<VisionCapturedImage> ClaimBufferedAsync(
        VisionResourceSession session,
        VisionAcquisitionSourceBinding binding,
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        // 节点级曝光/增益会改动正在出图的设备参数，使已在途的帧参数不一致，因此明确拒绝而不是静默应用。
        if (request.ExposureMicroseconds is not null || request.GainDecibels is not null)
            throw new VisionParameterNotSupportedException(
                $"外部回调缓冲源 {binding.SourceId} 不支持节点级曝光/增益覆盖；"
                + "这些参数由机器 Source/Profile 固定，请在机器配置里修改并重新发布。");

        var entry = await session.ClaimAsync(request.Timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            // 领取即记账：制品要能回答"这根运行经手了哪些帧"，而不是只给一个总数。
            RecordClaim(binding.SourceId, entry);

            var frame = new ImageFrame(entry.CaptureId, entry.Frame.Image);
            var metadata = new VisionCaptureMetadata(
                entry.CaptureId,
                binding.SourceId,
                binding.ProviderId,
                binding.ResourceKey,
                entry.CapturedAtUtc,
                entry.DeviceSequence,
                EVisionAcquisitionMode.BufferedExternal,
                entry.ReceivedSequence,
                entry.ReceivedAtUtc);
            return new VisionCapturedImage(frame, metadata);
        }
        finally
        {
            // 像素所有权先转给ImageFrame，再释放Provider句柄：设备/SDK对象释放后图像仍必须可读。
            entry.Frame.Dispose();
        }
    }

    private async ValueTask<VisionCapturedImage> CaptureOnDemandAsync(
        VisionResourceSession session,
        VisionAcquisitionSourceBinding binding,
        VisionCaptureRequest request,
        VisionAcquisitionOwner owner,
        CancellationToken cancellationToken)
    {
        await AcquireOperationAsync(session, binding, owner, request.Timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Faulted)
                throw new VisionDeviceOfflineException(
                    $"资源键 {binding.ResourceKey} 的设备已标记为故障（{session.FaultMessage}）；请先排除故障再继续采集。");

            // 节点不得隐式打开或重连设备：设备连接属于软件生命周期，只能由Runtime Start打开、由Runtime Stop关闭。
            var device = session.Device;
            if (device is null)
                throw new VisionDeviceOfflineException(
                    $"逻辑源 {binding.SourceId} 的设备未连接（资源键 {binding.ResourceKey}）；"
                    + "节点不得隐式打开或重连设备，请确认采集运行时已启动且该Source打开成功。");

            var providerFrame = await CaptureFromDeviceAsync(session, binding, device, request, cancellationToken).ConfigureAwait(false);

            // 主动单次采集没有回调边界，像素落地观测由这里交给会话，与缓冲源共用同一份累计值。
            if (providerFrame.TransferObservation is not null)
                session.RecordTransfer(providerFrame.TransferObservation);

            var captureId = Guid.NewGuid().ToString("N");
            try
            {
                var frame = new ImageFrame(captureId, providerFrame.Image);
                var metadata = new VisionCaptureMetadata(
                    captureId,
                    binding.SourceId,
                    binding.ProviderId,
                    binding.ResourceKey,
                    providerFrame.CapturedAtUtc,
                    providerFrame.DeviceSequence);
                return new VisionCapturedImage(frame, metadata);
            }
            finally
            {
                providerFrame.Dispose();
            }
        }
        finally
        {
            ReleaseOperation(session);
        }
    }

    private static async ValueTask<VisionProviderFrame> CaptureFromDeviceAsync(
        VisionResourceSession session,
        VisionAcquisitionSourceBinding binding,
        IVisionAcquisitionDevice device,
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await device.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VisionAcquisitionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 原生SDK故障后设备状态不可信：标记Faulted并阻断后续使用，不伪装成普通业务失败。
            session.MarkFaulted("DeviceFailure", exception.Message);
            throw new VisionDeviceOfflineException(
                $"逻辑源 {binding.SourceId} 采集失败（资源键 {binding.ResourceKey}）：{exception.Message}", exception);
        }
    }

    private static async ValueTask AcquireOperationAsync(
        VisionResourceSession session,
        VisionAcquisitionSourceBinding binding,
        VisionAcquisitionOwner owner,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (binding.SharingPolicy == EVisionSourceSharingPolicy.ExclusiveRun)
            throw new VisionSourceConfigurationException(
                $"共享策略 ExclusiveRun 只用于外部回调缓冲源；主动单次采集源 {binding.SourceId} 不能声明它。");
        if (binding.SharingPolicy == EVisionSourceSharingPolicy.Broadcast)
            throw new VisionSourceConfigurationException(
                "共享策略 Broadcast 需要真实连续流需求，尚未实现。");

        if (binding.SharingPolicy == EVisionSourceSharingPolicy.ExclusiveOperation)
        {
            if (!session.OperationGate.Wait(0))
                throw new VisionResourceConflictException(
                    BuildConflict(session, binding, owner, "同一资源键已有持有者；ExclusiveOperation 不排队。"));
            session.SetHolder(owner);
            return;
        }

        if (!await session.OperationGate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            throw new VisionResourceConflictException(
                BuildConflict(session, binding, owner, $"Serialized 等待超过 {timeout}，不做无限排队。"));
        session.SetHolder(owner);
    }

    private static void ReleaseOperation(VisionResourceSession session)
    {
        session.SetHolder(null);
        session.OperationGate.Release();
    }

    /// <summary>
    /// 打开会话设备；若会话已持有设备则直接复用。设备连接属于软件生命周期，只由Runtime Start打开。
    /// 采集节点与根运行布防都不得隐式打开设备。
    /// </summary>
    private async ValueTask<IVisionAcquisitionDevice> GetOrOpenDeviceAsync(
        VisionResourceSession session,
        VisionAcquisitionSourceBinding binding,
        VisionAcquisitionProviderRegistration registration,
        CancellationToken cancellationToken)
    {
        var existing = session.Device;
        if (existing is not null)
            return existing;

        // 设备Session按ResourceKey唯一建立并复用，Open/Close状态转换由Runtime串行化。
        await session.OpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = session.Device;
            if (existing is not null)
                return existing;

            var device = await OpenDeviceAsync(session, binding, registration, cancellationToken).ConfigureAwait(false);
            session.SetDevice(device);
            return device;
        }
        finally
        {
            session.OpenGate.Release();
        }
    }

    private async ValueTask<IVisionAcquisitionDevice> OpenDeviceAsync(
        VisionResourceSession session,
        VisionAcquisitionSourceBinding binding,
        VisionAcquisitionProviderRegistration registration,
        CancellationToken cancellationToken)
    {
        var provider = GetProvider(registration);
        // deviceSettings 解析出的私有绑定随Source绑定一路带到这里；公共层只转交不解释。
        var device = await provider
            .OpenAsync(
                new VisionAcquisitionProviderBinding(binding.ProviderBindingId, binding.ProviderState),
                cancellationToken)
            .ConfigureAwait(false);
        var identity = device.Identity;
        if (identity.HasCanonicalKey
            && !string.Equals(identity.CanonicalKey, binding.ResourceKey, StringComparison.Ordinal))
        {
            await device.DisposeAsync().ConfigureAwait(false);
            throw new VisionSourceConfigurationException(
                $"设备报告的规范身份 {identity.CanonicalKey} 与配置资源键 {binding.ResourceKey} 不一致；"
                + "拒绝继续，避免同一物理设备形成两个互不相知的锁域。");
        }

        return device;
    }

    /// <summary>按一个ResourceKey的代表Source打开设备并记录连接状态；打开失败标记故障但不自动切换Provider。</summary>
    private async ValueTask StartResourceAsync(
        VisionAcquisitionSourceBinding binding,
        CancellationToken cancellationToken)
    {
        var session = GetSession(binding);
        if (session.Device is not null)
            return;
        if (!_composition.TryGetProvider(binding.ProviderId, out var registration) || registration is null)
            throw new VisionSourceConfigurationException(
                $"逻辑源 {binding.SourceId} 绑定的Provider {binding.ProviderId} 不在当前组合中。");

        session.MarkConnecting($"正在打开资源键 {binding.ResourceKey} 的设备…");
        try
        {
            var device = await GetOrOpenDeviceAsync(session, binding, registration, cancellationToken).ConfigureAwait(false);
            session.MarkConnected(BuildConnectionDiagnostic(binding, device));
            // V2-4：OnConnect 接收流由 Runtime Start 布防一次并跨根运行保持；
            // 之后 BeginEpoch/EndEpoch 只开放与收口采集代次，不再驱动设备打开或停流。
            if (binding.AcquisitionMode == EVisionAcquisitionMode.BufferedExternal)
                await session.StartStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            var kind = exception is VisionSourceConfigurationException ? "SourceConfiguration" : "OpenFailure";
            session.MarkFaulted(kind, $"逻辑源 {binding.SourceId}（资源键 {binding.ResourceKey}）打开设备或启动接收流失败：{exception.Message}");
        }
    }

    /// <summary>按全部Source的打开结果推导启动状态：Required失败→NotReady；否则Optional失败→Degraded。</summary>
    private EVisionRuntimeState ComputeStartState(IReadOnlyList<VisionAcquisitionSourceBinding> sources)
    {
        var requiredFailed = false;
        var optionalFailed = false;
        foreach (var binding in sources)
        {
            if (SourceIsConnected(binding))
                continue;
            if (binding.IsRequired)
                requiredFailed = true;
            else
                optionalFailed = true;
        }

        if (requiredFailed)
            return EVisionRuntimeState.NotReady;
        return optionalFailed ? EVisionRuntimeState.Degraded : EVisionRuntimeState.Ready;
    }

    private bool SourceIsConnected(VisionAcquisitionSourceBinding binding) =>
        TryGetSession(binding.ResourceKey, out var session)
        && session.Device is not null
        && !session.Faulted;

    private static string BuildConnectionDiagnostic(
        VisionAcquisitionSourceBinding binding,
        IVisionAcquisitionDevice device)
    {
        var identity = device.Identity;
        var canonical = identity.HasCanonicalKey ? identity.CanonicalKey : "(设备未报告规范资源键)";
        return $"资源键 {binding.ResourceKey} 的设备已连接；设备报告规范身份 {canonical}。";
    }

    private IVisionAcquisitionProvider GetProvider(VisionAcquisitionProviderRegistration registration)
    {
        lock (_gate)
        {
            if (_providers.TryGetValue(registration.ProviderId, out var existing))
                return existing;
            var created = registration.Factory();
            if (created is null)
                throw new VisionProviderUnavailableException(registration.ProviderId, "Provider工厂返回空实例。");
            _providers.Add(registration.ProviderId, created);
            return created;
        }
    }

    private VisionResourceSession GetSession(VisionAcquisitionSourceBinding binding)
    {
        lock (_gate)
        {
            if (_resources.TryGetValue(binding.ResourceKey, out var existing))
                return existing;
            var created = new VisionResourceSession(binding.ResourceKey, binding.InboxPolicy);
            _resources.Add(binding.ResourceKey, created);
            return created;
        }
    }

    private bool TryGetSession(string resourceKey, out VisionResourceSession session)
    {
        lock (_gate)
            return _resources.TryGetValue(resourceKey, out session!);
    }

    /// <summary>把一次成功领取登记到当前根运行的制品；没有活动根运行时不记录。</summary>
    /// <param name="sourceId">领取来源的逻辑源标识。</param>
    /// <param name="entry">领取到的帧条目。</param>
    private void RecordClaim(string sourceId, VisionFrameInboxEntry entry)
    {
        RunLease? lease;
        lock (_gate)
            lease = _activeLease;

        lease?.RecordClaim(sourceId, entry);
    }

    /// <summary>
    /// 配置摘要脱敏：序列号、用户自定义名、设备名是能追到具体硬件的标识，
    /// 制品要归档到别处，因此遮蔽这些值；其余字段照原样保留，否则制品就无法用于比对配置差异。
    /// <para>
    /// 只作用于制品：CompositionId 与诊断文本仍使用原摘要，脱敏不得改变组合身份。
    /// </para>
    /// </summary>
    private static string? MaskConfigurationSummary(string? summary)
    {
        if (summary is null || summary.Trim().Length == 0)
            return summary;

        var entries = summary.Split(';');
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = entry.Substring(0, separator).Trim();
            if (!IsDeviceIdentityKey(key))
                continue;

            entries[index] = key + "=" + MaskValue(entry.Substring(separator + 1));
        }

        return string.Join(";", entries);
    }

    /// <summary>该摘要键的值是否属于设备身份标识。</summary>
    private static bool IsDeviceIdentityKey(string key) =>
        string.Equals(key, "serialNumber", StringComparison.Ordinal)
        || string.Equals(key, "userDefinedName", StringComparison.Ordinal)
        || string.Equals(key, "deviceName", StringComparison.Ordinal);

    /// <summary>遮蔽值：保留首尾各两个字符以便人工比对，中间一律以星号代替。</summary>
    private static string MaskValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= 4)
            return "***";
        return trimmed.Substring(0, 2) + "***" + trimmed.Substring(trimmed.Length - 2);
    }

    private VisionResourceConflictException BuildOwnershipConflict(RunLease holder, string requestedRunId)
    {
        var buffered = BufferedSources.FirstOrDefault();
        return new VisionResourceConflictException(new VisionResourceConflictDiagnostics(
            buffered?.SourceId ?? "(无外部回调源)",
            buffered?.ResourceKey ?? "(无外部回调源)",
            requestedRunId,
            requestedRunId,
            holder.RunId,
            holder.RunId,
            EVisionSourceSharingPolicy.ExclusiveRun,
            "外部回调缓冲源按根运行独占；同一采集运行时同时只能有一根根运行持有所有权。"));
    }

    private static VisionResourceConflictDiagnostics BuildConflict(
        VisionResourceSession session,
        VisionAcquisitionSourceBinding binding,
        VisionAcquisitionOwner owner,
        string reason)
    {
        var holder = session.Holder;
        return new VisionResourceConflictDiagnostics(
            binding.SourceId,
            binding.ResourceKey,
            owner.OwnerId,
            owner.OperationId,
            holder?.OwnerId,
            holder?.OperationId,
            binding.SharingPolicy,
            reason);
    }

    /// <summary>
    /// 一根根运行的采集所有权租约；同时是运行制品的来源。
    /// <para>
    /// 制品能力是额外实现的能力接口，不在 <see cref="IVisionAcquisitionRunLease"/> 上：
    /// 工作流侧只依赖租约契约，不需要为归档能力买单。
    /// </para>
    /// </summary>
    private sealed class RunLease : IVisionAcquisitionRunLease, IVisionAcquisitionRunArtifactSource
    {
        private readonly VisionAcquisitionRuntime _runtime;
        private readonly List<string> _armed = new List<string>();
        private readonly List<ClaimRecord> _claims = new List<ClaimRecord>();
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
        private long _unclaimedAtEpochEnd;
        private DateTimeOffset? _completedAtUtc;
        private int _disposed;

        public RunLease(
            VisionAcquisitionRuntime runtime,
            string runId,
            int epoch,
            string? workflowCompositionId,
            int machineConfigurationRevision)
        {
            _runtime = runtime;
            RunId = runId;
            Epoch = epoch;
            WorkflowCompositionId = workflowCompositionId;
            MachineConfigurationRevision = machineConfigurationRevision;
        }

        public string RunId { get; }

        public int Epoch { get; }

        /// <summary>工作流侧组合身份；宿主未提供时为空。</summary>
        public string? WorkflowCompositionId { get; }

        /// <summary>生成本组合的机器配置修订号；未接入修订存储时为 0。</summary>
        public int MachineConfigurationRevision { get; }

        public IReadOnlyList<string> ArmedSourceIds
        {
            get
            {
                lock (_armed)
                    return _armed.ToArray();
            }
        }

        /// <summary>开放本轮采集代次并登记布防的外部回调缓冲源；接收流已由 Runtime Start 布防，这里不再驱动设备。</summary>
        public ValueTask ArmAsync(CancellationToken cancellationToken)
        {
            foreach (var binding in _runtime.BufferedSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = _runtime.GetSession(binding);

                // 只开放本代次的接收与领取；设备与接收流由 Runtime Start 持有并跨根运行保持。
                session.BeginEpoch(Epoch);

                lock (_armed)
                    _armed.Add(binding.SourceId);
            }

            return default;
        }

        /// <summary>登记一次成功领取；由Runtime在领取返回后调用。</summary>
        /// <param name="sourceId">领取来源的逻辑源标识。</param>
        /// <param name="entry">领取到的帧条目。</param>
        public void RecordClaim(string sourceId, VisionFrameInboxEntry entry)
        {
            lock (_claims)
            {
                _claims.Add(new ClaimRecord(
                    sourceId,
                    new VisionAcquisitionRunFrameArtifact(
                        entry.CaptureId,
                        entry.ReceivedSequence,
                        entry.DeviceSequence,
                        entry.CapturedAtUtc)));
            }
        }

        /// <summary>退役本轮：收口采集代次并释放本代次未领取帧，然后归还所有权。接收流保持运行。</summary>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return default;

            foreach (var binding in _runtime.BufferedSources)
            {
                if (!_runtime.TryGetSession(binding.ResourceKey, out var session))
                    continue;

                // 收口返回值就是"本轮丢了多少帧"的唯一权威来源：运行期间不再另行计数，
                // 否则两处统计迟早会对不上，而制品必须与真实释放行为一致。
                var unclaimed = session.EndEpoch();
                lock (_claims)
                    _unclaimedAtEpochEnd += unclaimed;
            }

            lock (_runtime._gate)
            {
                if (ReferenceEquals(_runtime._activeLease, this))
                    _runtime._activeLease = null;
            }

            _completedAtUtc = DateTimeOffset.UtcNow;
            return default;
        }

        /// <inheritdoc/>
        public bool TryGetArtifact(out VisionAcquisitionRunArtifact? artifact)
        {
            var sources = new List<VisionAcquisitionRunSourceArtifact>();
            foreach (var binding in _runtime.BufferedSources)
            {
                var diagnostics = _runtime.GetDiagnostics(binding.SourceId);
                if (diagnostics is null)
                    continue;

                List<VisionAcquisitionRunFrameArtifact> frames;
                long claimed;
                lock (_claims)
                {
                    frames = _claims
                        .Where(item => string.Equals(item.SourceId, binding.SourceId, StringComparison.Ordinal))
                        .Select(item => item.Frame)
                        .ToList();
                    claimed = frames.Count;
                }

                sources.Add(new VisionAcquisitionRunSourceArtifact(
                    binding.SourceId,
                    diagnostics.AcquisitionTypeId,
                    binding.ProviderId,
                    diagnostics.PluginVersion,
                    binding.ResourceKey,
                    MaskConfigurationSummary(_runtime._composition.GetConfigurationSummary(binding.SourceId)),
                    claimed,
                    diagnostics.FramesReceived,
                    diagnostics.FramesExpired,
                    diagnostics.FramesRejectedWithoutEpoch,
                    diagnostics.FramesRejectedOverflow,
                    diagnostics.UnclaimedAtEpochEnd,
                    diagnostics.InboxHighWatermark,
                    diagnostics.BytesHighWatermark,
                    diagnostics.DeviceSequenceGaps,
                    diagnostics.FaultKind,
                    diagnostics.FaultMessage,
                    diagnostics.ConnectionState,
                    diagnostics.TransferState,
                    frames));
            }

            long unclaimedAtEpochEnd;
            lock (_claims)
                unclaimedAtEpochEnd = _unclaimedAtEpochEnd;

            artifact = new VisionAcquisitionRunArtifact(
                RunId,
                WorkflowCompositionId,
                _runtime.CompositionId,
                MachineConfigurationRevision,
                Epoch,
                _startedAtUtc,
                _completedAtUtc ?? DateTimeOffset.UtcNow,
                _runtime._composition.ProviderManifest,
                sources,
                unclaimedAtEpochEnd);
            return true;
        }
    }

    /// <summary>一次领取的记账条目；把帧制品与其来源绑定在一起，制品按源分组时不必再反查。</summary>
    private sealed record ClaimRecord(string SourceId, VisionAcquisitionRunFrameArtifact Frame);
}
