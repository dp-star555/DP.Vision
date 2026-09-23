using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DP.Vision.Acquisition;

/// <summary>
/// 机器相机配置解析器。公共层只解释sourceId、acquisitionType、连接/取流策略与Inbox限制；
/// deviceSettings以原始JSON保留，由对应Plugin解析。未知公共字段一律拒绝，避免拼写错误被静默忽略。
/// 配置根既可以是单个相机对象，也可以是相机数组。
/// </summary>
public static class VisionAcquisitionMachineConfigurationParser
{
    private const string SourceIdKey = "sourceId";
    private const string AcquisitionTypeKey = "acquisitionType";
    private const string SettingsVersionKey = "settingsVersion";
    private const string RequiredKey = "required";
    private const string ConnectionKey = "connection";
    private const string InboxKey = "inbox";
    private const string DeviceSettingsKey = "deviceSettings";

    private const string OpenOnApplicationStartKey = "openOnApplicationStart";
    private const string TransferStartKey = "transferStart";

    private const string CapacityKey = "capacity";
    private const string ByteBudgetKey = "byteBudget";
    private const string MaximumFrameAgeMillisecondsKey = "maximumFrameAgeMilliseconds";

    /// <summary>解析机器相机配置文本。</summary>
    /// <param name="json">机器相机配置JSON；根为单个相机对象或相机数组。</param>
    /// <returns>版本化相机定义列表。</returns>
    /// <exception cref="ArgumentNullException">配置为空。</exception>
    /// <exception cref="VisionSourceConfigurationException">配置不是有效JSON、含未知字段或必填字段缺失/非法。</exception>
    public static IReadOnlyList<VisionAcquisitionCameraDefinition> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("机器相机配置不能为空。", nameof(json));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException exception)
        {
            throw new VisionSourceConfigurationException(
                "机器相机配置不是有效JSON：" + exception.Message, exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return new[] { ReadCamera(root) };
            if (root.ValueKind != JsonValueKind.Array)
                throw new VisionSourceConfigurationException(
                    "机器相机配置根必须是单个相机对象或相机数组。");

            var cameras = new List<VisionAcquisitionCameraDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in root.EnumerateArray())
            {
                var camera = ReadCamera(element);
                if (!seen.Add(camera.SourceId))
                    throw new VisionSourceConfigurationException($"逻辑源身份重复：{camera.SourceId}。");
                cameras.Add(camera);
            }

            return cameras;
        }
    }

    private static VisionAcquisitionCameraDefinition ReadCamera(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new VisionSourceConfigurationException("相机定义必须是JSON对象。");

        string? sourceId = null;
        string? acquisitionType = null;
        int settingsVersion = 0;
        var isRequired = false;
        VisionAcquisitionConnectionPolicy? connection = null;
        VisionFrameInboxPolicy? inbox = null;
        string? deviceSettingsJson = null;
        var hasSettingsVersion = false;

        foreach (var field in element.EnumerateObject())
        {
            switch (field.Name)
            {
                case SourceIdKey:
                    sourceId = ReadRequiredText("相机定义", field);
                    break;
                case AcquisitionTypeKey:
                    acquisitionType = ReadRequiredText("相机定义", field);
                    break;
                case SettingsVersionKey:
                    settingsVersion = ReadPositiveInt("相机定义", field);
                    hasSettingsVersion = true;
                    break;
                case RequiredKey:
                    isRequired = ReadBool("相机定义", field);
                    break;
                case ConnectionKey:
                    connection = ReadConnection(field);
                    break;
                case InboxKey:
                    inbox = ReadInbox(field);
                    break;
                case DeviceSettingsKey:
                    if (field.Value.ValueKind == JsonValueKind.Null)
                    {
                        deviceSettingsJson = null;
                    }
                    else if (field.Value.ValueKind == JsonValueKind.Object)
                    {
                        // 原样保留给Plugin解析；公共层不解释其字段，也不要求其规范形态。
                        deviceSettingsJson = field.Value.GetRawText();
                    }
                    else
                    {
                        throw new VisionSourceConfigurationException(
                            $"相机定义的 {DeviceSettingsKey} 必须是JSON对象或null。");
                    }

                    break;
                default:
                    throw new VisionSourceConfigurationException(
                        $"相机定义含未知字段 {field.Name}；只支持 {SourceIdKey}、{AcquisitionTypeKey}、"
                        + $"{SettingsVersionKey}、{RequiredKey}、{ConnectionKey}、{InboxKey}、{DeviceSettingsKey}。");
            }
        }

        if (string.IsNullOrWhiteSpace(sourceId))
            throw new VisionSourceConfigurationException($"相机定义缺少必填字段 {SourceIdKey}。");
        if (string.IsNullOrWhiteSpace(acquisitionType))
            throw new VisionSourceConfigurationException($"相机定义缺少必填字段 {AcquisitionTypeKey}。");
        if (!hasSettingsVersion)
            throw new VisionSourceConfigurationException($"相机定义缺少必填字段 {SettingsVersionKey}。");

        try
        {
            return new VisionAcquisitionCameraDefinition(
                sourceId!,
                acquisitionType!,
                settingsVersion,
                isRequired,
                connection ?? new VisionAcquisitionConnectionPolicy(),
                inbox,
                deviceSettingsJson);
        }
        catch (ArgumentException exception)
        {
            throw new VisionSourceConfigurationException(
                $"相机定义 {sourceId} 无效：{exception.Message}", exception);
        }
    }

    private static VisionAcquisitionConnectionPolicy ReadConnection(JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.Object)
            throw new VisionSourceConfigurationException($"相机定义的 {ConnectionKey} 必须是JSON对象。");

        var openOnApplicationStart = true;
        var transferStart = EVisionAcquisitionTransferStart.PerRequest;
        foreach (var item in field.Value.EnumerateObject())
        {
            switch (item.Name)
            {
                case OpenOnApplicationStartKey:
                    openOnApplicationStart = ReadBool($"{ConnectionKey}", item);
                    break;
                case TransferStartKey:
                    transferStart = ReadTransferStart(item);
                    break;
                default:
                    throw new VisionSourceConfigurationException(
                        $"相机定义的 {ConnectionKey} 含未知字段 {item.Name}；"
                        + $"只支持 {OpenOnApplicationStartKey}、{TransferStartKey}。");
            }
        }

        return new VisionAcquisitionConnectionPolicy(openOnApplicationStart, transferStart);
    }

    private static EVisionAcquisitionTransferStart ReadTransferStart(JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.String)
            throw new VisionSourceConfigurationException(
                $"相机定义的 {ConnectionKey}.{TransferStartKey} 必须是字符串。");
        var text = field.Value.GetString();
        if (string.Equals(text, "PerRequest", StringComparison.Ordinal))
            return EVisionAcquisitionTransferStart.PerRequest;
        if (string.Equals(text, "OnConnect", StringComparison.Ordinal))
            return EVisionAcquisitionTransferStart.OnConnect;
        throw new VisionSourceConfigurationException(
            $"相机定义的 {ConnectionKey}.{TransferStartKey} 只支持 PerRequest 或 OnConnect；实际为 {text}。");
    }

    private static VisionFrameInboxPolicy? ReadInbox(JsonProperty field)
    {
        if (field.Value.ValueKind == JsonValueKind.Null)
            return null;
        if (field.Value.ValueKind != JsonValueKind.Object)
            throw new VisionSourceConfigurationException($"相机定义的 {InboxKey} 必须是JSON对象或null。");

        int? capacity = null;
        long? byteBudget = null;
        long? maximumFrameAgeMilliseconds = null;
        foreach (var item in field.Value.EnumerateObject())
        {
            switch (item.Name)
            {
                case CapacityKey:
                    capacity = ReadPositiveInt($"{InboxKey}.{CapacityKey}", item);
                    break;
                case ByteBudgetKey:
                    byteBudget = ReadPositiveLong($"{InboxKey}.{ByteBudgetKey}", item);
                    break;
                case MaximumFrameAgeMillisecondsKey:
                    maximumFrameAgeMilliseconds = ReadPositiveLong($"{InboxKey}.{MaximumFrameAgeMillisecondsKey}", item);
                    break;
                default:
                    throw new VisionSourceConfigurationException(
                        $"相机定义的 {InboxKey} 含未知字段 {item.Name}；"
                        + $"只支持 {CapacityKey}、{ByteBudgetKey}、{MaximumFrameAgeMillisecondsKey}。");
            }
        }

        if (capacity is null || byteBudget is null || maximumFrameAgeMilliseconds is null)
            throw new VisionSourceConfigurationException(
                $"相机定义的 {InboxKey} 必须完整声明 {CapacityKey}、{ByteBudgetKey}、{MaximumFrameAgeMillisecondsKey}。");

        return new VisionFrameInboxPolicy(
            capacity.Value,
            byteBudget.Value,
            TimeSpan.FromMilliseconds(maximumFrameAgeMilliseconds.Value));
    }

    private static string ReadRequiredText(string owner, JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.String)
            throw new VisionSourceConfigurationException($"{owner} 的 {field.Name} 必须是字符串。");
        var text = field.Value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new VisionSourceConfigurationException($"{owner} 的 {field.Name} 不能为空。");
        return text!;
    }

    private static bool ReadBool(string owner, JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.True && field.Value.ValueKind != JsonValueKind.False)
            throw new VisionSourceConfigurationException($"{owner} 的 {field.Name} 必须是布尔值。");
        return field.Value.GetBoolean();
    }

    private static int ReadPositiveInt(string owner, JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var value))
            throw new VisionSourceConfigurationException($"{owner} 的 {field.Name} 必须是整数。");
        if (value < 1)
            throw new VisionSourceConfigurationException($"{owner} 的 {field.Name} 必须为正。");
        return value;
    }

    private static long ReadPositiveLong(string owner, JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out var value))
            throw new VisionSourceConfigurationException($"{owner} 的 {field.Name} 必须是整数。");
        if (value < 1)
            throw new VisionSourceConfigurationException($"{owner} 的 {field.Name} 必须为正。");
        return value;
    }
}
