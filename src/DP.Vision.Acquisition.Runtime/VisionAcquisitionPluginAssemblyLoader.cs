using System;
using System.Linq;
using System.Reflection;

namespace DP.Vision.Acquisition;

/// <summary>
/// 插件程序集的统一加载入口：同身份程序集已由宿主加载时直接复用该实例，只有在宿主确实没有时才
/// <see cref="Assembly.LoadFrom(string)"/>。
/// <para>
/// 这不是加载优化，而是正确性要求。.NET Framework 的 <c>LoadFrom</c> 会把插件目录里的副本装进
/// LoadFrom 上下文，于是同一份契约程序集在进程里存在两个类型身份：插件里的入口类型实现的是
/// LoadFrom 上下文那一份，而宿主用 <c>typeof(...).IsAssignableFrom</c> 比对的是默认上下文那一份，
/// 结果静默为 false——插件被误判成"程序集里没有入口类型"，只在 <c>failures</c> 里留一条难以解释的诊断。
/// </para>
/// <para>
/// 部署约定把整个输出目录当作插件包，因此目录里必然带有宿主提供的契约程序集
/// （<c>DP.Vision</c>、<c>DP.Vision.Acquisition.Abstractions</c>），也可能存在被复制到子目录的重复副本，
/// 这些都必须走复用路径。.NET Framework 与 .NET 上的行为因此一致。
/// </para>
/// </summary>
internal static class VisionAcquisitionPluginAssemblyLoader
{
    /// <summary>加载插件程序集；同身份程序集已加载时复用该实例，避免产生第二份契约类型。</summary>
    /// <param name="assemblyPath">插件程序集路径。</param>
    /// <returns>可用于反射检查入口类型的程序集实例。</returns>
    /// <exception cref="BadImageFormatException">路径指向的不是托管程序集（原生SDK依赖）。</exception>
    /// <exception cref="VisionSourceConfigurationException">托管程序集无法加载。</exception>
    internal static Assembly Load(string assemblyPath)
    {
        // 原生依赖（halcon/pylon 等）在这里抛出 BadImageFormatException，由调用方决定跳过还是报告。
        var identity = AssemblyName.GetAssemblyName(assemblyPath);
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => IsSameIdentity(candidate.GetName(), identity));
        if (loaded is not null)
            return loaded;

        try
        {
            return Assembly.LoadFrom(assemblyPath);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            throw new VisionSourceConfigurationException(
                $"插件程序集 {assemblyPath} 无法加载；请检查原生依赖与CPU架构是否匹配。{failure.Message}", failure);
        }
    }

    private static bool IsSameIdentity(AssemblyName left, AssemblyName right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
        && left.Version == right.Version
        && string.Equals(left.CultureName, right.CultureName, StringComparison.OrdinalIgnoreCase)
        && PublicKeyTokenMatches(left, right);

    private static bool PublicKeyTokenMatches(AssemblyName left, AssemblyName right)
    {
        var leftToken = left.GetPublicKeyToken() ?? Array.Empty<byte>();
        var rightToken = right.GetPublicKeyToken() ?? Array.Empty<byte>();
        return leftToken.SequenceEqual(rightToken);
    }
}
