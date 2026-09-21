using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DP.Vision.Acquisition;

/// <summary>采集Provider插件Manifest中的标准Module分组。</summary>
public static class VisionAcquisitionProviderModuleGroups
{
    /// <summary>采集Provider Module分组；与工作流统一插件包格式共用同一个Manifest文件。</summary>
    public const string VisionAcquisition = "visionAcquisition";
}

/// <summary>
/// 与工作流统一插件包格式兼容的Manifest。只读取本层需要的字段，
/// 其他分组和额外字段由各自宿主解释，因此同一个 <c>plugin.json</c> 可同时声明两类Module。
/// </summary>
public sealed class VisionAcquisitionProviderManifest
{
    /// <summary>当前支持的Manifest契约版本。</summary>
    public const int CurrentManifestVersion = 1;

    /// <summary>Manifest固定文件名；插件根目录及其一级子目录都会被扫描。</summary>
    public const string FileName = "plugin.json";

    /// <summary>Manifest契约版本。</summary>
    public int ManifestVersion { get; set; } = CurrentManifestVersion;

    /// <summary>跨版本稳定的插件标识。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>插件实现版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>面向操作员的插件显示名；缺省时用PluginId代替。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>必须同时安装的插件标识。</summary>
    public string[] Requires { get; set; } = Array.Empty<string>();

    /// <summary>Module分组及其相对于Manifest的程序集路径。</summary>
    public Dictionary<string, string[]> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取并规范化Manifest。</summary>
    /// <param name="manifestPath">Manifest文件路径。</param>
    /// <returns>已规范化的Manifest。</returns>
    /// <exception cref="VisionSourceConfigurationException">文件不可读、不是有效JSON或必填字段无效。</exception>
    public static VisionAcquisitionProviderManifest Read(string manifestPath)
    {
        VisionAcquisitionProviderManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<VisionAcquisitionProviderManifest>(
                File.ReadAllText(manifestPath), VisionAcquisitionProviderPluginLoader.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new VisionSourceConfigurationException(
                $"插件Manifest {manifestPath} 不是有效JSON：{exception.Message}", exception);
        }

        if (manifest is null)
            throw new VisionSourceConfigurationException($"插件Manifest {manifestPath} 内容为空。");
        if (manifest.ManifestVersion != CurrentManifestVersion)
            throw new VisionSourceConfigurationException(
                $"插件Manifest {manifestPath} 使用不受支持的契约版本 {manifest.ManifestVersion}；当前只支持 {CurrentManifestVersion}。");
        if (string.IsNullOrWhiteSpace(manifest.PluginId))
            throw new VisionSourceConfigurationException($"插件Manifest {manifestPath} 缺少 pluginId。");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new VisionSourceConfigurationException($"插件Manifest {manifestPath} 缺少 version。");

        manifest.PluginId = manifest.PluginId.Trim();
        manifest.Version = manifest.Version.Trim();
        manifest.DisplayName = string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.PluginId : manifest.DisplayName.Trim();
        manifest.Requires = (manifest.Requires ?? Array.Empty<string>())
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifest.Requires.Contains(manifest.PluginId, StringComparer.OrdinalIgnoreCase))
            throw new VisionSourceConfigurationException($"插件 {manifest.PluginId} 不能依赖自身。");
        manifest.Modules = manifest.Modules is null
            ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string[]>(manifest.Modules, StringComparer.OrdinalIgnoreCase);
        return manifest;
    }
}

/// <summary>一次插件加载的失败记录；一个插件失败不阻断其他插件，但必须被报告而不是静默忽略。</summary>
/// <param name="ManifestPath">Manifest文件路径。</param>
/// <param name="Reason">失败原因。</param>
public sealed record VisionAcquisitionProviderPluginFailure(string ManifestPath, string Reason);

/// <summary>宿主侧Provider可用性视图；用于在首节点执行前把Provider级诊断写进逻辑源目录。</summary>
/// <param name="ProviderId">Provider稳定身份。</param>
/// <param name="PluginId">声明该Provider的插件标识。</param>
/// <param name="IsAvailable">当前是否可用。</param>
/// <param name="Diagnostic">不可用原因；可用时为空。</param>
public sealed record VisionAcquisitionProviderAvailability(
    string ProviderId,
    string PluginId,
    bool IsAvailable,
    string? Diagnostic);

