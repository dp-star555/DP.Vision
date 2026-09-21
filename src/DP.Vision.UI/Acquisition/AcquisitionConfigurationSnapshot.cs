using System;
using System.Collections.Generic;
using DP.Vision.Acquisition;

namespace DP.Vision.UI.Acquisition;

/// <summary>
/// 采集配置面板的一次采样：当前生效修订、候选状态、已发布源与历史修订。
/// <para>
/// 采样只读；候选校验与发布都由 <see cref="AcquisitionManagementPresenter"/> 显式调用触发，
/// 面板不会因为刷新而改动配置。
/// </para>
/// </summary>
public sealed class AcquisitionConfigurationSnapshot
{
    /// <summary>创建配置快照。</summary>
    /// <param name="currentRevision">当前生效修订号；从未发布过任何修订时为空。</param>
    /// <param name="compositionId">当前生效组合身份；与修订号成对出现，供运行制品反查。</param>
    /// <param name="hasCandidate">是否存在尚未发布的候选配置。</param>
    /// <param name="candidateErrors">最近一次候选校验的错误列表；通过或尚未校验时为空。</param>
    /// <param name="lastFailureMessage">最近一次发布/回滚失败的完整错误文本；成功或尚未失败时为空。</param>
    /// <param name="sources">已发布逻辑源行，按SourceId排序。</param>
    /// <param name="history">历史修订行，按修订号升序（即发布顺序）。</param>
    internal AcquisitionConfigurationSnapshot(
        int? currentRevision,
        string? compositionId,
        bool hasCandidate,
        IReadOnlyList<string> candidateErrors,
        string? lastFailureMessage,
        IReadOnlyList<AcquisitionConfigurationSourceRow> sources,
        IReadOnlyList<AcquisitionConfigurationRevisionRow> history)
    {
        CurrentRevision = currentRevision;
        CompositionId = compositionId;
        HasCandidate = hasCandidate;
        CandidateErrors = candidateErrors;
        LastFailureMessage = lastFailureMessage;
        Sources = sources;
        History = history;
    }

    /// <summary>当前生效修订号；从未发布过任何修订时为空。</summary>
    public int? CurrentRevision { get; }

    /// <summary>当前生效组合身份；从未发布时为空。</summary>
    public string? CompositionId { get; }

    /// <summary>是否存在尚未发布的候选配置；界面据此决定"发布"按钮是否可用。</summary>
    public bool HasCandidate { get; }

    /// <summary>最近一次候选校验的错误列表；通过或尚未校验时为空列表。</summary>
    public IReadOnlyList<string> CandidateErrors { get; }

    /// <summary>
    /// 最近一次发布或回滚失败的完整错误文本；成功或尚未失败时为空。
    /// <para>界面可直接显示它，不必解析异常；异常路径仍照常抛出，本字段只是同一份文本的留档。</para>
    /// </summary>
    public string? LastFailureMessage { get; }

    /// <summary>已发布逻辑源行；不可用源同样出现并带诊断。</summary>
    public IReadOnlyList<AcquisitionConfigurationSourceRow> Sources { get; }

    /// <summary>历史修订行，按修订号升序；回滚产生的修订也在其中。</summary>
    public IReadOnlyList<AcquisitionConfigurationRevisionRow> History { get; }
}

/// <summary>
/// 配置面板里的一行逻辑源：机器配置公开部分加Plugin私有配置摘要。
/// <para>
/// 摘要只用于人工比对"两次发布之间改了什么"，不参与运行决策；它已由Provider做成确定性文本。
/// </para>
/// </summary>
public sealed class AcquisitionConfigurationSourceRow
{
    /// <summary>由已发布源目录条目与配置摘要构造一行。</summary>
    /// <param name="source">已发布源目录投影。</param>
    /// <param name="configurationSummary">Plugin私有配置摘要；不存在时为空。</param>
    internal AcquisitionConfigurationSourceRow(VisionAcquisitionSourceInfo source, string? configurationSummary)
    {
        SourceId = source.SourceId;
        AcquisitionTypeId = source.AcquisitionTypeId;
        ResourceKey = source.ResourceKey;
        Kind = source.Kind;
        AcquisitionMode = source.AcquisitionMode;
        SharingPolicy = source.SharingPolicy;
        IsAvailable = source.IsAvailable;
        Diagnostic = source.Diagnostic;
        ConfigurationSummary = configurationSummary;
    }

    /// <summary>逻辑源标识。</summary>
    public string SourceId { get; }

    /// <summary>AcquisitionType身份；V1组合不解释机器配置时为空。</summary>
    public string? AcquisitionTypeId { get; }

    /// <summary>规范物理资源键；Source不可用（未安装Type）时为空串。</summary>
    public string ResourceKey { get; }

    /// <summary>采集几何形态；未安装Type或V1组合时为空。</summary>
    public EVisionAcquisitionKind? Kind { get; }

    /// <summary>采集时序：主动单次采集或外部回调缓冲。</summary>
    public EVisionAcquisitionMode AcquisitionMode { get; }

    /// <summary>该源在物理资源上的并发协调策略。</summary>
    public EVisionSourceSharingPolicy SharingPolicy { get; }

    /// <summary>Type已安装、配置已验证且该源当前可采集。</summary>
    public bool IsAvailable { get; }

    /// <summary>不可用原因；可用时为空。</summary>
    public string? Diagnostic { get; }

    /// <summary>Plugin生成的确定性配置摘要；未提供时为空。</summary>
    public string? ConfigurationSummary { get; }
}

/// <summary>配置面板里的一行历史修订；历史只追加，因此它同时是审计记录。</summary>
public sealed class AcquisitionConfigurationRevisionRow
{
    /// <summary>由一条已发布修订构造一行。</summary>
    /// <param name="revision">已发布修订。</param>
    internal AcquisitionConfigurationRevisionRow(VisionAcquisitionMachineConfigurationRevision revision)
    {
        Revision = revision.Revision;
        CompositionId = revision.CompositionId;
        PublishedAtUtc = revision.PublishedAtUtc;
        RestoredFromRevision = revision.RestoredFromRevision;
        IsRollback = revision.IsRollback;
    }

    /// <summary>修订号；从 1 开始连续递增，等于发布顺序。</summary>
    public int Revision { get; }

    /// <summary>由该修订配置组合出的组合身份。</summary>
    public string CompositionId { get; }

    /// <summary>发布时刻（UTC）。</summary>
    public DateTimeOffset PublishedAtUtc { get; }

    /// <summary>由回滚产生时记录来源修订号；正常发布时为空。</summary>
    public int? RestoredFromRevision { get; }

    /// <summary>本修订是否由回滚产生。</summary>
    public bool IsRollback { get; }
}