using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.Acquisition;
#if HALCON_SDK
using HalconDotNet;
#endif

namespace DP.Vision.Halcon;

/// <summary>一台 HALCON 采集接口报告的候选设备。</summary>
/// <param name="InterfaceName">HALCON 采集接口名，例如 GigEVision2。</param>
/// <param name="DeviceName">可直接写入 deviceSettings 的 deviceName；取自 <c>device:</c> 条目。</param>
/// <param name="SerialNumber">设备序列号；接口未报告时为空。</param>
/// <param name="UserDefinedName">设备用户自定义名；未设置时为空。</param>
/// <param name="ModelName">型号名称；接口未报告时为空。</param>
/// <param name="VendorName">厂商名称；接口未报告时为空。</param>
internal sealed record HalconDiscoveredDevice(
    string InterfaceName,
    string DeviceName,
    string? SerialNumber,
    string? UserDefinedName,
    string? ModelName,
    string? VendorName);

/// <summary>
/// <c>info_boards</c> 返回串的解析与候选映射。
/// <para>
/// HALCON 的格式是 <c>| token:value | token:value |</c>：条目以竖线分隔，每个条目形如 <c>token:value</c>。
/// 权威说明见 HALCON 参考手册 Acquisition 章节：整串可直接作为 <c>open_framegrabber</c> 的 Device 参数，
/// 其中 <c>device:</c> 条目是最重要的一个，<c>device_sn:</c> 是序列号，<c>user_name:</c> 是 DeviceUserID。
/// </para>
/// <para>
/// 映射规则刻意与 <see cref="HalconDeviceSettingsParser"/> 保持一致：发现产出的绑定身份与资源键
/// 必须与"手工填写 deviceSettings"完全一致，否则操作员从界面选一台相机后无法与配置对照。
/// </para>
/// </summary>
internal static class HalconBoardInfo
{
    private const string DeviceToken = "device";
    private const string SerialToken = "device_sn";
    private const string UserNameToken = "user_name";
    private const string ModelToken = "model";
    private const string VendorToken = "vendor";

    /// <summary>解析一台设备；无法确定设备名时返回空，由调用方跳过而不是伪造身份。</summary>
    /// <param name="rawLine">单个设备的原始串。</param>
    /// <param name="interfaceName">产生该设备的采集接口名。</param>
    /// <returns>解析结果；原始串或接口名为空时为空。</returns>
    public static HalconDiscoveredDevice? Parse(string? rawLine, string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(rawLine) || string.IsNullOrWhiteSpace(interfaceName))
            return null;

        string? deviceId = null;
        string? serial = null;
        string? user = null;
        string? model = null;
        string? vendor = null;

        // 用竖线而不是 " | " 切分：HALCON 的串首尾也可能带竖线，按空白切分会把空段与值纠缠在一起。
        foreach (var entry in rawLine!.Split('|'))
        {
            var text = entry.Trim();
            if (text.Length == 0)
                continue;

            // 只按第一个冒号切分：值本身可能含冒号（例如 IP 端口形式），不能按最后一个切。
            var separator = text.IndexOf(':');
            if (separator <= 0)
                continue;

            var token = text.Substring(0, separator).Trim();
            var value = Normalize(text.Substring(separator + 1));
            if (value is null)
                continue;

            switch (token)
            {
                case DeviceToken:
                    deviceId ??= value;
                    break;
                case SerialToken:
                    serial ??= value;
                    break;
                case UserNameToken:
                    user ??= value;
                    break;
                case ModelToken:
                    model ??= value;
                    break;
                case VendorToken:
                    vendor ??= value;
                    break;
                default:
                    // 其余条目（unique_name、device_ip、producer、interface 等）与候选身份无关，忽略。
                    break;
            }
        }

        // 有些接口只回一个裸设备串。文档明确说明"不含 token: 与竖线的纯字符串直接视为设备 ID"，
        // 因此这里保真整串，而不是宣告该设备不可用。
        var deviceName = deviceId ?? Normalize(rawLine);
        if (deviceName is null)
            return null;

