using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Acquisition;

/// <summary>
/// 机器相机配置的修订存储：候选校验、发布、历史与回滚的唯一入口。
/// <para>
/// 设计要点：<b>历史只追加，不移动指针</b>。回滚到某个修订不删除中间修订，而是追加一条内容等于目标修订的新修订，
/// 并记录它从哪个修订恢复。这样"当前生效的是哪一版"永远等于历史里修订号最大的那一条，
/// 现场排查时不需要还原一串指针变更才能解释"当时机器上跑的是什么"，也不存在丢历史的风险。
/// </para>
/// <para>
/// 校验与发布使用同一次组合结果：校验通过后发布的是<b>被校验的那一份快照</b>，
/// 不会出现"校验过的内容"与"发布出去的内容"不同的问题。
/// </para>
/// </summary>
public sealed class VisionAcquisitionMachineConfigurationRevisionStore
{
    private readonly VisionAcquisitionTypeCatalog _catalog;
    private readonly object _sync = new object();
    private readonly List<VisionAcquisitionMachineConfigurationRevision> _history =
        new List<VisionAcquisitionMachineConfigurationRevision>();

    private string? _candidateJson;
    private VisionAcquisitionProviderComposition? _candidateComposition;
    private IReadOnlyList<string>? _candidateErrors;

    /// <summary>创建修订存储。</summary>
    /// <param name="catalog">已冻结的AcquisitionType Catalog；校验与组合都基于它，存储自身不加载DLL。</param>
    /// <param name="initialConfigurationJson">可选的初始配置；给出时立即校验并作为第 1 号修订发布。</param>
    /// <exception cref="ArgumentNullException">Catalog 为空。</exception>
    /// <exception cref="VisionSourceConfigurationException">初始配置校验不通过。</exception>
    public VisionAcquisitionMachineConfigurationRevisionStore(
        VisionAcquisitionTypeCatalog catalog,
        string? initialConfigurationJson = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (initialConfigurationJson is null)
            return;

        SetCandidate(initialConfigurationJson);
        Publish();
    }

    /// <summary>当前生效修订；从未发布过任何修订时为空。</summary>
    public VisionAcquisitionMachineConfigurationRevision? Current
    {
        get
        {
            lock (_sync)
                return _history.Count == 0 ? null : _history[_history.Count - 1];
        }
    }

    /// <summary>是否存在尚未发布的候选配置。</summary>
    public bool HasCandidate
    {
        get
        {
            lock (_sync)
                return _candidateJson is not null;
        }
    }

    /// <summary>
    /// 设置候选配置原文；本方法只记录不校验，校验是显式动作。
    /// <para>
    /// 分成两步是为了让管理界面可以在编辑过程中反复校验而不必重设候选，
    /// 也为了在候选非法时把多条错误一次性报告出来，而不是在设置候选时抛出第一条。
    /// </para>
    /// </summary>
    /// <param name="json">机器相机配置 JSON。</param>
    /// <exception cref="ArgumentException">配置为空。</exception>
    public void SetCandidate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("候选机器相机配置不能为空。", nameof(json));

