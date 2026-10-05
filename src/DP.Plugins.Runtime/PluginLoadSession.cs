using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
#if NET8_0_OR_GREATER
using System.Runtime.Loader;
#endif

namespace DP.Plugins;

/// <summary>程序集发现失败，保留部署路径和原始原因。</summary>
public sealed class PluginLoadFailure
{
    /// <summary>创建失败记录。</summary>
    public PluginLoadFailure(string path, string reason) { AssemblyPath = path; Reason = reason; }
    /// <summary>失败程序集路径。</summary>
    public string AssemblyPath { get; }
    /// <summary>加载或模块检查原因。</summary>
    public string Reason { get; }
}

/// <summary>一次模块发现结果；失败不阻断无关程序集。</summary>
public sealed class PluginDiscoveryResult<T> where T : class
{
    internal PluginDiscoveryResult(IEnumerable<T> modules, IEnumerable<PluginLoadFailure> failures)
    { Modules = Array.AsReadOnly(modules.ToArray()); Failures = Array.AsReadOnly(failures.ToArray()); }
    /// <summary>已发现模块。</summary>
    public IReadOnlyList<T> Modules { get; }
    /// <summary>完整诊断。</summary>
    public IReadOnlyList<PluginLoadFailure> Failures { get; }
}

/// <summary>
/// 宿主生命周期内共用的插件包加载会话。领域加载器声明共享契约，按所需接口发现入口；
/// 私有托管依赖在现代 .NET 按包隔离，net48 使用统一依赖版本。更新重启生效。
/// </summary>
public sealed class PluginLoadSession
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, Assembly> _shared = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Assembly> _assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, object> _modules = new Dictionary<Type, object>();
#if NET8_0_OR_GREATER
    private readonly Dictionary<string, PackageContext> _packages = new Dictionary<string, PackageContext>(StringComparer.OrdinalIgnoreCase);
#else
    private readonly Dictionary<string, AssemblyName> _legacyDependencies = new Dictionary<string, AssemblyName>(StringComparer.OrdinalIgnoreCase);
#endif

    /// <summary>创建会话；会话应由宿主同时传给节点、采集和算法加载器。</summary>
    public PluginLoadSession() => RegisterSharedAssembly(typeof(PluginLoadSession).Assembly);

    /// <summary>声明宿主与插件必须复用的契约程序集；同名不同版本明确拒绝。</summary>
    /// <param name="assembly">宿主提供的契约实例。</param>
    public void RegisterSharedAssembly(Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));
        lock (_gate)
        {
            var name = assembly.GetName();
            if (_shared.TryGetValue(name.Name!, out var existing) && existing != assembly)
            {
                if (!SameIdentity(existing.GetName(), name))
                    throw new InvalidOperationException($"共享契约冲突：{existing.FullName} / {name.FullName}。");
                VerifyContent(assembly.Location, name);
                return;
            }
            if (!string.IsNullOrEmpty(assembly.Location)) VerifyContent(assembly.Location, name);
            _shared[name.Name!] = assembly;
        }
    }

    /// <summary>先加载并共享一个外部契约。独立能力包用此方法保持节点和引擎的类型身份一致。</summary>
    /// <param name="path">契约程序集路径。</param>
    public void RegisterSharedAssemblyPath(string path)
    {
        lock (_gate)
        {
            path = Path.GetFullPath(path);
            var name = AssemblyName.GetAssemblyName(path);
            VerifyContent(path, name);
            if (_shared.TryGetValue(name.Name!, out var existing))
            {
                if (!SameIdentity(existing.GetName(), name))
                    throw new InvalidOperationException($"共享契约版本冲突：{existing.FullName} / {name.FullName}。");
                return;
            }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => SameIdentity(a.GetName(), name));
#if NET8_0_OR_GREATER
            loaded = loaded ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
#else
            loaded = loaded ?? Assembly.LoadFrom(path);
#endif
            RegisterSharedAssembly(loaded);
        }
    }

    /// <summary>加载部署目录 contracts 中的共享扩展契约；宿主无需列举新增能力类型。</summary>
    public void RegisterSharedContracts(string pluginRoot)
    {
        var root = Path.Combine(Path.GetFullPath(pluginRoot), "contracts");
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            RegisterSharedAssemblyPath(path);
    }

    /// <summary>加载一个包内程序集；重复调用和同身份副本复用已有实例。</summary>
    /// <param name="assemblyPath">入口程序集路径。</param>
    /// <param name="packageRoot">包根目录，默认程序集所在目录。</param>
    /// <returns>已加载的程序集。</returns>
    public Assembly LoadAssembly(string assemblyPath, string? packageRoot = null)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        packageRoot = Path.GetFullPath(packageRoot ?? Path.GetDirectoryName(assemblyPath)!);
        lock (_gate)
        {
#if !NET8_0_OR_GREATER
            VerifyLegacyPackage(packageRoot);
#endif
            var name = AssemblyName.GetAssemblyName(assemblyPath);
            VerifyContent(assemblyPath, name);
            if (_shared.TryGetValue(name.Name!, out var shared))
            {
                if (!SameIdentity(shared.GetName(), name)) throw new InvalidOperationException($"共享契约版本不兼容：{name.FullName}。");
                return shared;
            }
            var key = packageRoot + "|" + name.FullName;
            if (_assemblies.TryGetValue(key, out var cached)) return cached;
            // 兼容宿主静态引用的内置包；不把它的全部依赖都当作共享契约。
            var existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => SameIdentity(a.GetName(), name)
#if NET8_0_OR_GREATER
                && AssemblyLoadContext.GetLoadContext(a) == AssemblyLoadContext.Default
#endif
            );
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(existing.Location)) VerifyContent(existing.Location, name);
                _assemblies[key] = existing;
                return existing;
            }
