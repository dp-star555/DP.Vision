using System;
using System.Collections.Generic;

namespace DP.Vision.Acquisition;

/// <summary>
/// 可导出运行制品的根运行租约。
/// <para>
/// 制品能力放在独立接口而不是 <see cref="IVisionAcquisitionRunLease"/> 上：
/// 后者是工作流侧唯一需要实现的契约，往里加成员会让每个宿主都被迫实现与它无关的审计能力。
/// 宿主需要制品时只做一次能力查询，拿不到就说明该租约不产出制品。
/// </para>
/// </summary>
public interface IVisionAcquisitionRunArtifactSource
{
    /// <summary>读取本轮运行的采集制品。</summary>
    /// <param name="artifact">本轮制品；租约尚未退役时其 <c>CompletedAtUtc</c> 为当时的时刻。</param>
    /// <returns>租约能产出制品时返回 <see langword="true"/>。</returns>
    bool TryGetArtifact(out VisionAcquisitionRunArtifact? artifact);
}

/// <summary>
/// 一根根运行的采集制品：把"这次运行到底用了什么配置、经手了哪些帧"固定成可归档的记录。
/// <para>
/// 它只记录事实，不参与控制流：制品缺失或字段为空绝不改变采集行为，
/// 否则"审计"就会变成一条会阻断生产的隐藏依赖。
/// </para>
/// </summary>
/// <param name="RunId">根运行身份。</param>
/// <param name="WorkflowCompositionId">工作流侧组合身份；宿主未提供时为空。</param>
/// <param name="AcquisitionCompositionId">采集侧Provider组合身份；与工作流组合身份分别记录。</param>
/// <param name="MachineConfigurationRevision">生成该组合的机器配置修订号；未接入修订存储时为0。</param>
/// <param name="Epoch">本轮采集代次。</param>
/// <param name="StartedAtUtc">本轮取得采集所有权的时刻。</param>
/// <param name="CompletedAtUtc">读取制品时的时刻；租约未退役时它不是最终值。</param>
/// <param name="PluginManifest">Provider版本清单，按ProviderId排序。</param>
/// <param name="Sources">每个已布防源的采集与配置事实。</param>
/// <param name="UnclaimedAtEpochEnd">本轮收口时未领取而释放的帧总数。</param>
public sealed record VisionAcquisitionRunArtifact(
    string RunId,
    string? WorkflowCompositionId,
    string AcquisitionCompositionId,
    int MachineConfigurationRevision,
    int Epoch,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string> PluginManifest,
    IReadOnlyList<VisionAcquisitionRunSourceArtifact> Sources,
    long UnclaimedAtEpochEnd);

/// <summary>
/// 一个源在本轮运行中的事实：身份、配置摘要（已脱敏）、运行指标与故障原因。
/// </summary>
/// <param name="SourceId">逻辑源标识。</param>
/// <param name="AcquisitionTypeId">AcquisitionType身份；V1组合或未安装Type时为空。</param>
/// <param name="ProviderId">服务该源的Provider身份。</param>
/// <param name="PluginVersion">Provider实现版本；未发布时为空。</param>
/// <param name="ResourceKey">物理资源键。</param>
/// <param name="CameraConfigurationSummary">Plugin私有配置摘要；序列号等敏感值已遮蔽。</param>
/// <param name="ClaimedFrames">本轮通过队列领取的帧数。</param>
/// <param name="FramesReceived">累计接收帧数，含被拒绝的帧。</param>
/// <param name="FramesExpired">累计超龄释放帧数。</param>
/// <param name="FramesRejectedWithoutEpoch">累计无活动代次拒绝帧数。</param>
/// <param name="FramesRejectedOverflow">累计队列溢出拒绝帧数。</param>
/// <param name="UnclaimedAtEpochEnd">累计代次收口未领取帧数。</param>
/// <param name="InboxHighWatermark">待领取帧数历史峰值。</param>
/// <param name="BytesHighWatermark">待领取字节数历史峰值。</param>
/// <param name="DeviceSequenceGaps">设备序号跳号次数。</param>
/// <param name="LastFailureKind">故障类别；未故障时为空。</param>
/// <param name="LastFailureMessage">故障说明；未故障时为空。</param>
/// <param name="ConnectionState">读取制品时的连接状态。</param>
/// <param name="TransferState">读取制品时的取流状态。</param>
/// <param name="ClaimedFrameDetails">本轮领取帧的身份，按领取顺序。</param>
public sealed record VisionAcquisitionRunSourceArtifact(
    string SourceId,
    string? AcquisitionTypeId,
    string ProviderId,
    string? PluginVersion,
    string ResourceKey,
    string? CameraConfigurationSummary,
    long ClaimedFrames,
    long FramesReceived,
    long FramesExpired,
    long FramesRejectedWithoutEpoch,
    long FramesRejectedOverflow,
    long UnclaimedAtEpochEnd,
    int InboxHighWatermark,
    long BytesHighWatermark,
    long DeviceSequenceGaps,
    string? LastFailureKind,
    string? LastFailureMessage,
    EVisionConnectionState ConnectionState,
    string? TransferState,
    IReadOnlyList<VisionAcquisitionRunFrameArtifact> ClaimedFrameDetails);

/// <summary>本轮领取到的一帧的身份；用于事后把制品与具体图像对上号。</summary>
/// <param name="CaptureId">Runtime 分配的采集身份。</param>
/// <param name="ReceivedSequence">Runtime 接收序号；主动单次采集没有这个序号。</param>
/// <param name="DeviceSequence">设备报告帧序号；设备不提供时为空。</param>
/// <param name="CapturedAtUtc">采集时刻。</param>
public sealed record VisionAcquisitionRunFrameArtifact(
    string CaptureId,
    long? ReceivedSequence,
    long? DeviceSequence,
    DateTimeOffset CapturedAtUtc);