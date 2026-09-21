using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// Basler面阵Type的deviceSettings解析器。机器配置的deviceSettings是单设备扁平对象，
/// 字段含义、校验和诊断都由本Provider拥有；未知字段一律拒绝，避免拼写错误被静默忽略。
/// 解析结果生成内部绑定身份、规范ResourceKey与进入CompositionId的配置摘要。
/// </summary>
public static class BaslerDeviceSettingsParser
{
    private const string SerialNumberKey = "serialNumber";
    private const string UserDefinedNameKey = "userDefinedName";
    private const string TriggerSourceKey = "triggerSource";
    private const string PixelFormatKey = "pixelFormat";

    /// <summary>解析并校验单设备deviceSettings。</summary>
    /// <param name="json">deviceSettings JSON文本；为空表示尚未配置设备。</param>
    /// <returns>验证后的内部绑定信息。</returns>
    /// <exception cref="VisionSourceConfigurationException">配置不是JSON对象、含未知字段或选择器不唯一。</exception>
    public static VisionDeviceSettingsParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new VisionSourceConfigurationException(
                "Basler 相机缺少 deviceSettings；请配置 serialNumber 或 userDefinedName 之一。");

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
                "Basler 相机 deviceSettings 不是有效JSON：" + exception.Message, exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VisionSourceConfigurationException(
                    "Basler 相机 deviceSettings 必须是JSON对象，形如 {\"serialNumber\":\"40123456\"}。");

            string? serialNumber = null;
            string? userDefinedName = null;
            string? triggerSource = null;
            string? pixelFormat = null;
            foreach (var field in root.EnumerateObject())
            {
                switch (field.Name)
                {
                    case SerialNumberKey:
                        serialNumber = ReadText(field);
                        break;
                    case UserDefinedNameKey:
                        userDefinedName = ReadText(field);
                        break;
                    case TriggerSourceKey:
                        triggerSource = ReadText(field);
                        break;
                    case PixelFormatKey:
                        pixelFormat = ReadText(field);
                        break;
                    default:
                        throw new VisionSourceConfigurationException(
                            $"Basler 相机 deviceSettings 含未知字段 {field.Name}；"
                            + $"只支持 {SerialNumberKey}、{UserDefinedNameKey}、{TriggerSourceKey}、{PixelFormatKey}。");
                }
            }

            var hasSerial = !string.IsNullOrWhiteSpace(serialNumber);
            var hasName = !string.IsNullOrWhiteSpace(userDefinedName);
            if (hasSerial == hasName)
            {
                throw new VisionSourceConfigurationException(
                    "Basler 相机必须且只能指定 serialNumber 或 userDefinedName 之一："
                    + "同时给出无法判断以哪个为准，都不给出则会匹配到任意一台设备。");
            }

            var selector = hasSerial ? serialNumber! : userDefinedName!;
            var resourceKey = hasSerial
                ? "camera:serial:" + serialNumber
                : "camera:name:" + userDefinedName;
            return new VisionDeviceSettingsParseResult(
                selector,
                resourceKey,
                BuildSummary(pixelFormat, serialNumber, triggerSource, userDefinedName));
        }
    }

    /// <summary>确定性摘要；字段按序数排序并只含已出现的字段，进入CompositionId。</summary>
    private static string BuildSummary(
        string? pixelFormat,
        string? serialNumber,
        string? triggerSource,
        string? userDefinedName)
    {
        var entries = new List<string>();
        if (pixelFormat is not null) entries.Add(PixelFormatKey + "=" + pixelFormat);
        if (serialNumber is not null) entries.Add(SerialNumberKey + "=" + serialNumber);
        if (triggerSource is not null) entries.Add(TriggerSourceKey + "=" + triggerSource);
        if (userDefinedName is not null) entries.Add(UserDefinedNameKey + "=" + userDefinedName);
        return string.Join(";", entries.OrderBy(item => item, StringComparer.Ordinal));
    }

    private static string ReadText(JsonProperty field)
    {
        if (field.Value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (field.Value.ValueKind != JsonValueKind.String)
            throw new VisionSourceConfigurationException($"Basler 相机 deviceSettings 的 {field.Name} 必须是字符串。");
        return field.Value.GetString() ?? string.Empty;
    }
}