/// <summary>加载成功的插件。</summary>
/// <param name="PluginId">插件稳定身份。</param>
/// <param name="DisplayName">面向操作员的插件显示名。</param>
/// <param name="Version">插件实现版本。</param>
/// <param name="ManifestPath">Manifest文件路径。</param>
/// <param name="ProviderIds">本插件Module实际贡献的Provider身份，按序数排序。</param>
/// <param name="Entry">插件入口；健康报告由入口可选实现。</param>
/// <param name="Module">插件创建的Module，可直接参与候选组合。</param>
public sealed record VisionAcquisitionProviderPlugin(
    string PluginId,
    string DisplayName,
    string Version,
    string ManifestPath,
    IReadOnlyList<string> ProviderIds,
    IVisionAcquisitionProviderPlugin Entry,
    IVisionAcquisitionProviderModule Module)
{
    /// <summary>报告本插件当前是否可用。</summary>
    /// <param name="diagnostic">不可用原因；可用时为空。</param>
    /// <returns>可用时返回 <see langword="true"/>；入口未实现健康报告时视为可用。</returns>
    public bool TryGetHealth(out string? diagnostic)
    {
        if (Entry is IVisionAcquisitionProviderHealth health)
            return health.TryGetHealth(out diagnostic);
        diagnostic = null;
        return true;
    }
}