#if NET8_0_OR_GREATER
            if (!_packages.TryGetValue(packageRoot, out var context))
                _packages.Add(packageRoot, context = new PackageContext(this, packageRoot));
            context.AddResolver(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
#else
            var incompatible = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase) && !SameIdentity(a.GetName(), name));
            if (incompatible != null) throw new InvalidOperationException($"net48 统一依赖版本约束冲突：{incompatible.FullName} / {name.FullName}。");
            var assembly = Assembly.LoadFrom(assemblyPath);
#endif
            _assemblies[key] = assembly;
            return assembly;
        }
    }

    /// <summary>取得指定接口的公开无参模块，模块跨发现流程只创建一次。</summary>
    public IReadOnlyList<T> GetModules<T>(Assembly assembly) where T : class
    {
        lock (_gate)
        {
            // 只有框架引用的SDK不可能实现应用自己的模块契约。避免为了扫描无关依赖
            // 解析其全部公开UI类型（例如HALCON同时声明WinForms/WPF控件）。
            // 含自定义基类/中间契约的程序集仍完整检查，保留间接实现接口的入口。
            var contract = typeof(T).Assembly.GetName();
            if (!IsFrameworkReference(contract) && assembly.GetName().Name != contract.Name
                && assembly.GetReferencedAssemblies().All(reference => reference.Name != contract.Name && IsFrameworkReference(reference)))
                return Array.Empty<T>();
            Type[] types;
            try { types = assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException failure)
            {
                throw new InvalidOperationException($"无法检查 {assembly.Location}：" + string.Join("；",
                    failure.LoaderExceptions.Where(e => e != null).Select(e => e!.Message)), failure);
            }
            var modules = new List<T>();
            foreach (var type in types.Where(t => typeof(T).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract
                && !t.ContainsGenericParameters && t.GetConstructor(Type.EmptyTypes) != null).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (!_modules.TryGetValue(type, out var module))
                { module = Activator.CreateInstance(type)!; _modules.Add(type, module); }
                modules.Add((T)module);
            }
            return modules.AsReadOnly();
        }
    }

    private static bool IsFrameworkReference(AssemblyName reference)
    {
        var name = reference.Name ?? "";
        if (name != "mscorlib" && name != "netstandard" && name != "System" && !name.StartsWith("System.", StringComparison.Ordinal)
            && name != "WindowsBase" && name != "PresentationCore" && name != "PresentationFramework") return false;
        var token = string.Concat((reference.GetPublicKeyToken() ?? Array.Empty<byte>()).Select(b => b.ToString("x2")));
        return token == "b77a5c561934e089" || token == "b03f5f7f11d50a3a" || token == "31bf3856ad364e35"
            || token == "cc7b13ffcd2ddd51" || token == "7cec85d7bea7798e";
    }

    /// <summary>无需 Manifest，递归扫描目录中的模块；原生依赖跳过，托管失败报告。</summary>
    public PluginDiscoveryResult<T> Discover<T>(string root, Func<string, bool>? includeAssembly = null) where T : class
    {
        var modules = new List<T>(); var failures = new List<PluginLoadFailure>();
        if (string.IsNullOrWhiteSpace(root)) return new PluginDiscoveryResult<T>(modules, failures);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            return new PluginDiscoveryResult<T>(modules, new[] { new PluginLoadFailure(root, "插件目录不存在。") });
        foreach (var path in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (includeAssembly != null && !includeAssembly(path)) continue;
            try
            {
                var relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                var separator = relative.IndexOf(Path.DirectorySeparatorChar);
                var package = separator < 0 ? root : Path.Combine(root, relative.Substring(0, separator));
                foreach (var module in GetModules<T>(LoadAssembly(path, package)))
                    if (!modules.Contains(module)) modules.Add(module);
            }
            catch (BadImageFormatException) { }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { failures.Add(new PluginLoadFailure(path, failure.ToString())); }
        }
        return new PluginDiscoveryResult<T>(modules, failures);
    }

    private void VerifyContent(string path, AssemblyName name)
    {
        using var stream = File.OpenRead(path); using var sha = SHA256.Create();
        var hash = Convert.ToBase64String(sha.ComputeHash(stream));
        if (_content.TryGetValue(name.FullName, out var known) && known != hash)
            throw new InvalidOperationException($"同身份程序集内容冲突：{name.FullName}，路径 {path}。");
        _content[name.FullName] = hash;
    }

    private static bool SameIdentity(AssemblyName left, AssemblyName right) =>
        string.Equals(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);

