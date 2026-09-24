using System;
using System.Collections.Generic;

namespace DP.Vision.Acquisition.Management;

/// <summary>
/// 采集监视面板的一次采样：运行时整体状态加每个逻辑源一行诊断。
/// <para>
/// 采样是只读动作，不驱动设备、不改变配置；因此运行时尚未启动或已经停止时同样可以采样，
/// 此时行里的状态自己说明"还没打开"或"已经关了"，界面不需要为这两段时间另做一套空白态。
/// </para>
/// </summary>
public sealed class AcquisitionMonitorSnapshot
{
    /// <summary>创建监视快照。</summary>
    /// <param name="runtimeState">运行时整体生命周期状态。</param>
    /// <param name="compositionId">运行时所持组合身份；面板用它核对"这台机器跑的是哪一份组合"。</param>
    /// <param name="machineConfigurationRevision">生成该组合的机器配置修订号；未接入修订存储时为 0。</param>
    /// <param name="capturedAtUtc">本次采样的时刻（UTC），不是任何一帧的采集时刻。</param>
    /// <param name="rows">每个已发布逻辑源一行，按SourceId排序。</param>
    internal AcquisitionMonitorSnapshot(
        EVisionRuntimeState runtimeState,
        string compositionId,
        int machineConfigurationRevision,
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<AcquisitionMonitorRow> rows)
    {
        RuntimeState = runtimeState;
        CompositionId = compositionId;
        MachineConfigurationRevision = machineConfigurationRevision;
        CapturedAtUtc = capturedAtUtc;
        Rows = rows;
    }

    /// <summary>
    /// 运行时整体生命周期状态：Created/Starting/Ready/Degraded/NotReady/Stopped。
    /// <para>这里是状态本身而不是"是否正常"的结论，界面必须能显示 Starting 与 NotReady 的区别。</para>
    /// </summary>
    public EVisionRuntimeState RuntimeState { get; }

    /// <summary>是否全部Required源已连接；取值与 <c>VisionAcquisitionRuntime.IsReady</c> 一致，不由界面自行推断。</summary>
    public bool IsReady => RuntimeState == EVisionRuntimeState.Ready;

    /// <summary>是否降级运行（Required源已连接但存在Optional源失败）；取值与 <c>VisionAcquisitionRuntime.IsDegraded</c> 一致。</summary>
    public bool IsDegraded => RuntimeState == EVisionRuntimeState.Degraded;

    /// <summary>运行时所持组合身份。</summary>
    public string CompositionId { get; }

    /// <summary>生成该组合的机器配置修订号。</summary>
    public int MachineConfigurationRevision { get; }

    /// <summary>本次采样的时刻（UTC）。</summary>
    public DateTimeOffset CapturedAtUtc { get; }

    /// <summary>逐源诊断行；从未被使用过的源也会出现一行"尚未打开"的诊断。</summary>
    public IReadOnlyList<AcquisitionMonitorRow> Rows { get; }
}

/// <summary>
/// 监视面板里的一行：逐字段搬运 <see cref="VisionSourceDiagnostics"/>，不做二次统计。
/// <para>
/// 搬运而不是合并字段是刻意的：现场要判断的是"没触发、没领取还是已经故障"，
/// 任何"界面自己算一个汇总数"的做法都会让面板与运行诊断出现两套口径。
/// </para>
/// </summary>
public sealed class AcquisitionMonitorRow
{
    /// <summary>由运行诊断构造一行。</summary>
    /// <param name="diagnostics">运行时给出的逐源诊断快照。</param>
    internal AcquisitionMonitorRow(VisionSourceDiagnostics diagnostics)
    {
        SourceId = diagnostics.SourceId;
        ResourceKey = diagnostics.ResourceKey;
        ProviderId = diagnostics.ProviderId;
        State = diagnostics.State;
        Epoch = diagnostics.Epoch;
        FramesReceived = diagnostics.FramesReceived;
        FramesClaimed = diagnostics.FramesClaimed;
        FramesExpired = diagnostics.FramesExpired;
        FramesRejected = diagnostics.FramesRejected;
        InboxCount = diagnostics.InboxCount;
        InboxBytes = diagnostics.InboxBytes;
        InboxHighWatermark = diagnostics.InboxHighWatermark;
        BytesHighWatermark = diagnostics.BytesHighWatermark;
        DeviceSequenceGaps = diagnostics.DeviceSequenceGaps;
        CallbackFaults = diagnostics.CallbackFaults;
        FaultKind = diagnostics.FaultKind;
        FaultMessage = diagnostics.FaultMessage;
        IsFaulted = diagnostics.IsFaulted;
        ConnectionState = diagnostics.ConnectionState;
        ConnectionMessage = diagnostics.ConnectionMessage;
        FramesRejectedWithoutEpoch = diagnostics.FramesRejectedWithoutEpoch;
        UnclaimedAtEpochEnd = diagnostics.UnclaimedAtEpochEnd;
        AcquisitionTypeId = diagnostics.AcquisitionTypeId;
        PluginId = diagnostics.PluginId;
        PluginVersion = diagnostics.PluginVersion;
        TransferState = diagnostics.TransferState;
        ConnectionRevision = diagnostics.ConnectionRevision;
        FramesRejectedOverflow = diagnostics.FramesRejectedOverflow;
        Transfer = diagnostics.Transfer;
    }