        return new HalconDiscoveredDevice(interfaceName!.Trim(), deviceName, serial, user, model, vendor);
    }

    /// <summary>把解析结果映射为公共候选描述，并按规范资源键排序。</summary>
    /// <param name="devices">解析结果。</param>
    /// <returns>顺序确定的候选描述；同一资源键的重复报告会被去重。</returns>
    /// <exception cref="ArgumentNullException">解析结果为空。</exception>
    public static IReadOnlyList<VisionDeviceDescriptor> ToDescriptors(IEnumerable<HalconDiscoveredDevice> devices)
    {
        if (devices is null)
            throw new ArgumentNullException(nameof(devices));

        var byResourceKey = new Dictionary<string, VisionDeviceDescriptor>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (device is null)
                continue;

            var interfaceName = Normalize(device.InterfaceName);
            var deviceName = Normalize(device.DeviceName);
            if (interfaceName is null || deviceName is null)
                continue;

            var serial = Normalize(device.SerialNumber);
            var bindingId = serial ?? interfaceName + "|" + deviceName;
            var resourceKey = serial is not null
                ? "camera:serial:" + serial
                : "halcon:camera:" + interfaceName + "|" + deviceName;
            var descriptor = new VisionDeviceDescriptor(
                HalconAcquisitionProvider.ProviderIdentity,
                bindingId,
                resourceKey,
                Normalize(device.UserDefinedName) ?? deviceName,
                Normalize(device.VendorName),
                Normalize(device.ModelName),
                serial);

            // 同一台相机可能同时出现在 GigEVision2 与 GenICamTL 两个接口下：按资源键保留首个，
            // 否则界面会列出两个指向同一物理设备的候选。
            if (!byResourceKey.ContainsKey(resourceKey))
                byResourceKey.Add(resourceKey, descriptor);
        }

        return byResourceKey.Values
            .OrderBy(item => item.CanonicalKey, StringComparer.Ordinal)
            .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
}

/// <summary>真实设备枚举；未装配 HALCON SDK 时明确失败，不返回空列表假装现场没有设备。</summary>
internal static class HalconDeviceEnumeration
{
    /// <summary>
    /// 默认查询的采集接口：工业相机走这三个。
    /// <para>
    /// HALCON 没有"列出已安装采集接口"的查询，因此只能按名字逐个查询 <c>info_boards</c>；
    /// 未安装的接口会查询失败，由本类跳过，不影响其余接口。
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyList<string> DefaultInterfaceNames =
        new[] { "GigEVision2", "USB3Vision", "GenICamTL" };

    /// <summary>枚举给定采集接口下的候选设备。</summary>
    /// <param name="interfaceNames">要查询的接口名；为空时使用 <see cref="DefaultInterfaceNames"/>。</param>
    /// <returns>原始枚举结果，未做去重与排序。</returns>
    /// <exception cref="VisionProviderUnavailableException">本程序集未装配 SDK，或所有接口都查询失败。</exception>
    public static IReadOnlyList<HalconDiscoveredDevice> Enumerate(IReadOnlyList<string>? interfaceNames = null)
    {
#if HALCON_SDK
        var names = interfaceNames ?? DefaultInterfaceNames;
        var found = new List<HalconDiscoveredDevice>();
        var succeeded = 0;
        string? firstFailure = null;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            try
            {
                HOperatorSet.InfoFramegrabber(name, "info_boards", out _, out var boards);
                succeeded++;
                for (var index = 0; index < boards.Length; index++)
                {
                    var parsed = HalconBoardInfo.Parse(boards[index].S, name);
                    if (parsed is not null)
                        found.Add(parsed);
                }
            }
            catch (Exception exception)
            {
                // 未安装该接口时查询必然失败；一个接口不可用不代表整机没有相机。
                // 走既有分类是为了让许可证故障保留"查 license 文件"这类能照着做的说明，
                // 而不是把厂商原始错误文本直接甩给操作员。
                var classified = exception is HOperatorException operatorException
                    ? HalconStreamFaults.Classify(
                        operatorException.GetErrorCode(),
                        operatorException.GetErrorMessage(),
                        operatorException)
                    : exception;
                firstFailure ??= $"{name}: {classified.Message}";
            }
        }

        // 所有接口都查不通时必须报错：返回空列表会让界面把"接口不可用"显示成"现场没有相机"，
        // 操作员会去检查相机电源，而真正的问题在采集接口。
        if (succeeded == 0 && firstFailure is not null)
        {
            throw new VisionProviderUnavailableException(
                HalconAcquisitionProvider.ProviderIdentity,
                "HALCON 采集接口全部无法枚举设备（" + firstFailure + "）；"
                + "请确认 HALCON 已安装对应采集接口（GigEVision2 / USB3Vision / GenICamTL）并已获得许可证。");
        }

        return found;
#else
        throw new VisionProviderUnavailableException(
            HalconAcquisitionProvider.ProviderIdentity,
            "本程序集未装配 HALCON SDK（HALCON_SDK 未定义）；请安装 HALCON 运行时并以 HALCONROOT 或 HalconDotNetPath 重新构建 DP.Vision.Halcon。");
#endif
    }
}