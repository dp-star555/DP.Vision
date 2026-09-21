using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON面阵Type的deviceSettings解析器。机器配置的deviceSettings是单设备扁平对象，
/// 字段含义、校验和诊断都由本Provider拥有；未知字段一律拒绝，避免拼写错误被静默忽略。
/// 解析结果生成内部绑定身份、规范ResourceKey与进入CompositionId的配置摘要。
/// </summary>
public static class HalconDeviceSettingsParser
{
    private const string InterfaceNameKey = "interfaceName";
    private const string DeviceNameKey = "deviceName";
    private const string SerialNumberKey = "serialNumber";
    private const string TriggerSourceKey = "triggerSource";
    private const string GrabTimeoutMillisecondsKey = "grabTimeoutMilliseconds";

    /// <summary>解析并校验单设备deviceSettings。</summary>
    /// <param name="json">deviceSettings JSON文本；为空表示尚未配置设备。</param>
    /// <returns>验证后的内部绑定信息。</returns>
    /// <exception cref="VisionSourceConfigurationException">配置不是JSON对象、含未知字段或缺少必填字段。</exception>
    public static VisionDeviceSettingsParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new VisionSourceConfigurationException(
                "HALCON 相机缺少 deviceSettings；请配置 interfaceName 与 deviceName。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json!, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException exception)
        {
            throw new VisionSourceConfigurationException(
                "HALCON 相机 deviceSettings 不是有效JSON：" + exception.Message, exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VisionSourceConfigurationException(
                    "HALCON 相机 deviceSettings 必须是JSON对象，形如 {\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\"}。");

            string? interfaceName = null;
            string? deviceName = null;
            string? serialNumber = null;
            string? triggerSource = null;
            var grabTimeoutMilliseconds = HalconAcquisitionBinding.DefaultGrabTimeoutMilliseconds;
            foreach (var field in root.EnumerateObject())
            {
                switch (field.Name)
                {
                    case InterfaceNameKey:
                        interfaceName = ReadText(field);
                        break;
                    case DeviceNameKey:
                        deviceName = ReadText(field);
                        break;
                    case SerialNumberKey:
                        serialNumber = ReadText(field);
                        break;
                    case TriggerSourceKey:
                        triggerSource = ReadText(field);
                        break;
                    case GrabTimeoutMillisecondsKey:
                        grabTimeoutMilliseconds = ReadPositiveInt(field);
                        break;
                    default:
                        throw new VisionSourceConfigurationException(
                            $"HALCON 相机 deviceSettings 含未知字段 {field.Name}；"
                            + $"只支持 {InterfaceNameKey}、{DeviceNameKey}、{SerialNumberKey}、"
                            + $"{TriggerSourceKey}、{GrabTimeoutMillisecondsKey}。");
                }
            }

            if (string.IsNullOrWhiteSpace(interfaceName))
                throw new VisionSourceConfigurationException($"HALCON 相机 deviceSettings 缺少或留空了 {InterfaceNameKey}。");
            if (string.IsNullOrWhiteSpace(deviceName))
                throw new VisionSourceConfigurationException($"HALCON 相机 deviceSettings 缺少或留空了 {DeviceNameKey}。");

            var hasSerial = !string.IsNullOrWhiteSpace(serialNumber);
            var bindingId = hasSerial ? serialNumber! : interfaceName + "|" + deviceName;
            var resourceKey = hasSerial
                ? "camera:serial:" + serialNumber
                : "halcon:camera:" + interfaceName + "|" + deviceName;
            return new VisionDeviceSettingsParseResult(
                bindingId,
                resourceKey,
                BuildSummary(interfaceName, deviceName, serialNumber, triggerSource, grabTimeoutMilliseconds));
        }
    }

    /// <summary>确定性摘要；字段按序数排序，抓取超时使用生效值，进入CompositionId。</summary>
    private static string BuildSummary(
        string interfaceName,
        string deviceName,
        string? serialNumber,
        string? triggerSource,
        int grabTimeoutMilliseconds)
    {
        var entries = new List<string>
        {
            DeviceNameKey + "=" + deviceName,
            GrabTimeoutMillisecondsKey + "=" + grabTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
            InterfaceNameKey + "=" + interfaceName
        };
        if (serialNumber is not null) entries.Add(SerialNumberKey + "=" + serialNumber);
        if (triggerSource is not null) entries.Add(TriggerSourceKey + "=" + triggerSource);
        return string.Join(";", entries.OrderBy(item => item, StringComparer.Ordinal));
    }

    private static string ReadText(JsonProperty field)
    {
        if (field.Value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (field.Value.ValueKind != JsonValueKind.String)
            throw new VisionSourceConfigurationException($"HALCON 相机 deviceSettings 的 {field.Name} 必须是字符串。");
        return field.Value.GetString() ?? string.Empty;
    }

    private static int ReadPositiveInt(JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var value))
            throw new VisionSourceConfigurationException(
                $"HALCON 相机 deviceSettings 的 {field.Name} 必须是整数。");
        if (value < 1)
            throw new VisionSourceConfigurationException(
                $"HALCON 相机 deviceSettings 的 {field.Name} 必须为正；无限等待会让停止无法收敛。");
        return value;
    }
}
