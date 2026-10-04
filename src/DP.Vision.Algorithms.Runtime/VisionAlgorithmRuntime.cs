using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Algorithms;

/// <summary>一个节点算法槽位的准备请求；键应包含计划路径、节点和槽位。</summary>
public sealed class VisionAlgorithmRequest
{
    /// <summary>创建准备请求。</summary>
    public VisionAlgorithmRequest(string bindingKey, Type contractType, VisionAlgorithmSelection selection, IEnumerable<string>? requiredFeatures = null)
    {
        if (string.IsNullOrWhiteSpace(bindingKey)) throw new ArgumentException("绑定键不能为空。", nameof(bindingKey));
        BindingKey = bindingKey; ContractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        RequiredFeatures = Array.AsReadOnly((requiredFeatures ?? Array.Empty<string>()).ToArray());
    }
    /// <summary>计划位置和槽位身份。</summary>
    public string BindingKey { get; }
    /// <summary>所需接口。</summary>
    public Type ContractType { get; }
    /// <summary>准备时冻结的选择。</summary>
    public VisionAlgorithmSelection Selection { get; }
    /// <summary>所需附加特征。</summary>
    public IReadOnlyList<string> RequiredFeatures { get; }
}

/// <summary>共享资源准备与租约管理；不拥有 Workflow 节点、厂商或业务判定。</summary>
public sealed class VisionAlgorithmRuntime : IDisposable
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, ResourceEntry> _resources = new Dictionary<string, ResourceEntry>(StringComparer.Ordinal);
    private readonly VisionAlgorithmCatalog _catalog;
    private readonly VisionAlgorithmResourceContext? _resourceContext;
    private bool _disposed;
    private long _sequence;
    private readonly List<Exception> _releaseFailures = new List<Exception>();

    /// <summary>资源销毁或创建取消回调的失败快照；不阻断其他资源回滚，宿主应报告。</summary>
    public IReadOnlyList<Exception> ReleaseFailures { get { lock (_gate) return _releaseFailures.ToArray(); } }

    /// <summary>使用冻结目录创建算法运行时。</summary>
    public VisionAlgorithmRuntime(VisionAlgorithmCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    /// <summary>为未显式传入上下文的读取器配置资源目录。</summary>
    public VisionAlgorithmRuntime(VisionAlgorithmCatalog catalog, VisionAlgorithmResourceContext resourceContext) : this(catalog)
    { _resourceContext = resourceContext ?? throw new ArgumentNullException(nameof(resourceContext)); }

    /// <summary>准备完整候选计划；任何失败或取消都会归还本次已申请的租约。</summary>
    public Task<VisionAlgorithmPlan> PrepareAsync(IEnumerable<VisionAlgorithmRequest> requests, CancellationToken cancellationToken = default)
        => PrepareAsync(requests, _resourceContext, cancellationToken);

    /// <summary>使用本次配方的资源基础目录准备；不修改配方中的相对路径。</summary>
    public async Task<VisionAlgorithmPlan> PrepareAsync(IEnumerable<VisionAlgorithmRequest> requests, VisionAlgorithmResourceContext? resources, CancellationToken cancellationToken)
    {
        if (requests == null) throw new ArgumentNullException(nameof(requests));
        var snapshots = requests.Select(r => new VisionAlgorithmRequest(r.BindingKey, r.ContractType, Snapshot(r.Selection, new HashSet<VisionAlgorithmSelection>()), r.RequiredFeatures)).ToArray();
        if (snapshots.GroupBy(r => r.BindingKey, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidOperationException("计划存在重复算法绑定键。");
        var inspection = new VisionAlgorithmInspection(_catalog).Analyze(snapshots, resources);
        if (!inspection.Success)
            throw VisionAlgorithmExceptionDiagnostics.Attach(new InvalidOperationException(string.Join("；", inspection.Issues.Select(i =>
                $"算法绑定 {i.BindingKey}（{i.ImplementationId}）：{i.Message}"))), inspection.Issues);
        var leases = new List<ResourceEntry>(); var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        try
        {
            foreach (var request in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var binding = await PrepareBindingAsync(request.ContractType, request.Selection, request.RequiredFeatures,
                        leases, new List<string>(), resources, cancellationToken).ConfigureAwait(false);
                    bindings.Add(request.BindingKey, binding);
                }
                catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
                {
                    var issues = VisionAlgorithmExceptionDiagnostics.Read(error);
                    if (issues.Count == 0) issues = new[] { new VisionAlgorithmIssue("ALG_INITIALIZATION_FAILED", "Preparation", request.BindingKey,
                        request.Selection.ImplementationId, "", error.Message, error.ToString()) };
                    else issues = issues.Select(i => new VisionAlgorithmIssue(i.Code, i.Phase, request.BindingKey, i.ImplementationId, i.DependencyPath, i.Message, i.Detail)).ToArray();
                    throw VisionAlgorithmExceptionDiagnostics.Attach(new InvalidOperationException($"算法绑定 {request.BindingKey}（{request.Selection.ImplementationId}）准备失败：{error.Message}", error), issues);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new VisionAlgorithmPlan(this, bindings, leases);
        }
        catch
        {
            foreach (var entry in leases.AsEnumerable().Reverse()) Release(entry);
            throw;
        }
    }

    private async Task<Binding> PrepareBindingAsync(Type contract, VisionAlgorithmSelection selection, IReadOnlyList<string> features,
        List<ResourceEntry> leases, List<string> path, VisionAlgorithmResourceContext? resources, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(selection.ImplementationId)) throw new InvalidOperationException("算法实现未明确选择。");
        if (path.Contains(selection.ImplementationId)) throw new InvalidOperationException("算法依赖循环：" + string.Join(" → ", path.Concat(new[] { selection.ImplementationId })));
        var descriptor = _catalog.GetRequired(selection.ImplementationId);
        if (!contract.IsAssignableFrom(descriptor.ContractType) && !descriptor.ContractType.IsAssignableFrom(contract)) throw new InvalidOperationException($"{descriptor.ImplementationId} 不提供 {contract.FullName}。");
        foreach (var feature in features)
            if (!descriptor.Features.Contains(feature, StringComparer.Ordinal)) throw new InvalidOperationException($"{descriptor.ImplementationId} 缺少特征 {feature}。");
        path.Add(selection.ImplementationId);
        try
        {
            var configuration = new VisionAlgorithmConfiguration(selection.SettingsVersion, selection.Settings);
            if (resources != null) configuration = resources.Resolve(descriptor, configuration);
            var declared = descriptor.Factory.GetDependencies(configuration);
            if (declared.GroupBy(d => d.Slot, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new InvalidOperationException("工厂重复声明依赖槽位。");
            if (selection.Dependencies.Keys.Any(key => !declared.Any(d => d.Slot == key))) throw new InvalidOperationException("配置包含未声明的依赖槽位。");
            var dependencies = new Dictionary<string, object>(StringComparer.Ordinal); var dependencyBindings = new List<Binding>();
            foreach (var dependency in declared.OrderBy(d => d.Slot, StringComparer.Ordinal))
            {
                if (!selection.Dependencies.TryGetValue(dependency.Slot, out var chosen)) throw new InvalidOperationException($"{descriptor.ImplementationId} 缺少依赖选择 {dependency.Slot}。");
                var binding = await PrepareBindingAsync(dependency.ContractType, chosen, Array.Empty<string>(), leases, path, resources, token).ConfigureAwait(false);
                dependencies.Add(dependency.Slot, (await binding.Entry.Task.ConfigureAwait(false)).Instance);
                dependencyBindings.Add(binding);
            }
            var activation = await descriptor.Factory.PrepareAsync(configuration,
                new ReadOnlyDictionary<string, object>(dependencies), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var identity = Hash(string.Concat(new[] { descriptor.ImplementationId, descriptor.Version, activation.ResourceIdentity }
                .Concat(declared.OrderBy(d => d.Slot, StringComparer.Ordinal).Select(d => d.Slot))
                .Concat(dependencyBindings.Select(b => b.Entry.Key)).Select(part => part.Length + ":" + part)));
            if (activation.Sharing == EVisionAlgorithmSharing.Exclusive) identity += ":" + Guid.NewGuid().ToString("N");
            var entry = Acquire(identity, descriptor.ContractType, activation, dependencyBindings.SelectMany(b => b.Closure).Distinct().ToArray());
            leases.Add(entry);
            var resource = await WaitAsync(entry.Task, token).ConfigureAwait(false);
            if (!contract.IsInstanceOfType(resource.Instance)) throw new InvalidOperationException($"{descriptor.ImplementationId} 未实现所需接口 {contract.FullName}。");
            return new Binding(entry, dependencyBindings.SelectMany(b => b.Closure).Concat(new[] { entry }).Distinct().OrderBy(e => e.Sequence).ToArray(), descriptor);
        }
        catch (Exception error) when (error is not OutOfMemoryException && error is not OperationCanceledException)
        {
            if (VisionAlgorithmExceptionDiagnostics.Read(error).Count == 0)
                VisionAlgorithmExceptionDiagnostics.Attach(error, new[] { new VisionAlgorithmIssue("ALG_INITIALIZATION_FAILED", "Preparation", "",
                    descriptor.ImplementationId, string.Join(" → ", path), error.Message, error.ToString()) });
            throw;
        }
        finally { path.RemoveAt(path.Count - 1); }
    }

    private ResourceEntry Acquire(string key, Type contract, VisionAlgorithmActivation activation, ResourceEntry[] dependencies)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VisionAlgorithmRuntime));
            if (_resources.TryGetValue(key, out var cached))
            {
                if (cached.Sharing != activation.Sharing || cached.ContractType != contract) throw new InvalidOperationException("共享资源身份的接口或并发策略不一致。");
                cached.References++; return cached;
            }
            var entry = new ResourceEntry(key, ++_sequence, contract, activation.Sharing) { References = 1, Dependencies = dependencies };
            // 即使调用者取消准备，尚未结束的工厂创建和销毁仍可使用其依赖。
            foreach (var dependency in dependencies) dependency.References++;
            _resources.Add(key, entry);
            entry.Task = Task.Run(async () =>
            {
                VisionAlgorithmResource? resource = null;
                try
                {
                    resource = await activation.CreateAsync(entry.Creation.Token).ConfigureAwait(false);
                    if (resource == null || !contract.IsInstanceOfType(resource.Instance)) throw new InvalidOperationException("工厂返回的对象不满足声明接口。");
                    return resource;
                }
                catch { resource?.Dispose(); throw; }
            });
            return entry;
        }
    }

    internal void Release(ResourceEntry entry)
    {
        Task<VisionAlgorithmResource>? retire = null;
        lock (_gate)
        {
            if (--entry.References != 0) return;
            if (_resources.TryGetValue(entry.Key, out var existing) && existing == entry) _resources.Remove(entry.Key);
            retire = entry.Task;
        }
        // 取消不能持有缓存锁：用户创建函数的取消回调可能调用资源管理器。
        if (!retire.IsCompleted)
        {
            try { entry.Creation.Cancel(); }
            catch (Exception error) when (error is not OutOfMemoryException) { lock (_gate) _releaseFailures.Add(error); }
        }
        _ = RetireAsync(entry, retire);
    }

    private async Task RetireAsync(ResourceEntry entry, Task<VisionAlgorithmResource> task)
    {
        VisionAlgorithmResource? resource = null;
        try { resource = await task.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 取消最后等待者后，后台创建仍可能失败；此时不能把原始错误藏掉。
            if (entry.Creation.IsCancellationRequested && error is not OperationCanceledException)
                lock (_gate) _releaseFailures.Add(error);
        }
        try { resource?.Dispose(); }
        catch (Exception error) when (error is not OutOfMemoryException) { lock (_gate) _releaseFailures.Add(error); }
        finally
        {
            entry.Creation.Dispose(); entry.Serial.Dispose();
            foreach (var dependency in entry.Dependencies.Reverse()) Release(dependency);
        }
    }

    internal void RetainInvocation(Binding binding)
    {
        lock (_gate)
        {
            if (binding.Closure.Any(entry => entry.References <= 0)) throw new ObjectDisposedException(nameof(VisionAlgorithmPlan));
            foreach (var entry in binding.Closure) entry.References++;
        }
    }

    internal TResult Invoke<T, TResult>(Binding binding, Func<T, TResult> invoke, CancellationToken token) where T : class
    {
        var locked = new List<ResourceEntry>();
        try
        {
            foreach (var entry in binding.Closure.Where(e => e.Sharing == EVisionAlgorithmSharing.SharedSerial))
            { entry.Serial.Wait(token); locked.Add(entry); }
            token.ThrowIfCancellationRequested();
            return invoke((T)binding.Entry.Task.GetAwaiter().GetResult().Instance);
        }
        finally
        {
            foreach (var entry in locked.AsEnumerable().Reverse()) entry.Serial.Release();
            foreach (var entry in binding.Closure.Reverse()) Release(entry);
        }
    }

    internal async Task<TResult> InvokeAsync<T, TResult>(Binding binding, Func<T, CancellationToken, Task<TResult>> invoke, CancellationToken token) where T : class
    {
        var locked = new List<ResourceEntry>();
        try
        {
            foreach (var entry in binding.Closure.Where(e => e.Sharing == EVisionAlgorithmSharing.SharedSerial))
            { await entry.Serial.WaitAsync(token).ConfigureAwait(false); locked.Add(entry); }
            token.ThrowIfCancellationRequested();
            return await invoke((T)(await binding.Entry.Task.ConfigureAwait(false)).Instance, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (var entry in locked.AsEnumerable().Reverse()) entry.Serial.Release();
            foreach (var entry in binding.Closure.Reverse()) Release(entry);
        }
    }

    /// <summary>禁止新准备；已有计划与在途调用保持租约，完成后自然释放。</summary>
    public void Dispose() { lock (_gate) _disposed = true; }

    private static VisionAlgorithmSelection Snapshot(VisionAlgorithmSelection selection, HashSet<VisionAlgorithmSelection> path)
    {
        if (selection == null || !path.Add(selection)) throw new InvalidOperationException("选择配置为空或形成引用循环。");
        try
        {
            if (selection.Settings == null || selection.Dependencies == null) throw new InvalidOperationException("算法设置或依赖配置不能为空。");
            return new VisionAlgorithmSelection { ImplementationId = selection.ImplementationId, SettingsVersion = selection.SettingsVersion,
                Settings = new Dictionary<string, string>(selection.Settings, StringComparer.Ordinal),
                Dependencies = selection.Dependencies.ToDictionary(p => p.Key, p => Snapshot(p.Value, path), StringComparer.Ordinal) };
        }
        finally { path.Remove(selection); }
    }

    private static string Hash(string text)
    { using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }

    private static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken token)
    {
        if (!token.CanBeCanceled) return await task.ConfigureAwait(false);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult(true)))
        { if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) token.ThrowIfCancellationRequested(); }
        token.ThrowIfCancellationRequested();
        return await task.ConfigureAwait(false);
    }

    internal sealed class ResourceEntry
    {
        internal ResourceEntry(string key, long sequence, Type contractType, EVisionAlgorithmSharing sharing)
        { Key = key; Sequence = sequence; ContractType = contractType; Sharing = sharing; }
        internal readonly string Key;
        internal readonly long Sequence;
        internal readonly Type ContractType;
        internal readonly EVisionAlgorithmSharing Sharing;
        internal readonly CancellationTokenSource Creation = new CancellationTokenSource();
        internal readonly SemaphoreSlim Serial = new SemaphoreSlim(1, 1);
        internal Task<VisionAlgorithmResource> Task = null!;
        internal int References;
        internal ResourceEntry[] Dependencies = Array.Empty<ResourceEntry>();
    }

    internal sealed class Binding
    {
        internal Binding(ResourceEntry entry, ResourceEntry[] closure, VisionAlgorithmDescriptor descriptor) { Entry = entry; Closure = closure; Descriptor = descriptor; }
        internal readonly ResourceEntry Entry;
        internal readonly ResourceEntry[] Closure;
        internal readonly VisionAlgorithmDescriptor Descriptor;
    }
}

