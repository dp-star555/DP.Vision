using System;

namespace DP.Vision.Acquisition;

/// <summary>一次采集的来源事实；由Acquisition Runtime在Provider返回后补齐，Provider不能自行伪造。</summary>
/// <param name="CaptureId">采集意图与结果的唯一身份，由Runtime生成。</param>
/// <param name="SourceId">逻辑视觉源标识。</param>
/// <param name="ProviderId">实际完成采集的Provider稳定身份。</param>
/// <param name="ResourceKey">该次采集占用的物理资源键。</param>
/// <param name="CapturedAtUtc">设备报告或Runtime观测的采集时刻。</param>
/// <param name="DeviceSequence">设备可选提供的帧序号；不支持时为空。</param>
public sealed record VisionCaptureMetadata(
    string CaptureId,
    string SourceId,
    string ProviderId,
    string ResourceKey,
    DateTimeOffset CapturedAtUtc,
    long? DeviceSequence);
