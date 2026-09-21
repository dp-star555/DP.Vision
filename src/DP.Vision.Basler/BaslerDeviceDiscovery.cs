using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.Acquisition;
#if BASLER_SDK
using Basler.Pylon;
#endif

namespace DP.Vision.Basler;

/// <summary>一台 pylon 枚举到的相机；只保留生成候选绑定所需的字段。</summary>
/// <param name="SerialNumber">相机序列号；pylon 未报告时为空。</param>
/// <param name="UserDefinedName">相机用户自定义名；未设置时为空。</param>
/// <param name="ModelName">型号名称。</param>
/// <param name="VendorName">厂商名称。</param>
/// <param name="FriendlyName">pylon 的友好显示名。</param>
internal sealed record BaslerDiscoveredCamera(
    string? SerialNumber,
    string? UserDefinedName,
    string? ModelName,
    string? VendorName,
    string? FriendlyName);

/// <summary>
/// 枚举结果到公共候选描述的映射。
/// <para>
/// 这里刻意复用 <see cref="BaslerDeviceSettingsParser"/> 的选择器与资源键规则：
/// "发现得到的绑定"和"手工填写的 deviceSettings"必须产出同一个 ResourceKey，
/// 否则操作员从界面选一台相机、再手工核对配置时会看到两个不同的键，无法判断是不是同一台设备。
/// </para>
/// </summary>
internal static class BaslerDeviceDiscovery
{
    /// <summary>把枚举结果映射为候选描述，并按规范资源键排序。</summary>
    /// <param name="cameras">pylon 枚举结果。</param>
    /// <returns>顺序确定的候选描述；无法生成稳定绑定的相机被跳过。</returns>
    /// <exception cref="ArgumentNullException">枚举结果为空。</exception>
    public static IReadOnlyList<VisionDeviceDescriptor> ToDescriptors(IEnumerable<BaslerDiscoveredCamera> cameras)
    {
        if (cameras is null)
            throw new ArgumentNullException(nameof(cameras));

        var descriptors = new List<VisionDeviceDescriptor>();
        foreach (var camera in cameras)
        {
            if (camera is null)
                continue;

            var serial = Normalize(camera.SerialNumber);
            var name = Normalize(camera.UserDefinedName);

            // 序列号与自定义名都拿不到时无法生成稳定绑定：跳过这台相机，
            // 而不是回退成"按顺序第几台"这种换个上电顺序就变的身份。
            if (serial is null && name is null)
                continue;

            var bindingId = serial ?? name!;
            descriptors.Add(new VisionDeviceDescriptor(
                BaslerAcquisitionProvider.ProviderIdentity,
                bindingId,
                serial is not null ? "camera:serial:" + serial : "camera:name:" + name,
                Normalize(camera.FriendlyName) ?? Normalize(camera.ModelName) ?? bindingId,
                Normalize(camera.VendorName),
                Normalize(camera.ModelName),
                serial));
        }

        // 排序键取规范资源键：同一台设备在任何一次枚举里都落在同一位置，界面不会因枚举顺序抖动。
        return descriptors
            .OrderBy(item => item.CanonicalKey, StringComparer.Ordinal)
            .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
}

/// <summary>真实设备枚举；未装配 pylon 或运行时不可用时明确失败，不返回空列表假装现场没有设备。</summary>
internal static class BaslerDeviceEnumeration
{
    /// <summary>枚举当前可见的 pylon 相机。</summary>
    /// <returns>原始枚举结果，未做去重与排序。</returns>
    /// <exception cref="VisionProviderUnavailableException">本程序集未装配 pylon 支持，或进程解析不到 pylon 原生运行时。</exception>
    public static IReadOnlyList<BaslerDiscoveredCamera> Enumerate()
    {
#if BASLER_SDK
        // 托管程序集随 NuGet 还原，编译期必然存在；缺的是原生运行时。
        // 此时调用 pylon 会抛原生异常，因此先做可判定探测，给出能照着做的诊断。
        if (!BaslerPylonRuntime.IsDeployed)
        {
            throw new VisionProviderUnavailableException(
                BaslerAcquisitionProvider.ProviderIdentity,
                BaslerPylonRuntime.DescribeMissingRuntime());
        }

        var found = new List<BaslerDiscoveredCamera>();
        foreach (var info in CameraFinder.Enumerate())
        {
            found.Add(new BaslerDiscoveredCamera(
                Read(info, CameraInfoKey.SerialNumber),
                Read(info, CameraInfoKey.UserDefinedName),
                Read(info, CameraInfoKey.ModelName),
                Read(info, CameraInfoKey.VendorName),
                Read(info, CameraInfoKey.FriendlyName)));
        }

        return found;
#else
        throw new VisionProviderUnavailableException(
            BaslerAcquisitionProvider.ProviderIdentity,
            "本程序集未装配 Basler pylon 支持（BASLER_SDK 未定义）；请以默认配置重新构建 DP.Vision.Basler。");
#endif
    }

#if BASLER_SDK
    /// <summary>读取相机信息字段；不是每台相机、每种传输层都提供全部键，缺失按空处理。</summary>
    private static string? Read(ICameraInfo info, string key) =>
        info.ContainsKey(key) ? info[key] : null;
#endif
}