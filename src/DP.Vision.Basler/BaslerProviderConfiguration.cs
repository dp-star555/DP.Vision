using System;
using System.Collections.Generic;
using System.Text.Json;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// Basler Provider私有配置。公共层只负责把配置文本原样交给本Provider，
/// 字段含义、校验和诊断都由这里拥有；未知字段一律拒绝，避免拼写错误被静默忽略。
/// 本类型刻意不引用 Basler.Pylon，使绑定与配置在未装配SDK的构建下同样可用、可测。
/// </summary>
public static class BaslerProviderConfiguration
{
    private const string BindingsKey = "bindings";
    private const string SerialNumberKey = "serialNumber";
    private const string UserDefinedNameKey = "userDefinedName";
    private const string TriggerSourceKey = "triggerSource";

    /// <summary>解析并校验私有配置文本。</summary>
    /// <param name="configuration">私有配置JSON文本；为空表示本Provider尚未配置任何设备绑定。</param>
    /// <returns>Provider私有设备绑定。</returns>
    /// <exception cref="VisionSourceConfigurationException">配置不是JSON对象、含未知字段、缺少必填字段或选择器不唯一。</exception>
    public static IReadOnlyList<BaslerAcquisitionBinding> ParseBindings(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
            return Array.Empty<BaslerAcquisitionBinding>();

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
                "Basler Provider 私有配置不是有效JSON：" + exception.Message, exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VisionSourceConfigurationException(
                    "Basler Provider 私有配置必须是JSON对象，形如 {\"bindings\":{\"top-camera\":{\"serialNumber\":\"40123456\"}}}。");

            var bindings = new List<BaslerAcquisitionBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, BindingsKey, StringComparison.Ordinal))
                    throw new VisionSourceConfigurationException(
                        $"Basler Provider 私有配置含未知字段 {property.Name}；只支持 {BindingsKey}。");
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new VisionSourceConfigurationException(
                        $"Basler Provider 私有配置的 {BindingsKey} 必须是JSON对象，键为绑定身份。");

                foreach (var binding in property.Value.EnumerateObject())
                {
                    var bindingId = binding.Name.Trim();
                    if (bindingId.Length == 0)
                        throw new VisionSourceConfigurationException("Basler Provider 私有配置包含空绑定身份。");
                    if (!seen.Add(bindingId))
                        throw new VisionSourceConfigurationException(
                            $"Basler Provider 私有配置中绑定身份重复：{bindingId}。");
                    bindings.Add(ReadBinding(bindingId, binding.Value));
                }
            }

            return bindings;
        }
    }

    private static BaslerAcquisitionBinding ReadBinding(string bindingId, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new VisionSourceConfigurationException($"Basler Provider 绑定 {bindingId} 必须是JSON对象。");

        string? serialNumber = null;
        string? userDefinedName = null;
        string? triggerSource = null;
        foreach (var field in element.EnumerateObject())
        {
            switch (field.Name)
            {
                case SerialNumberKey:
                    serialNumber = ReadText(bindingId, field);
                    break;
                case UserDefinedNameKey:
                    userDefinedName = ReadText(bindingId, field);
                    break;
                case TriggerSourceKey:
                    triggerSource = ReadText(bindingId, field);
                    break;
                default:
                    throw new VisionSourceConfigurationException(
                        $"Basler Provider 绑定 {bindingId} 含未知字段 {field.Name}；"
                        + $"只支持 {SerialNumberKey}、{UserDefinedNameKey}、{TriggerSourceKey}。");
            }
        }

        try
        {
            return new BaslerAcquisitionBinding(bindingId, serialNumber, userDefinedName, triggerSource);
        }
        catch (ArgumentException exception)
        {
            throw new VisionSourceConfigurationException(
                $"Basler Provider 绑定 {bindingId} 无效：{exception.Message}", exception);
        }
    }

    private static string? ReadText(string bindingId, JsonProperty field)
    {
        if (field.Value.ValueKind == JsonValueKind.Null)
            return null;
        if (field.Value.ValueKind != JsonValueKind.String)
            throw new VisionSourceConfigurationException(
                $"Basler Provider 绑定 {bindingId} 的 {field.Name} 必须是字符串。");
        var text = field.Value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new VisionSourceConfigurationException($"Basler Provider 绑定 {bindingId} 的 {field.Name} 不能为空。");
        return text;
    }
}
