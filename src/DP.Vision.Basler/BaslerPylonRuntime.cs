using System;
using System.Runtime.InteropServices;

namespace DP.Vision.Basler;

/// <summary>
/// Basler pylon 原生运行时部署探测。
///
/// 与 HALCON 不同，pylon 的托管程序集随 NuGet 包一起还原，编译期一定存在；
/// "已安装插件但缺 SDK"因此是**运行时**问题：进程解析不到 pylon 原生基库时，
/// 一旦调用 pylon 托管 API 就会抛出 <c>SEHException</c>（原生侧异常）。
/// 所以这里在真正采集之前先做一次可判定的解析探测，让宿主能在首节点执行前拿到Provider级诊断。
/// </summary>
public static class BaslerPylonRuntime
{
    /// <summary>与本包版本绑定的 pylon 原生基库名；主版本号来自 <c>Basler.Pylon.NET.x64</c> 10.x。</summary>
    public const string NativeBaseLibrary = "PylonBase_v10.dll";

    private static readonly Lazy<bool> Deployment = new Lazy<bool>(Probe, isThreadSafe: true);

    /// <summary>本进程当前能否解析到 pylon 原生运行时。结果在进程内缓存一次。</summary>
    public static bool IsDeployed => Deployment.Value;

    /// <summary>构建"Provider已安装但运行时不可用"的诊断文本。</summary>
    /// <returns>面向操作员的诊断说明，包含已尝试的解析位置和处置办法。</returns>
    public static string DescribeMissingRuntime()
    {
        var pylonRoot = Environment.GetEnvironmentVariable("PYLON_ROOT");
        var rootText = string.IsNullOrWhiteSpace(pylonRoot)
            ? "环境变量 PYLON_ROOT 未设置"
            : $"环境变量 PYLON_ROOT = {pylonRoot}";
        return $"Provider {BaslerAcquisitionProvider.ProviderIdentity} 已安装，但 pylon 运行时未部署"
            + $"（进程无法解析 {NativeBaseLibrary}；{rootText}）。"
            + "请安装 Basler pylon Camera Software Suite（免费），或把 pylon 运行时随程序一起部署到"
            + "可执行文件目录或 PATH 上；也可以把依赖该Provider的逻辑源标记为不可用。";
    }

    /// <summary>探测 pylon 原生基库是否可被本进程解析。</summary>
    /// <returns>可解析时返回 <see langword="true"/>。</returns>
    private static bool Probe()
    {
#if BASLER_SDK
        try
        {
            // 只做解析探测，成功后不卸载：pylon 运行时的语义本来就是随进程存活，
            // 真正采集时同一进程还会再次加载它，提前加载不产生额外状态。
            return NativeMethods.LoadLibraryW(NativeBaseLibrary) != IntPtr.Zero;
        }
        catch (Exception)
        {
            // 探测本身不允许把进程带崩：任何异常一律按"不可用"处理，由诊断文本说明原因。
            return false;
        }
#else
        // 未装配SDK的构建里根本没有采集实现，必须报告不可用而不是静默通过。
        return false;
#endif
    }

    /// <summary>原生库解析探测所需的入口；不使用托管 <c>NativeLibrary</c> 以保持 net48 与 net8.0 行为一致。</summary>
    private static class NativeMethods
    {
        [DllImport("kernel32", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibraryW(string fileName);
    }
}
