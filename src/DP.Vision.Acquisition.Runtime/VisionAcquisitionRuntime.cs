using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition;

/// <summary>站点级采集运行时；拥有Provider实例、设备Session、互斥与路由，Provider拥有硬件，运行拥有图像。</summary>
public sealed class VisionAcquisitionRuntime : IVisionAcquisition, IAsyncDisposable
{
    private readonly VisionAcquisitionProviderComposition _composition;
    private readonly Dictionary<string, IVisionAcquisitionProvider> _providers =
        new Dictionary<string, IVisionAcquisitionProvider>(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceEntry> _resources =
        new Dictionary<string, ResourceEntry>(StringComparer.Ordinal);
    private readonly object _gate = new object();
    private bool _disposed;

    /// <summary>创建运行时。</summary>
    /// <param name="composition">已发布的不可变Provider组合。</param>
    /// <exception cref="ArgumentNullException">组合为空。</exception>
    public VisionAcquisitionRuntime(VisionAcquisitionProviderComposition composition)
    {
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
    }

    /// <summary>本运行时持有的组合身份；与Workflow Runtime组合身份分别记录。</summary>
    public string CompositionId => _composition.CompositionId;

    /// <summary>已发布的逻辑源清单；供属性编辑器和运行准备列出候选。</summary>
    public IReadOnlyList<VisionAcquisitionSourceBinding> Sources => _composition.Sources;

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

        var resource = GetResource(binding.ResourceKey);
        await AcquireAsync(resource, binding, owner, request.Timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            var device = await GetOrOpenDeviceAsync(resource, binding, registration, cancellationToken).ConfigureAwait(false);
            var providerFrame = await CaptureFromDeviceAsync(resource, binding, device, request, cancellationToken).ConfigureAwait(false);

            // 像素所有权先转给ImageFrame，再释放Provider句柄：设备/SDK对象释放后图像仍必须可读。
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
            Release(resource);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        IVisionAcquisitionDevice[] devices;
        IVisionAcquisitionProvider[] providers;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            devices = _resources.Values.Select(entry => entry.Device).OfType<IVisionAcquisitionDevice>().ToArray();
            providers = _providers.Values.ToArray();
            _resources.Clear();
            _providers.Clear();
        }

        foreach (var device in devices)
            await device.DisposeAsync().ConfigureAwait(false);
        foreach (var provider in providers)
            await provider.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask<VisionProviderFrame> CaptureFromDeviceAsync(
        ResourceEntry resource,
        VisionAcquisitionSourceBinding binding,
        IVisionAcquisitionDevice device,
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (resource.Faulted)
            throw new VisionDeviceOfflineException(
                $"资源键 {binding.ResourceKey} 的设备已标记为故障（{resource.FaultMessage}）；请先排除故障再继续采集。");

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
            resource.MarkFaulted(exception.Message);
            throw new VisionDeviceOfflineException(
                $"逻辑源 {binding.SourceId} 采集失败（资源键 {binding.ResourceKey}）：{exception.Message}", exception);
        }
    }

    private async ValueTask AcquireAsync(
        ResourceEntry resource,
        VisionAcquisitionSourceBinding binding,
        VisionAcquisitionOwner owner,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (binding.SharingPolicy == EVisionSourceSharingPolicy.ExclusiveRun
            || binding.SharingPolicy == EVisionSourceSharingPolicy.Broadcast)
            throw new VisionSourceConfigurationException(
                $"共享策略 {binding.SharingPolicy} 尚未实现；ExclusiveRun 依赖运行作用域所有权，Broadcast 需要真实连续流需求。");

        if (binding.SharingPolicy == EVisionSourceSharingPolicy.ExclusiveOperation)
        {
            if (!resource.Gate.Wait(0))
                throw new VisionResourceConflictException(
                    BuildConflict(resource, binding, owner, "同一资源键已有持有者；ExclusiveOperation 不排队。"));
            resource.SetHolder(owner);
            return;
        }

        if (!await resource.Gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            throw new VisionResourceConflictException(
                BuildConflict(resource, binding, owner, $"Serialized 等待超过 {timeout}，不做无限排队。"));
        resource.SetHolder(owner);
    }

    private static void Release(ResourceEntry resource)
    {
        resource.SetHolder(null);
        resource.Gate.Release();
    }

    private async ValueTask<IVisionAcquisitionDevice> GetOrOpenDeviceAsync(
        ResourceEntry resource,
        VisionAcquisitionSourceBinding binding,
        VisionAcquisitionProviderRegistration registration,
        CancellationToken cancellationToken)
    {
        var existing = resource.Device;
        if (existing is not null)
            return existing;

        // 设备Session按ResourceKey惰性打开并复用，Open/Close状态转换由Runtime串行化。
        await resource.OpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = resource.Device;
            if (existing is not null)
                return existing;

            var provider = GetProvider(registration);
            var device = await provider.OpenAsync(binding.ProviderBindingId, cancellationToken).ConfigureAwait(false);
            var identity = device.Identity;
            if (identity.HasCanonicalKey
                && !string.Equals(identity.CanonicalKey, binding.ResourceKey, StringComparison.Ordinal))
            {
                await device.DisposeAsync().ConfigureAwait(false);
                throw new VisionSourceConfigurationException(
                    $"设备报告的规范身份 {identity.CanonicalKey} 与配置资源键 {binding.ResourceKey} 不一致；"
                    + "拒绝继续，避免同一物理设备形成两个互不相知的锁域。");
            }

            resource.Device = device;
            return device;
        }
        finally
        {
            resource.OpenGate.Release();
        }
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

    private ResourceEntry GetResource(string resourceKey)
    {
        lock (_gate)
        {
            if (_resources.TryGetValue(resourceKey, out var existing))
                return existing;
            var entry = new ResourceEntry();
            _resources.Add(resourceKey, entry);
            return entry;
        }
    }

    private static VisionResourceConflictDiagnostics BuildConflict(
        ResourceEntry resource,
        VisionAcquisitionSourceBinding binding,
        VisionAcquisitionOwner owner,
        string reason)
    {
        var holder = resource.Holder;
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

    private sealed class ResourceEntry
    {
        private readonly object _sync = new object();
        private VisionAcquisitionOwner? _holder;
        private IVisionAcquisitionDevice? _device;
        private bool _faulted;
        private string? _faultMessage;

        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        public SemaphoreSlim OpenGate { get; } = new SemaphoreSlim(1, 1);

        public VisionAcquisitionOwner? Holder
        {
            get { lock (_sync) return _holder; }
        }

        public IVisionAcquisitionDevice? Device
        {
            get { lock (_sync) return _device; }
            set { lock (_sync) _device = value; }
        }

        public bool Faulted
        {
            get { lock (_sync) return _faulted; }
        }

        public string? FaultMessage
        {
            get { lock (_sync) return _faultMessage; }
        }

        public void SetHolder(VisionAcquisitionOwner? owner)
        {
            lock (_sync)
                _holder = owner;
        }

        public void MarkFaulted(string message)
        {
            lock (_sync)
            {
                _faulted = true;
                _faultMessage = message;
            }
        }
    }
}
