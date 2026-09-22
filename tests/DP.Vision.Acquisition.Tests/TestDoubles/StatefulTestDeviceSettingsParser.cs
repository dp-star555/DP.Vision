using System;
using System.Text.Json;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 测试用"插件私有绑定对象"。公共层不该解释它，只该原样转交，
/// 因此这里用一个测试程序集独有的类型来证明：Provider 收到的就是解析器造出来的那一个。
/// </summary>
/// <param name="SerialNumber">从 deviceSettings 解析出的序列号。</param>
internal sealed record TestDeviceBinding(string SerialNumber);

/// <summary>
/// 带私有状态的测试设备配置解析器：与 <see cref="TestDeviceSettingsParser"/> 同契约，
/// 但额外把解析结果装进 <c>ProviderState</c>，用于验证
/// "deviceSettings → 解析 → 私有绑定 → Provider.OpenAsync" 这条链路真的接通。
/// </summary>
public static class StatefulTestDeviceSettingsParser
{
    /// <summary>解析测试deviceSettings并把解析结果作为插件私有状态带出。</summary>
    /// <param name="settingsJson">原始JSON文本；空或缺少serialNumber直接失败。</param>
    /// <returns>携带 <see cref="TestDeviceBinding"/> 的解析结果。</returns>
    /// <exception cref="VisionSourceConfigurationException">配置为空或缺少 serialNumber。</exception>
    public static VisionDeviceSettingsParseResult Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            throw new VisionSourceConfigurationException("测试deviceSettings不能为空。");

        // net48 的引用程序集没有 IsNullOrWhiteSpace 的 [NotNullWhen(false)] 标注，这里显式断言非空以保持零警告。
        using var document = JsonDocument.Parse(settingsJson!);
        if (!document.RootElement.TryGetProperty("serialNumber", out var serial)
            || string.IsNullOrWhiteSpace(serial.GetString()))
            throw new VisionSourceConfigurationException("测试deviceSettings缺少serialNumber。");

        var value = serial.GetString()!.Trim();
        return new VisionDeviceSettingsParseResult(
            providerBindingId: "flow:serial:" + value,
            resourceKey: "camera:serial:" + value,
            configurationSummary: "serialNumber=" + value,
            providerState: new TestDeviceBinding(value));
    }
}