#if !NET8_0_OR_GREATER
    private void VerifyLegacyPackage(string root)
    {
        // 在实例化入口前验证整个包，避免扫描顺序隐藏第三方依赖版本冲突。
        foreach (var path in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
        {
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(path); }
            catch (BadImageFormatException) { continue; }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
            var existing = loaded?.GetName();
            if (existing == null) _legacyDependencies.TryGetValue(name.Name!, out existing);
            if (existing != null && !SameIdentity(existing, name))
                throw new InvalidOperationException($"net48 统一依赖版本约束冲突：{existing.FullName} / {name.FullName}，包 {root}。");
            VerifyContent(path, name);
            _legacyDependencies[name.Name!] = name;
        }
    }
#endif

#if NET8_0_OR_GREATER
    private sealed class PackageContext : AssemblyLoadContext
    {
        private readonly PluginLoadSession _owner;
        private readonly string _root;
        private readonly List<AssemblyDependencyResolver> _resolvers = new List<AssemblyDependencyResolver>();
        private readonly HashSet<string> _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal PackageContext(PluginLoadSession owner, string root) : base("DP.Plugin:" + root, false) { _owner = owner; _root = root; }
        internal void AddResolver(string path) { if (_paths.Add(path)) _resolvers.Add(new AssemblyDependencyResolver(path)); }
        protected override Assembly? Load(AssemblyName name)
        {
            lock (_owner._gate)
            {
                if (_owner._shared.TryGetValue(name.Name!, out var shared))
                {
                    if (!SameIdentity(name, shared.GetName())) throw new FileLoadException($"共享契约版本不兼容：{name.FullName} / {shared.FullName}。");
                    return shared;
                }
                foreach (var resolver in _resolvers)
                { var path = resolver.ResolveAssemblyToPath(name); if (path != null) { _owner.VerifyContent(path, AssemblyName.GetAssemblyName(path)); return LoadFromAssemblyPath(path); } }
                var local = Path.Combine(_root, name.Name + ".dll");
                if (File.Exists(local)) { _owner.VerifyContent(local, AssemblyName.GetAssemblyName(local)); return LoadFromAssemblyPath(local); }
                return null;
            }
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            lock (_owner._gate)
            {
                foreach (var resolver in _resolvers)
                { var path = resolver.ResolveUnmanagedDllToPath(name); if (path != null) return LoadUnmanagedDllFromPath(path); }
                return IntPtr.Zero;
            }
        }
    }
#endif
}