        lock (_sync)
        {
            _candidateJson = json;
            _candidateComposition = null;
            _candidateErrors = null;
        }
    }

    /// <summary>
    /// 校验候选配置，并缓存本次组合结果供 <see cref="Publish"/> 复用。
    /// <para>
    /// 本方法不抛配置异常：校验路径是管理界面的常规路径，调用方需要的是"能不能发布 + 为什么不能"，
    /// 而不是异常。第三方 Plugin 在解析 deviceSettings 时抛出的意外异常同样被转成诊断文本，
    /// 否则一次插件缺陷会让管理界面在校验按钮上直接崩溃。
    /// </para>
    /// </summary>
    /// <returns>校验结果；候选缺失、解析失败或组合失败时为无效。</returns>
    public VisionAcquisitionMachineConfigurationValidationResult ValidateCandidate()
    {
        lock (_sync)
        {
            if (_candidateJson is null)
                return VisionAcquisitionMachineConfigurationValidationResult.Invalid(
                    "尚未设置候选机器相机配置。");

            try
            {
                var cameras = VisionAcquisitionMachineConfigurationParser.Parse(_candidateJson);
                var composition = new VisionAcquisitionMachineConfigurationComposer().Compose(_catalog, cameras);
                _candidateComposition = composition;
                _candidateErrors = null;

                // 未安装Type的Source仍然是合法配置（校验通过），但必须在结果里显式列出：
                // 现场最容易出错的正是"插件没部署"被当成"相机没接"。
                var unavailable = composition.SourceCatalog
                    .Where(info => !info.IsAvailable)
                    .Select(info => info.SourceId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

                return new VisionAcquisitionMachineConfigurationValidationResult(
                    IsValid: true,
                    Errors: Array.Empty<string>(),
                    CompositionId: composition.CompositionId,
                    SourceIds: composition.Sources.Select(binding => binding.SourceId).ToArray(),
                    UnavailableSourceIds: unavailable);
            }
            catch (Exception exception)
            {
                _candidateComposition = null;
                var message = string.IsNullOrWhiteSpace(exception.Message)
                    ? exception.GetType().Name
                    : exception.Message;
                _candidateErrors = new[] { message };
                return VisionAcquisitionMachineConfigurationValidationResult.Invalid(message);
            }
        }
    }

    /// <summary>
    /// 发布候选配置：先校验，通过后追加一条新修订并清空候选。
    /// <para>
    /// 清空候选是刻意的：同一个候选被连点两次发布不应产生两条内容相同的修订。
    /// </para>
    /// </summary>
    /// <returns>刚发布的修订。</returns>
    /// <exception cref="VisionSourceConfigurationException">候选缺失或校验不通过；消息包含全部校验错误。</exception>
    public VisionAcquisitionMachineConfigurationRevision Publish()
    {
        lock (_sync)
        {
            var result = ValidateCandidate();
            if (!result.IsValid || _candidateComposition is null)
            {
                var details = result.Errors.Count == 0
                    ? "候选机器相机配置校验未通过。"
                    : string.Join("；", result.Errors);
                throw new VisionSourceConfigurationException($"候选机器相机配置校验未通过：{details}");
            }

            var revision = AppendRevision(_candidateComposition, restoredFromRevision: null);
            _candidateJson = null;
            _candidateErrors = null;
            _candidateComposition = null;
            return revision;
        }
    }

    /// <summary>
    /// 回滚到任一历史修订：以该修订的配置内容追加一条新修订。
    /// <para>
    /// 不删除目标修订之后的任何修订，也不改变历史顺序——历史是审计记录，不是可回退的指针。
    /// 目标修订内容的校验规则与普通候选一致；若当时的配置在当前 Catalog 下已不合法（例如插件降级），
    /// 回滚会失败而不是发布一份无效配置。
    /// </para>
    /// </summary>
    /// <param name="revision">目标修订号；必须是 <see cref="History"/> 中出现过的修订号。</param>
    /// <returns>由回滚产生的新修订。</returns>
    /// <exception cref="VisionSourceConfigurationException">修订号不存在，或目标修订内容在当前 Catalog 下校验不通过。</exception>
    public VisionAcquisitionMachineConfigurationRevision Rollback(int revision)
    {
        lock (_sync)
        {
            var target = _history.SingleOrDefault(item => item.Revision == revision)
                ?? throw new VisionSourceConfigurationException(
                    $"修订 {revision} 不存在，无法回滚；可用修订号："
                    + (_history.Count == 0
                        ? "（尚无任何修订）"
                        : string.Join("、", _history.Select(item => item.Revision).ToArray()))
                    + "。");

            _candidateJson = target.ConfigurationJson;
            _candidateComposition = null;
            _candidateErrors = null;

            var result = ValidateCandidate();
            if (!result.IsValid || _candidateComposition is null)
            {
                _candidateJson = null;
                var details = result.Errors.Count == 0
                    ? "配置校验未通过。"
                    : string.Join("；", result.Errors);

                // 回滚失败必须把候选一起收回：留下一个"内容来自历史但校验不过"的候选，
                // 会让下一次 Publish 报出与操作员当前动作无关的错误。
                throw new VisionSourceConfigurationException(
                    $"回滚到修订 {revision} 失败：该修订内容在当前 Catalog 下校验不通过：{details}");
            }

            var rolledBack = AppendRevision(_candidateComposition, restoredFromRevision: revision);
            _candidateJson = null;
            _candidateErrors = null;
            _candidateComposition = null;
            return rolledBack;
        }
    }

    /// <summary>历史修订列表，按修订号升序（即发布顺序）；相邻修订可直接比对差异。</summary>
    /// <returns>已发布的全部修订快照；从未发布时为空列表。</returns>
    public IReadOnlyList<VisionAcquisitionMachineConfigurationRevision> History()
    {
        lock (_sync)
            return _history.ToArray();
    }

    /// <summary>按修订号查找历史修订。</summary>
    /// <param name="revision">修订号。</param>
    /// <returns>找到的修订；不存在时为空。</returns>
    public VisionAcquisitionMachineConfigurationRevision? Find(int revision)
    {
        lock (_sync)
            return _history.SingleOrDefault(item => item.Revision == revision);
    }

    /// <summary>最近一次候选校验的错误列表；候选缺失或已通过校验时为空。</summary>
    public IReadOnlyList<string> CandidateErrors
    {
        get
        {
            lock (_sync)
                return _candidateErrors ?? Array.Empty<string>();
        }
    }

    private VisionAcquisitionMachineConfigurationRevision AppendRevision(
        VisionAcquisitionProviderComposition composition,
        int? restoredFromRevision)
    {
        var revision = new VisionAcquisitionMachineConfigurationRevision(
            _history.Count + 1,
            _candidateJson!,
            composition.CompositionId,
            _catalog.CatalogId,
            DateTimeOffset.UtcNow,
            restoredFromRevision);
        _history.Add(revision);
        return revision;
    }
}

