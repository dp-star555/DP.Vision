using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DP.Plugins;

namespace DP.Vision.Algorithms;

/// <summary>算法登记/发现诊断。</summary>
public sealed class VisionAlgorithmDiagnostic
{
    /// <summary>创建来源和原因。</summary>
    public VisionAlgorithmDiagnostic(string source, string reason) { Source = source; Reason = reason; }
    /// <summary>模块或包路径。</summary>
    public string Source { get; }
    /// <summary>完整原因。</summary>
    public string Reason { get; }
}

/// <summary>不可变算法目录；有冲突的实现不进入可用集合。</summary>
public sealed class VisionAlgorithmCatalog
{
    private readonly IReadOnlyDictionary<string, VisionAlgorithmDescriptor> _byId;
    private VisionAlgorithmCatalog(IEnumerable<VisionAlgorithmDescriptor> descriptors, IEnumerable<VisionAlgorithmDiagnostic> diagnostics,
        IReadOnlyDictionary<string, VisionAlgorithmOrigin> origins)
    {
        Implementations = Array.AsReadOnly(descriptors.OrderBy(d => d.ImplementationId, StringComparer.Ordinal).ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.OrderBy(d => d.Source, StringComparer.Ordinal).ThenBy(d => d.Reason, StringComparer.Ordinal).ToArray());
        _byId = new ReadOnlyDictionary<string, VisionAlgorithmDescriptor>(Implementations.ToDictionary(d => d.ImplementationId, StringComparer.Ordinal));
        Origins = origins;
    }
    /// <summary>可用实现。</summary>
    public IReadOnlyList<VisionAlgorithmDescriptor> Implementations { get; }
    /// <summary>被拒绝模块、冲突及加载诊断。</summary>
    public IReadOnlyList<VisionAlgorithmDiagnostic> Diagnostics { get; }
    /// <summary>成功实现的入口与程序集来源；不在节点配置中重复保存。</summary>
    public IReadOnlyDictionary<string, VisionAlgorithmOrigin> Origins { get; }
    /// <summary>按身份进行不抛异常的只读查询。</summary>
    public bool TryGet(string implementationId, out VisionAlgorithmDescriptor? descriptor) => _byId.TryGetValue(implementationId, out descriptor);
    /// <summary>按明确实现身份取得描述，缺失明确报错。</summary>
    public VisionAlgorithmDescriptor GetRequired(string implementationId) =>
        _byId.TryGetValue(implementationId, out var descriptor) ? descriptor : throw new InvalidOperationException($"算法实现 {implementationId} 不可用。");

    /// <summary>原子组合模块贡献，失败模块完全撤销，冲突项全部拒绝。</summary>
    public static VisionAlgorithmCatalog Compose(IEnumerable<IVisionAlgorithmModule> modules, IEnumerable<VisionAlgorithmDiagnostic>? diagnostics = null)
    {
        if (modules == null) throw new ArgumentNullException(nameof(modules));
        var errors = (diagnostics ?? Array.Empty<VisionAlgorithmDiagnostic>()).ToList();
        var candidates = new List<KeyValuePair<string, VisionAlgorithmDescriptor>>();
        var sources = new Dictionary<string, VisionAlgorithmOrigin>(StringComparer.Ordinal);
        foreach (var group in modules.GroupBy(m => m.ExtensionId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1)
            { errors.Add(new VisionAlgorithmDiagnostic(group.Key ?? "", "模块身份为空或重复。")); continue; }
            var builder = new Registration();
            try
            {
                group.Single().Register(builder);
                var module = group.Single();
                sources[group.Key!] = new VisionAlgorithmOrigin(group.Key!, module.GetType().FullName ?? module.GetType().Name, module.GetType().Assembly.Location);
                candidates.AddRange(builder.Items.Select(d => new KeyValuePair<string, VisionAlgorithmDescriptor>(group.Key!, d)));
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { errors.Add(new VisionAlgorithmDiagnostic(group.Key!, failure.ToString())); }
        }
        var rejected = new HashSet<VisionAlgorithmDescriptor>();
        foreach (var group in candidates.GroupBy(p => p.Value.ImplementationId, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add(new VisionAlgorithmDiagnostic(group.Key, "实现身份冲突：" + string.Join("、", group.Select(p => p.Key))));
            foreach (var candidate in group) rejected.Add(candidate.Value);
        }
        foreach (var group in candidates.GroupBy(p => p.Value.CapabilityId, StringComparer.Ordinal).Where(g => g.Select(p => p.Value.ContractType).Distinct().Count() > 1))
        {
            errors.Add(new VisionAlgorithmDiagnostic(group.Key, "能力身份对应不同接口：" + string.Join("、", group.Select(p => p.Key))));
            foreach (var candidate in group) rejected.Add(candidate.Value);
        }
        var accepted = candidates.Where(p => !rejected.Contains(p.Value)).ToArray();
        return new VisionAlgorithmCatalog(accepted.Select(p => p.Value), errors,
            new ReadOnlyDictionary<string, VisionAlgorithmOrigin>(accepted.ToDictionary(p => p.Value.ImplementationId, p => sources[p.Key], StringComparer.Ordinal)));
    }

    private sealed class Registration : IVisionAlgorithmRegistration
    {
        internal readonly List<VisionAlgorithmDescriptor> Items = new List<VisionAlgorithmDescriptor>();
        public void Add(VisionAlgorithmDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (Items.Any(d => d.ImplementationId == descriptor.ImplementationId)) throw new InvalidOperationException($"模块重复登记 {descriptor.ImplementationId}。");
            Items.Add(descriptor);
        }
    }
}

/// <summary>成功登记的模块来源。</summary>
public sealed class VisionAlgorithmOrigin
{
    /// <summary>保存入口和实际程序集路径。</summary>
    public VisionAlgorithmOrigin(string moduleId, string entryType, string assemblyPath)
    { ModuleId = moduleId; EntryType = entryType; AssemblyPath = assemblyPath; }
    /// <summary>模块身份。</summary>
    public string ModuleId { get; }
    /// <summary>入口类型。</summary>
    public string EntryType { get; }
    /// <summary>实际加载路径。</summary>
    public string AssemblyPath { get; }
}

/// <summary>算法模块发现适配，复用领域中立包会话。</summary>
public sealed class VisionAlgorithmModuleLoader
{
    private readonly PluginLoadSession _session;
    /// <summary>创建发现器。</summary>
    public VisionAlgorithmModuleLoader(PluginLoadSession? session = null)
    {
        _session = session ?? new PluginLoadSession();
        _session.RegisterSharedAssembly(typeof(IVisionAlgorithmModule).Assembly);
        _session.RegisterSharedAssembly(typeof(IImageSource).Assembly);
    }
    /// <summary>发现并冻结目录；扩展契约在发现前由宿主共享目录注册。</summary>
    public VisionAlgorithmCatalog Load(string root, IEnumerable<IVisionAlgorithmModule>? builtInModules = null)
    {
        var discovered = _session.Discover<IVisionAlgorithmModule>(root);
        // 内置模块所在契约 DLL 可能被包附带；同一个入口类型仅贡献一次，显式内置实例优先。
        return VisionAlgorithmCatalog.Compose((builtInModules ?? Array.Empty<IVisionAlgorithmModule>()).Concat(discovered.Modules)
            .GroupBy(module => module.GetType()).Select(group => group.First()),
            discovered.Failures.Select(f => new VisionAlgorithmDiagnostic(f.AssemblyPath, f.Reason)));
    }
}
