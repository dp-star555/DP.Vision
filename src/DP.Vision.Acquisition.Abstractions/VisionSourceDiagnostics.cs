using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// 一个逻辑源的运行诊断快照；供运行监视与现场排障读取，不参与控制流。
/// <para>
/// 字段覆盖"来源身份 + 当前状态 + 运行指标 + 故障原因"四类，任何一类缺失都会让现场无法判断
/// 是"没触发"、"没领取"还是"已经故障"。
/// </para>
/// </summary>
/// <param name="SourceId">逻辑源标识。</param>
/// <param name="ResourceKey">物理资源键。</param>
/// <param name="ProviderId">服务该源的Provider身份。</param>
/// <param name="State">会话状态：Created/Opening/Armed/Faulted/Stopping/Disposed。</param>
/// <param name="Epoch">当前采集代次。</param>
/// <param name="FramesReceived">已接收的帧总数，含被拒绝的帧。</param>
/// <param name="FramesClaimed">已成功领取的帧总数。</param>
/// <param name="FramesExpired">因超龄被释放的帧总数。</param>
/// <param name="FramesRejected">因队列溢出或未布防被拒绝的帧总数。</param>
/// <param name="InboxCount">当前待领取帧数。</param>
/// <param name="InboxBytes">当前待领取帧的逻辑像素字节总和。</param>
/// <param name="InboxHighWatermark">待领取帧数的历史峰值。</param>
/// <param name="BytesHighWatermark">待领取字节数的历史峰值。</param>
/// <param name="DeviceSequenceGaps">设备序号出现跳号的次数。</param>
/// <param name="CallbackFaults">回调边界吞掉的异常次数。</param>
/// <param name="FaultKind">故障类别；未故障时为空。</param>
/// <param name="FaultMessage">故障说明；未故障时为空。</param>
public sealed record VisionSourceDiagnostics(
    string SourceId,
    string ResourceKey,
    string ProviderId,
    string State,
    int Epoch,
    long FramesReceived,
    long FramesClaimed,
    long FramesExpired,
    long FramesRejected,
    int InboxCount,
    long InboxBytes,
    int InboxHighWatermark,
    long BytesHighWatermark,
    long DeviceSequenceGaps,
    long CallbackFaults,
    string? FaultKind,
    string? FaultMessage)
{
    /// <summary>是否处于故障状态。</summary>
    public bool IsFaulted => FaultKind is not null;
}
