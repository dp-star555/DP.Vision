using System;

namespace DP.Vision.Acquisition;

/// <summary>设备健康状态；Faulted不能伪装为Closed或自动成功。</summary>
public enum EVisionDeviceHealthState
{
    /// <summary>尚未查询或Provider不提供健康信息。</summary>
    Unknown = 0,

    /// <summary>设备可用。</summary>
    Healthy = 1,

    /// <summary>设备可用但已降级，例如丢帧或重连过。</summary>
    Degraded = 2,

    /// <summary>设备已故障，不能继续采集。</summary>
    Faulted = 3
}

/// <summary>一次设备健康查询结果；不强迫不支持该能力的Provider伪造支持。</summary>
/// <param name="State">健康状态。</param>
/// <param name="Message">可选诊断说明。</param>
/// <param name="ObservedAtUtc">观测时刻；未知时为空。</param>
public sealed record VisionDeviceHealth(
    EVisionDeviceHealthState State,
    string? Message = null,
    DateTimeOffset? ObservedAtUtc = null);
