using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DP.Plugins;

namespace DP.Vision.Acquisition;

/// <summary>一次Driver Module扫描的失败记录；一个DLL失败不阻断其他DLL，但必须被报告。</summary>
/// <param name="AssemblyPath">程序集路径。</param>
/// <param name="Reason">失败原因。</param>
public sealed record VisionAcquisitionDriverModuleFailure(string AssemblyPath, string Reason);

/// <summary>Driver Module扫描结果；宿主用它组合并一次冻结Type Catalog。</summary>
/// <param name="Modules">扫描发现的Driver Module，按ExtensionId排序后路径稳定。</param>
/// <param name="Failures">失败记录，按程序集路径排序。</param>
public sealed record VisionAcquisitionDriverModuleLoadResult(
    IReadOnlyList<IVisionAcquisitionDriverModule> Modules,
    IReadOnlyList<VisionAcquisitionDriverModuleFailure> Failures);

/// <summary>
/// 从受信任插件目录自动发现采集Driver Module。
/// 加载依据是"程序集中存在实现 <see cref="IVisionAcquisitionDriverModule"/> 的公开类型"，
/// 不读取Manifest：Manifest不是加载依据，只能作为构建生成的包索引（用于跳过SDK依赖DLL和记录部署版本）。
/// 机器相机配置不通过本加载器传入任何插件信息，也不包含程序集路径。
/// </summary>
public sealed class VisionAcquisitionDriverModuleLoader
{
    private readonly PluginLoadSession _session;

    /// <summary>保留既有无参数构造入口。</summary>
    public VisionAcquisitionDriverModuleLoader() : this(null) { }

    /// <summary>创建采集发现器，可与 Workflow 和算法发现器共用加载会话。</summary>
    public VisionAcquisitionDriverModuleLoader(PluginLoadSession? session)
    {
        _session = session ?? new PluginLoadSession();
        _session.RegisterSharedAssembly(typeof(IVisionAcquisitionDriverModule).Assembly);
        _session.RegisterSharedAssembly(typeof(IImageSource).Assembly);
    }
    /// <summary>扫描插件根目录并加载全部Driver Module。</summary>
    /// <param name="pluginRoot">受信任插件根目录；递归扫描其下的所有托管DLL。</param>
    /// <returns>加载结果；单个DLL失败不会阻止其他DLL。</returns>
    public VisionAcquisitionDriverModuleLoadResult Load(string pluginRoot)
    {
        var modules = new List<IVisionAcquisitionDriverModule>();
        var failures = new List<VisionAcquisitionDriverModuleFailure>();
        if (string.IsNullOrWhiteSpace(pluginRoot))
            return new VisionAcquisitionDriverModuleLoadResult(modules, failures);

        var root = Path.GetFullPath(pluginRoot);
        if (!Directory.Exists(root))
        {
            failures.Add(new VisionAcquisitionDriverModuleFailure(
                root, "插件目录不存在；未扫描任何Driver Module。"));
            return new VisionAcquisitionDriverModuleLoadResult(modules, failures);
        }

        var discovered = _session.Discover<IVisionAcquisitionDriverModule>(root);
        modules.AddRange(discovered.Modules);
        failures.AddRange(discovered.Failures.Select(failure => new VisionAcquisitionDriverModuleFailure(failure.AssemblyPath, failure.Reason)));

        return new VisionAcquisitionDriverModuleLoadResult(
            modules
                .OrderBy(module => module.ExtensionId, StringComparer.Ordinal)
                .ThenBy(module => module.GetType().Assembly.Location, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            failures.OrderBy(item => item.AssemblyPath, StringComparer.Ordinal).ToArray());
    }

}
