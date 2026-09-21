using System;
using System.Collections.Generic;
using System.Text.Json;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// HALCON Provider私有配置。公共层只负责把配置文本原样交给本Provider，
/// 字段含义、校验和诊断都由这里拥有；未知字段一律拒绝，避免拼写错误被静默忽略。
/// </summary>
public static class HalconProviderConfiguration
{
    private const string BindingsKey = "bindings";
    private const string InterfaceNameKey = "interfaceName";
    private const string DeviceNameKey = "deviceName";
    private const string SerialNumberKey = "serialNumber";
    private const string TriggerSourceKey = "triggerSource";
    private const string GrabTimeoutKey = "grabTimeoutMilliseconds";

    /// <summary>解析并校验私有配置文本。</summary>
    /// <param name="configuration">私有配置JSON文本；为空表示本Provider尚未配置任何设备绑定。</param>
    /// <returns>Provider私有设备绑定。</returns>
    /// <exception cref="VisionSourceConfigurationException">配置不是JSON对象、含未知字段或缺少必填字段。</exception>
    public static IReadOnlyList<HalconAcquisitionBinding> ParseBindings(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
            return Array.Empty<HalconAcquisitionBinding>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(configuration!, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException exception)
        {
            throw new VisionSourceConfigurationException(
                "HALCON Provider 私有配置不是有效JSON：" + exception.Message, exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VisionSourceConfigurationException(
                    "HALCON Provider 私有配置必须是JSON对象，形如 {\"bindings\":{\"top-camera\":{...}}}。");

            var bindings = new List<HalconAcquisitionBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, BindingsKey, StringComparison.Ordinal))
                    throw new VisionSourceConfigurationException(
                        $"HALCON Provider 私有配置含未知字段 {property.Name}；只支持 {BindingsKey}。");
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new VisionSourceConfigurationException(
                        $"HALCON Provider 私有配置的 {BindingsKey} 必须是JSON对象，键为绑定身份。");

                foreach (var binding in property.Value.EnumerateObject())
                {
                    var bindingId = binding.Name.Trim();
                    if (bindingId.Length == 0)
                        throw new VisionSourceConfigurationException("HALCON Provider 私有配置包含空绑定身份。");
                    if (!seen.Add(bindingId))
                        throw new VisionSourceConfigurationException(
                            $"HALCON Provider 私有配置中绑定身份重复：{bindingId}。");
                    bindings.Add(ReadBinding(bindingId, binding.Value));
                }
            }

            return bindings;
        }
    }

    private static HalconAcquisitionBinding ReadBinding(string bindingId, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new VisionSourceConfigurationException($"HALCON Provider 绑定 {bindingId} 必须是JSON对象。");

        string? interfaceName = null;
        string? deviceName = null;
        string? serialNumber = null;
        string? triggerSource = null;
        var grabTimeout = HalconAcquisitionBinding.DefaultGrabTimeoutMilliseconds;
        foreach (var field in element.EnumerateObject())
        {
            switch (field.Name)
            {
                case InterfaceNameKey:
                    interfaceName = ReadText(bindingId, field);
                    break;
                case DeviceNameKey:
                    deviceName = ReadText(bindingId, field);
                    break;
                case SerialNumberKey:
                    serialNumber = ReadText(bindingId, field);
                    break;
                case TriggerSourceKey:
                    triggerSource = ReadText(bindingId, field);
                    break;
                case GrabTimeoutKey:
                    grabTimeout = ReadPositiveInt(bindingId, field);
                    break;
                default:
                    throw new VisionSourceConfigurationException(
                        $"HALCON Provider 绑定 {bindingId} 含未知字段 {field.Name}；"
                        + $"只支持 {InterfaceNameKey}、{DeviceNameKey}、{SerialNumberKey}、"
                        + $"{TriggerSourceKey}、{GrabTimeoutKey}。");
            }
        }

        if (string.IsNullOrWhiteSpace(interfaceName))
            throw new VisionSourceConfigurationException($"HALCON Provider 绑定 {bindingId} 缺少或留空了 {InterfaceNameKey}。");
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new VisionSourceConfigurationException($"HALCON Provider 绑定 {bindingId} 缺少或留空了 {DeviceNameKey}。");
        return new HalconAcquisitionBinding(bindingId, interfaceName!, deviceName!, serialNumber, triggerSource, grabTimeout);
    }

    private static int ReadPositiveInt(string bindingId, JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var value))
            throw new VisionSourceConfigurationException(
                $"HALCON Provider 绑定 {bindingId} 的 {field.Name} 必须是整数。");
        if (value < 1)
            throw new VisionSourceConfigurationException(
                $"HALCON Provider 绑定 {bindingId} 的 {field.Name} 必须为正；无限等待会让停止无法收敛。");
        return value;
    }

    private static string? ReadText(string bindingId, JsonProperty field)
    {
        if (field.Value.ValueKind == JsonValueKind.Null)
            return null;
        if (field.Value.ValueKind != JsonValueKind.String)
            throw new VisionSourceConfigurationException(
                $"HALCON Provider 绑定 {bindingId} 的 {field.Name} 必须是字符串。");
        var text = field.Value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new VisionSourceConfigurationException($"HALCON Provider 绑定 {bindingId} 的 {field.Name} 不能为空。");
        return text;
    }
}