/// <summary>准备成功的不可变绑定计划；执行通过计划取得调用租约，Dispose 不打断在途调用。</summary>
public sealed class VisionAlgorithmPlan : IDisposable
{
    private readonly object _gate = new object();
    private readonly VisionAlgorithmRuntime _owner;
    private readonly IReadOnlyDictionary<string, VisionAlgorithmRuntime.Binding> _bindings;
    private List<VisionAlgorithmRuntime.ResourceEntry>? _leases;
    internal VisionAlgorithmPlan(VisionAlgorithmRuntime owner, IReadOnlyDictionary<string, VisionAlgorithmRuntime.Binding> bindings, List<VisionAlgorithmRuntime.ResourceEntry> leases)
    { _owner = owner; _bindings = bindings; _leases = leases; }
    /// <summary>执行一个已绑定的类型化算法调用。</summary>
    public TResult Invoke<T, TResult>(string bindingKey, Func<T, TResult> invoke, CancellationToken token = default) where T : class
    {
        if (invoke == null) throw new ArgumentNullException(nameof(invoke));
        // 锁仅保护“取得调用租约”：实际算法执行不能持有计划锁，否则停止无法及时归还计划引用。
        VisionAlgorithmRuntime.Binding binding;
        lock (_gate)
        {
            if (_leases == null) throw new ObjectDisposedException(nameof(VisionAlgorithmPlan));
            if (!_bindings.TryGetValue(bindingKey, out binding!)) throw new InvalidOperationException($"缺少算法绑定 {bindingKey}。");
            if (!typeof(T).IsInstanceOfType(binding.Entry.Task.GetAwaiter().GetResult().Instance)) throw new InvalidOperationException("请求接口与冻结绑定不匹配。");
            _owner.RetainInvocation(binding);
        }
        return _owner.Invoke(binding, invoke, token);
    }
    /// <summary>异步调用；直到实际任务结束才归还调用租约，不能把Task当作已完成结果。</summary>
    public Task<TResult> InvokeAsync<T, TResult>(string bindingKey, Func<T, CancellationToken, Task<TResult>> invoke, CancellationToken token = default) where T : class
    {
        if (invoke == null) throw new ArgumentNullException(nameof(invoke));
        VisionAlgorithmRuntime.Binding binding;
        lock (_gate)
        {
            if (_leases == null) throw new ObjectDisposedException(nameof(VisionAlgorithmPlan));
            if (!_bindings.TryGetValue(bindingKey, out binding!)) throw new InvalidOperationException($"缺少算法绑定 {bindingKey}。");
            if (!typeof(T).IsInstanceOfType(binding.Entry.Task.GetAwaiter().GetResult().Instance)) throw new InvalidOperationException("请求接口与冻结绑定不匹配。");
            _owner.RetainInvocation(binding);
        }
        return _owner.InvokeAsync(binding, invoke, token);
    }
    /// <summary>取得实际选择的实现描述，用于结果溯源。</summary>
    public VisionAlgorithmDescriptor GetDescriptor(string bindingKey) => _bindings.TryGetValue(bindingKey, out var binding)
        ? binding.Descriptor : throw new InvalidOperationException($"缺少算法绑定 {bindingKey}。");
    /// <summary>归还本计划资源引用，最后在途调用结束后才销毁。</summary>
    public void Dispose()
    {
        List<VisionAlgorithmRuntime.ResourceEntry>? leases;
        lock (_gate) { leases = _leases; _leases = null; }
        if (leases != null) foreach (var entry in leases.AsEnumerable().Reverse()) _owner.Release(entry);
    }
}