    /// <summary>逻辑源标识。</summary>
    public string SourceId { get; }

    /// <summary>物理资源键，用于把两个Source认到同一台相机上。</summary>
    public string ResourceKey { get; }

    /// <summary>服务该源的Provider身份。</summary>
    public string ProviderId { get; }

    /// <summary>会话状态：Created/Opening/Armed/Faulted/Stopping/Disposed/Streaming。</summary>
    public string State { get; }

    /// <summary>当前采集代次。</summary>
    public int Epoch { get; }

    /// <summary>已接收帧总数，含被拒绝的帧。</summary>
    public long FramesReceived { get; }

    /// <summary>已成功领取帧总数。</summary>
    public long FramesClaimed { get; }

    /// <summary>因超龄被释放的帧总数。</summary>
    public long FramesExpired { get; }

    /// <summary>被拒绝帧总数（溢出、未布防、无活动代次合计），与逐项计数同时显示以便核对。</summary>
    public long FramesRejected { get; }

    /// <summary>当前待领取帧数；消费端是否跟得上主要由它体现。</summary>
    public int InboxCount { get; }

    /// <summary>当前待领取帧的逻辑像素字节总和。</summary>
    public long InboxBytes { get; }

    /// <summary>待领取帧数的历史峰值。</summary>
    public int InboxHighWatermark { get; }

    /// <summary>待领取字节数的历史峰值。</summary>
    public long BytesHighWatermark { get; }

    /// <summary>设备序号跳号次数；跳号说明回调丢帧，不是"没有触发"。</summary>
    public long DeviceSequenceGaps { get; }

    /// <summary>回调边界吞掉的异常次数；非零表示Provider回调实现有缺陷。</summary>
    public long CallbackFaults { get; }

    /// <summary>故障类别；未故障时为空。</summary>
    public string? FaultKind { get; }

    /// <summary>故障说明；未故障时为空。</summary>
    public string? FaultMessage { get; }

    /// <summary>是否已故障。</summary>
    public bool IsFaulted { get; }

    /// <summary>设备连接状态；与取流状态分开显示，因为"连着但没推帧"和"已经断开"是两回事。</summary>
    public EVisionConnectionState ConnectionState { get; }

    /// <summary>连接诊断说明：成功时报告设备规范身份，失败时报告原因。</summary>
    public string? ConnectionMessage { get; }

    /// <summary>无活动采集代次时被拒绝并释放的帧数；它回答"帧来了但没有根运行"。</summary>
    public long FramesRejectedWithoutEpoch { get; }

    /// <summary>代次收口时未领取而释放的帧数。</summary>
    public long UnclaimedAtEpochEnd { get; }

    /// <summary>服务该源的AcquisitionType身份；V1组合或未安装Type时为空。</summary>
    public string? AcquisitionTypeId { get; }

    /// <summary>Provider插件身份；未发布时为空。</summary>
    public string? PluginId { get; }

    /// <summary>Provider插件实现版本；未发布时为空。它回答"是插件旧了还是配置错了"。</summary>
    public string? PluginVersion { get; }

    /// <summary>取流状态（NotStarted/Streaming/Stopped/Faulted）；未接入时为"NotStarted"。</summary>
    public string? TransferState { get; }

    /// <summary>本会话成功建立连接的次数；大于 1 表示发生过重连。</summary>
    public int ConnectionRevision { get; }

    /// <summary>因队列溢出被拒绝的帧数；它是FaultSource策略的触发原因。</summary>
    public long FramesRejectedOverflow { get; }

    /// <summary>
    /// 像素落地观测累计值；Provider从未上报观测时为空。
    /// <para>
    /// 保持可空而不是折算成 0：界面必须能区分"没有观测"与"观测到 0 字节"，后者只可能是上报实现出错。
    /// </para>
    /// </summary>
    public VisionPixelTransferSummary? Transfer { get; }

    /// <summary>是否有像素落地观测可供展示；为假时界面显示"未上报观测"而不是一排零。</summary>
    public bool HasTransferObservation => Transfer is not null;
}