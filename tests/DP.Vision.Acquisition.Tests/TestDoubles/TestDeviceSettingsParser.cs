using System;
using System.Text.Json;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 测试用设备配置解析器：扁平 deviceSettings，仅读取 serialNumber。
/// bindingId=test:serial、resourceKey=camera:serial:xxx、summary=serialNumber=xxx。
/// 不解释其他字段，模拟Plugin私有解析；与厂商解析器同契约但独立实现。
/// </summary>
public static class TestDeviceSettingsParser
{
    /// <summary>解析测试deviceSettings。</summary>
    /// <param name="settingsJson">原始JSON文本；空或缺少serialNumber直接失败。</param>
    public static VisionDeviceSettingsParseResult Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            throw new VisionSourceConfigurationException("测试deviceSettings不能为空。");

        // net48 的引用程序集没有 IsNullOrWhiteSpace 的 [NotNullWhen(false)] 标注，这里显式断言非空以保持零警告。
        using var document = JsonDocument.Parse(settingsJson!);
        if (!document.RootElement.TryGetProperty("serialNumber", out var serial) ||
            string.IsNullOrWhiteSpace(serial.GetString()))
            throw new VisionSourceConfigurationException("测试deviceSettings缺少serialNumber。");

        var value = serial.GetString()!.Trim();
        return new VisionDeviceSettingsParseResult(
            providerBindingId: "test:serial:" + value,
            resourceKey: "camera:serial:" + value,
            configurationSummary: "serialNumber=" + value);
    }
}
