using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// 版本化机器相机定义。公共层只解释 <see cref="SourceId"/>、<see cref="AcquisitionTypeId"/>、
/// 连接/取流策略与Inbox限制；<see cref="DeviceSettingsJson"/> 由对应Plugin解析，公共层不解释其字段。
/// 不要求人员填写ProviderBindingId或ResourceKey——Plugin验证配置后生成内部绑定与规范资源键。
/// </summary>
public sealed record VisionAcquisitionCameraDefinition
{
    /// <summary>本文档结构的格式版本；<see cref="SettingsVersion"/> 是设备设置契约版本，由AcquisitionType声明。</summary>
    public const int DocumentFormatVersion = 1;

    /// <summary>创建机器相机定义。</summary>
    /// <param name="sourceId">逻辑视觉源标识；工作流文档只保存它。</param>
    /// <param name="acquisitionTypeId">AcquisitionType稳定身份，例如dp.acquisition.basler.area；不包含CLR完整类型名。</param>
    /// <param name="settingsVersion">设备设置契约版本；必须与AcquisitionType声明的DeviceSettingsVersion一致。</param>
    /// <param name="isRequired">该Source是否必需；必需源在运行期就绪前必须成功打开。</param>
    /// <param name="connection">连接与取流策略。</param>
    /// <param name="inbox">外部回调缓冲的有界策略；PerRequest源必须为空。</param>
    /// <param name="deviceSettingsJson">Plugin私有设备设置原始JSON；为空表示尚未配置设备。</param>
    /// <exception cref="ArgumentException">任一身份为空、设置版本非法，或策略与缓冲策略不匹配。</exception>
    public VisionAcquisitionCameraDefinition(
        string sourceId,
        string acquisitionTypeId,
        int settingsVersion,
        bool isRequired,
        VisionAcquisitionConnectionPolicy connection,
        VisionFrameInboxPolicy? inbox,
        string? deviceSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("逻辑源标识不能为空。", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(acquisitionTypeId))
            throw new ArgumentException("AcquisitionType身份不能为空。", nameof(acquisitionTypeId));
        if (settingsVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(settingsVersion), settingsVersion, "设置版本必须为正整数。");
        if (connection is null)
            throw new ArgumentNullException(nameof(connection));

        // 模式与缓冲策略必须成对出现：OnConnect需要Inbox界定待领取队列，PerRequest不接受Inbox。
        if (connection.TransferStart == EVisionAcquisitionTransferStart.OnConnect)
        {
            if (inbox is null)
                throw new ArgumentException(
                    "OnConnect源必须声明待领取队列的容量、字节预算与最大帧龄；缺失时无法建立有界队列。",
                    nameof(inbox));
        }
        else if (inbox is not null)
        {
            throw new ArgumentException(
                "PerRequest源不接受待领取队列策略；请确认是否漏配了OnConnect。",
                nameof(inbox));
        }

        SourceId = sourceId.Trim();
        AcquisitionTypeId = acquisitionTypeId.Trim();
        SettingsVersion = settingsVersion;
        IsRequired = isRequired;
        Connection = connection;
        Inbox = inbox;
        DeviceSettingsJson = deviceSettingsJson;
    }

    /// <summary>逻辑视觉源标识；工作流文档只保存它。</summary>
    public string SourceId { get; }

    /// <summary>AcquisitionType稳定身份；不包含CLR完整类型名。</summary>
    public string AcquisitionTypeId { get; }

    /// <summary>设备设置契约版本；必须与AcquisitionType声明的DeviceSettingsVersion一致。</summary>
    public int SettingsVersion { get; }

    /// <summary>该Source是否必需；必需源在运行期就绪前必须成功打开。</summary>
    public bool IsRequired { get; }

    /// <summary>连接与取流策略。</summary>
    public VisionAcquisitionConnectionPolicy Connection { get; }

    /// <summary>外部回调缓冲的有界策略；PerRequest源为空。</summary>
    public VisionFrameInboxPolicy? Inbox { get; }

    /// <summary>Plugin私有设备设置原始JSON；为空表示尚未配置设备。公共层不解释其字段。</summary>
    public string? DeviceSettingsJson { get; }
}