/// <summary>插件加载结果；宿主用它装配Provider组合并生成逻辑源目录。</summary>
/// <param name="Plugins">加载成功的插件，按PluginId排序。</param>
/// <param name="Failures">失败记录，按Manifest路径排序。</param>
public sealed record VisionAcquisitionProviderPluginLoadResult(
    IReadOnlyList<VisionAcquisitionProviderPlugin> Plugins,
    IReadOnlyList<VisionAcquisitionProviderPluginFailure> Failures)
{
    /// <summary>全部已加载插件的Module，按PluginId排序。</summary>
    public IReadOnlyList<IVisionAcquisitionProviderModule> Modules =>
        Plugins.Select(plugin => plugin.Module).ToArray();

    /// <summary>按ProviderId排序的Provider可用性视图。</summary>
    public IReadOnlyList<VisionAcquisitionProviderAvailability> ProviderAvailability =>
        Plugins
            .SelectMany(plugin => plugin.ProviderIds.Select(providerId =>
            {
                var available = plugin.TryGetHealth(out var diagnostic);
                return new VisionAcquisitionProviderAvailability(providerId, plugin.PluginId, available, diagnostic);
            }))
            .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// 从插件目录加载采集Provider插件。只认识Manifest与 <see cref="IVisionAcquisitionProviderPlugin"/>，
/// 因此宿主不需要在编译期引用任何厂商程序集；也不得反向引用工作流以复用其Loader。
/// </summary>
public sealed class VisionAcquisitionProviderPluginLoader
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>扫描目录并加载全部插件。</summary>
    /// <param name="pluginRoot">插件根目录；只扫描该目录及其一级子目录下的 <c>plugin.json</c>。</param>
    /// <param name="privateConfigurationProvider">按PluginId提供插件私有配置文本；公共层不解释其内容。</param>
    /// <returns>加载结果；单个插件失败不会阻止其他插件加载。</returns>
    public VisionAcquisitionProviderPluginLoadResult Load(
        string pluginRoot,
        Func<string, string?>? privateConfigurationProvider = null)
    {
        var plugins = new List<VisionAcquisitionProviderPlugin>();
        var failures = new List<VisionAcquisitionProviderPluginFailure>();
        if (string.IsNullOrWhiteSpace(pluginRoot))
            return new VisionAcquisitionProviderPluginLoadResult(plugins, failures);

        var root = Path.GetFullPath(pluginRoot);
        if (!Directory.Exists(root))
        {
            failures.Add(new VisionAcquisitionProviderPluginFailure(
                root, "插件目录不存在；未加载任何采集Provider插件。"));
            return new VisionAcquisitionProviderPluginLoadResult(plugins, failures);
        }

        foreach (var manifestPath in DiscoverManifests(root))
        {
            try
            {
                plugins.AddRange(LoadPackage(manifestPath, privateConfigurationProvider));
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                failures.Add(new VisionAcquisitionProviderPluginFailure(manifestPath, failure.Message));
            }
        }

        return new VisionAcquisitionProviderPluginLoadResult(
            plugins.OrderBy(plugin => plugin.PluginId, StringComparer.Ordinal).ToArray(),
            failures.OrderBy(item => item.ManifestPath, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<string> DiscoverManifests(string root)
    {
        var paths = new List<string>();
        var rootManifest = Path.Combine(root, VisionAcquisitionProviderManifest.FileName);
        if (File.Exists(rootManifest))
            paths.Add(rootManifest);
        paths.AddRange(Directory.EnumerateDirectories(root)
            .Select(directory => Path.Combine(directory, VisionAcquisitionProviderManifest.FileName))
            .Where(File.Exists));

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<VisionAcquisitionProviderPlugin> LoadPackage(
        string manifestPath,
        Func<string, string?>? privateConfigurationProvider)
    {
        var manifest = VisionAcquisitionProviderManifest.Read(manifestPath);
        if (!manifest.Modules.TryGetValue(VisionAcquisitionProviderModuleGroups.VisionAcquisition, out var assemblyFiles))
            return Array.Empty<VisionAcquisitionProviderPlugin>();

        var packageRoot = Path.GetFullPath(Path.GetDirectoryName(manifestPath)! + Path.DirectorySeparatorChar);
        var configuration = privateConfigurationProvider?.Invoke(manifest.PluginId);
        var plugins = new List<VisionAcquisitionProviderPlugin>();
        var entries = new List<IVisionAcquisitionProviderPlugin>();
        foreach (var assemblyFile in assemblyFiles ?? Array.Empty<string>())
        {
            var assemblyPath = ResolveModulePath(manifestPath, packageRoot, assemblyFile);
            entries.AddRange(CreateEntries(assemblyPath, manifest));
        }

        if (entries.Count == 0)
            throw new VisionSourceConfigurationException(
                $"插件 {manifest.PluginId} 把程序集声明到 {VisionAcquisitionProviderModuleGroups.VisionAcquisition} 分组，但其中没有实现 IVisionAcquisitionProviderPlugin 的公开类型。");

        foreach (var entry in entries)
        {
            var module = entry.CreateModule(configuration)
                ?? throw new VisionSourceConfigurationException($"插件 {entry.PluginId} 未返回Module。");
            if (string.IsNullOrWhiteSpace(module.ExtensionId))
                throw new VisionSourceConfigurationException($"插件 {entry.PluginId} 返回的Module缺少身份。");
            plugins.Add(new VisionAcquisitionProviderPlugin(
                entry.PluginId,
                manifest.DisplayName,
                manifest.Version,
                manifestPath,
                CollectProviderIds(entry.PluginId, module),
                entry,
                module));
        }

        return plugins;
    }

    private static string ResolveModulePath(string manifestPath, string packageRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new VisionSourceConfigurationException($"插件Manifest {manifestPath} 包含空程序集路径。");
        var path = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
        if (!path.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            throw new VisionSourceConfigurationException($"插件程序集路径不能离开插件包目录：{relativePath}。");
        if (!File.Exists(path))
            throw new VisionSourceConfigurationException($"插件程序集不存在：{path}。");
        return path;
    }

    private static IReadOnlyList<IVisionAcquisitionProviderPlugin> CreateEntries(
        string assemblyPath,
        VisionAcquisitionProviderManifest manifest)
    {
        Assembly assembly;
        try
        {
            assembly = VisionAcquisitionPluginAssemblyLoader.Load(assemblyPath);
        }
        catch (BadImageFormatException failure)
        {
            throw new VisionSourceConfigurationException(
                $"插件程序集 {assemblyPath} 不是托管程序集；请检查插件包内容与CPU架构是否匹配。{failure.Message}", failure);
        }

        Type[] types;
        try
        {
            types = assembly.GetExportedTypes()
                .Where(type => typeof(IVisionAcquisitionProviderPlugin).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false }
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var details = string.Join(Environment.NewLine,
                exception.LoaderExceptions.Where(item => item is not null).Select(item => item!.Message));
            throw new VisionSourceConfigurationException(
                $"无法检查插件程序集 {assemblyPath} 中的入口类型。{Environment.NewLine}{details}", exception);
        }

        var entries = new List<IVisionAcquisitionProviderPlugin>();
        foreach (var type in types)
        {
            if (Activator.CreateInstance(type) is not IVisionAcquisitionProviderPlugin entry)
                throw new VisionSourceConfigurationException($"插件入口类型无法创建：{type.FullName}。");
            if (!string.Equals(entry.PluginId, manifest.PluginId, StringComparison.Ordinal))
                throw new VisionSourceConfigurationException(
                    $"插件入口 {type.FullName} 报告的身份 {entry.PluginId} 与Manifest声明的 {manifest.PluginId} 不一致。");
            entries.Add(entry);
        }

        return entries;
    }

    private static IReadOnlyList<string> CollectProviderIds(string pluginId, IVisionAcquisitionProviderModule module)
    {
        var collector = new CollectingBuilder();
        module.Contribute(collector);
        if (collector.ProviderIds.Count == 0)
            throw new VisionSourceConfigurationException($"插件 {pluginId} 的Module没有贡献任何Provider。");
        return collector.ProviderIds;
    }

    private sealed class CollectingBuilder : IVisionAcquisitionProviderContributionBuilder
    {
        private readonly List<string> _providerIds = new List<string>();

        public IReadOnlyList<string> ProviderIds =>
            _providerIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        public void Register(VisionAcquisitionProviderRegistration registration)
        {
            if (registration is null)
                throw new VisionSourceConfigurationException("Provider注册不能为空。");
            if (string.IsNullOrWhiteSpace(registration.ProviderId))
                throw new VisionSourceConfigurationException("Provider身份不能为空。");
            _providerIds.Add(registration.ProviderId.Trim());
        }
    }
}