/// <summary>
/// 一条已发布的机器相机配置修订。
/// <para>
/// 同时保存配置原文与组合身份：原文用于回滚与差异比对，组合身份用于把
/// "运行制品里记录的 AcquisitionCompositionId" 反向定位到具体是第几号修订。
/// </para>
/// </summary>
/// <param name="Revision">修订号；从 1 开始连续递增，等于发布顺序。</param>
/// <param name="ConfigurationJson">发布时的配置原文；回滚以此为准。</param>
/// <param name="CompositionId">由本配置组合出的组合身份。</param>
/// <param name="CatalogId">组合时使用的 Catalog 身份；Catalog 变化会改变组合身份。</param>
/// <param name="PublishedAtUtc">发布时刻（UTC）。</param>
/// <param name="RestoredFromRevision">由回滚产生时记录来源修订号；正常发布为空。</param>
public sealed record VisionAcquisitionMachineConfigurationRevision(
    int Revision,
    string ConfigurationJson,
    string CompositionId,
    string CatalogId,
    DateTimeOffset PublishedAtUtc,
    int? RestoredFromRevision = null)
{
    /// <summary>本修订是否由回滚产生。</summary>
    public bool IsRollback => RestoredFromRevision is not null;
}

/// <summary>候选机器相机配置的校验结果。</summary>
/// <param name="IsValid">候选是否可用于发布。</param>
/// <param name="Errors">校验错误文本；通过时为空。</param>
/// <param name="CompositionId">校验通过时给出组合身份，否则为空。</param>
/// <param name="SourceIds">校验通过时给出全部逻辑源标识（按配置顺序），否则为空。</param>
/// <param name="UnavailableSourceIds">校验通过但 AcquisitionType 未安装的逻辑源标识；这些源会被标记为不可用。</param>
public sealed record VisionAcquisitionMachineConfigurationValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    string? CompositionId = null,
    IReadOnlyList<string>? SourceIds = null,
    IReadOnlyList<string>? UnavailableSourceIds = null)
{
    /// <summary>构造无效结果。</summary>
    /// <param name="error">错误文本。</param>
    /// <returns>无效结果。</returns>
    internal static VisionAcquisitionMachineConfigurationValidationResult Invalid(string error) =>
        new(false, new[] { error });
}