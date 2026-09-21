using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

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

        foreach (var assemblyPath in DiscoverAssemblies(root))
        {
            try
            {
                modules.AddRange(CreateModules(assemblyPath));
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                failures.Add(new VisionAcquisitionDriverModuleFailure(assemblyPath, failure.Message));
            }
        }

        return new VisionAcquisitionDriverModuleLoadResult(
            modules
                .OrderBy(module => module.ExtensionId, StringComparer.Ordinal)
                .ThenBy(module => module.GetType().Assembly.Location, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            failures.OrderBy(item => item.AssemblyPath, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<string> DiscoverAssemblies(string root) =>
        Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<IVisionAcquisitionDriverModule> CreateModules(string assemblyPath)
    {
        // 原生SDK依赖DLL（halcon/pylon等）不是托管Driver Module；先按程序集元数据预检跳过，
        // 避免把每个原生库都当作失败报告。只有托管程序集才继续。
        try
        {
            _ = AssemblyName.GetAssemblyName(assemblyPath);
        }
        catch (BadImageFormatException)
        {
            return Array.Empty<IVisionAcquisitionDriverModule>();
        }

        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(assemblyPath);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            throw new VisionSourceConfigurationException(
                $"插件程序集 {assemblyPath} 无法加载；请检查原生依赖与CPU架构是否匹配。{failure.Message}", failure);
        }

        Type[] types;
        try
        {
            types = assembly.GetExportedTypes()
                .Where(type => IsDriverModuleType(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // 依赖解析不全会让部分类型无法加载；只使用能加载的类型。
            // 一个没有Driver Module的依赖DLL不是模块，静默跳过而不是报告失败。
            types = (exception.Types ?? Array.Empty<Type>())
                .Where(type => type is not null)
                .Select(type => type!)
                .Where(IsDriverModuleType)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }

        var modules = new List<IVisionAcquisitionDriverModule>();
        foreach (var type in types)
        {
            var created = Activator.CreateInstance(type);
            if (created is not IVisionAcquisitionDriverModule module)
                throw new VisionSourceConfigurationException($"Driver Module类型无法创建：{type.FullName}。");
            modules.Add(module);
        }

        return modules;
    }

    private static bool IsDriverModuleType(Type type) =>
        typeof(IVisionAcquisitionDriverModule).IsAssignableFrom(type)
        && type is { IsAbstract: false, IsInterface: false }
        && type.GetConstructor(Type.EmptyTypes) is not null;
}
