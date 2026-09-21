using System;

namespace DP.Vision.Acquisition;

/// <summary>一次采集的来源事实；由Acquisition Runtime在Provider返回后补齐，Provider不能自行伪造。</summary>
/// <remarks>
/// 跨字段不变式在这里强制：<see cref="EVisionAcquisitionMode.BufferedExternal"/> 的来源必须有
/// <see cref="ReceivedSequence"/> 与 <see cref="ReceivedAtUtc"/>。位置记录无法在构造期表达跨字段约束，
/// 因此本类型改为显式属性 + 两个构造入口；只传6个参数的调用方行为完全不变。
/// </remarks>
public sealed record VisionCaptureMetadata
{
    /// <summary>创建主动单次采集（OnDemand）的来源事实。</summary>
    /// <param name="captureId">采集意图与结果的唯一身份，由Runtime生成。</param>
    /// <param name="sourceId">逻辑视觉源标识。</param>
    /// <param name="providerId">实际完成采集的Provider稳定身份。</param>
    /// <param name="resourceKey">该次采集占用的物理资源键。</param>
    /// <param name="capturedAtUtc">设备报告或Runtime观测的采集时刻。</param>
    /// <param name="deviceSequence">设备可选提供的帧序号；不支持时为空。</param>
    public VisionCaptureMetadata(
        string captureId,
        string sourceId,
        string providerId,
        string resourceKey,
        DateTimeOffset capturedAtUtc,
        long? deviceSequence)
        : this(
            captureId,
            sourceId,
            providerId,
            resourceKey,
            capturedAtUtc,
            deviceSequence,
            EVisionAcquisitionMode.OnDemand,
            null,
            null)
    {
    }

    /// <summary>创建来源事实并校验模式与接收序号的一致性。</summary>
    /// <param name="captureId">采集意图与结果的唯一身份，由Runtime生成。</param>
    /// <param name="sourceId">逻辑视觉源标识。</param>
    /// <param name="providerId">实际完成采集的Provider稳定身份。</param>
    /// <param name="resourceKey">该次采集占用的物理资源键。</param>
    /// <param name="capturedAtUtc">设备报告或Runtime观测的采集时刻。</param>
    /// <param name="deviceSequence">设备可选提供的帧序号；不支持时为空。</param>
    /// <param name="acquisitionMode">该来源采用的采集模式。</param>
    /// <param name="receivedSequence">Runtime接收回调帧时分配的单调递增序号；主动单次采集为空。</param>
    /// <param name="receivedAtUtc">Runtime取得帧所有权的时刻；主动单次采集为空。</param>
    /// <exception cref="ArgumentException">身份为空，或BufferedExternal缺少接收序号/接收时刻。</exception>
    public VisionCaptureMetadata(
        string captureId,
        string sourceId,
        string providerId,
        string resourceKey,
        DateTimeOffset capturedAtUtc,
        long? deviceSequence,
        EVisionAcquisitionMode acquisitionMode,
        long? receivedSequence,
        DateTimeOffset? receivedAtUtc)
    {
        CaptureId = Require(captureId, "采集身份", nameof(captureId));
        SourceId = Require(sourceId, "逻辑源标识", nameof(sourceId));
        ProviderId = Require(providerId, "Provider身份", nameof(providerId));
        ResourceKey = Require(resourceKey, "物理资源键", nameof(resourceKey));

        if (acquisitionMode == EVisionAcquisitionMode.BufferedExternal)
        {
            if (receivedSequence is null)
                throw new ArgumentException(
                    "外部回调缓冲来源必须带Runtime接收序号；缺失说明该帧不是从有界队列领取的。",
                    nameof(receivedSequence));
            if (receivedAtUtc is null)
                throw new ArgumentException(
                    "外部回调缓冲来源必须带接收时刻；否则无法判断帧龄。",
                    nameof(receivedAtUtc));
        }
        else if (receivedSequence is not null || receivedAtUtc is not null)
        {
            throw new ArgumentException(
                "主动单次采集不应带Runtime接收序号或接收时刻；该序号只属于外部回调缓冲队列。",
                nameof(receivedSequence));
        }

        CapturedAtUtc = capturedAtUtc;
        DeviceSequence = deviceSequence;
        AcquisitionMode = acquisitionMode;
        ReceivedSequence = receivedSequence;
        ReceivedAtUtc = receivedAtUtc;
    }

    /// <summary>采集意图与结果的唯一身份，由Runtime生成；同时作为帧身份。</summary>
    public string CaptureId { get; }

    /// <summary>逻辑视觉源标识。</summary>
    public string SourceId { get; }

    /// <summary>实际完成采集的Provider稳定身份。</summary>
    public string ProviderId { get; }

    /// <summary>该次采集占用的物理资源键。</summary>
    public string ResourceKey { get; }

    /// <summary>设备报告或Runtime观测的采集时刻。</summary>
    public DateTimeOffset CapturedAtUtc { get; }

    /// <summary>设备可选提供的帧序号；不支持时为空。</summary>
    public long? DeviceSequence { get; }

    /// <summary>该来源采用的采集模式。</summary>
    public EVisionAcquisitionMode AcquisitionMode { get; }

    /// <summary>Runtime成功接收回调帧时分配的单调递增序号；主动单次采集为空。</summary>
    /// <remarks>它不是TriggerId，也不证明图像属于某个产品；外部设备不需要知道或指定它。</remarks>
    public long? ReceivedSequence { get; }

    /// <summary>Runtime取得帧所有权的时刻；主动单次采集为空。</summary>
    public DateTimeOffset? ReceivedAtUtc { get; }

    private static string Require(string value, string label, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(label + "不能为空。", parameterName);
        return value.Trim();
    }
}
