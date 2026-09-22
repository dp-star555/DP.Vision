using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Management;

/// <summary>
/// 采集管理界面的表现层：把设备发现、配置修订、运行监视与试拍编排成宿主可直接绑定的快照。
/// <para>
/// 本类不依赖任何UI框架，也不拥有设备：设备生命周期归 <see cref="VisionAcquisitionRuntime"/>，
/// 配置修订归 <see cref="VisionAcquisitionMachineConfigurationRevisionStore"/>，这里只做编排与投影。
/// </para>
/// <para>除试拍外的方法都是纯读取，可在UI线程直接调用；试拍是异步的，取消按协作式传播。</para>
/// </summary>
public sealed class AcquisitionManagementPresenter
{
    /// <summary>试拍发起方身份；用于资源冲突诊断，不是设备句柄。</summary>
    private const string TrialOwnerId = "dp.vision.ui.acquisition.trial";

    /// <summary>未显式给出请求时试拍使用的超时；等待外部触发或长曝光时需要更长时间，由调用方传入请求覆盖。</summary>
    private static readonly TimeSpan DefaultTrialTimeout = TimeSpan.FromSeconds(5);

    private readonly VisionAcquisitionRuntime _runtime;
    private readonly VisionAcquisitionProviderComposition _composition;
    private readonly VisionAcquisitionMachineConfigurationRevisionStore _revisions;
    private readonly Func<VisionAcquisitionProviderRegistration, IVisionAcquisitionProvider> _discoveryProviderFactory;
    private string? _configurationFailure;

    /// <summary>创建表现层。</summary>
    /// <param name="runtime">站点级采集运行时；监视与试拍都读它，本类不释放它。</param>
    /// <param name="composition">运行时所使用的已发布组合；发现与"是否已被配置引用"都以它为准。</param>
    /// <param name="revisions">机器配置修订存储；候选校验、发布、历史与回滚的唯一入口。</param>
    /// <param name="discoveryProviderFactory">
    /// 发现用Provider工厂；为空时使用注册里的工厂。
    /// <para>
    /// 工厂每次调用必须返回独立实例（注册契约如此约定）：发现结束会释放该实例。
    /// 传入复用实例的工厂会让运行时失去自己的Provider，宿主必须自行保证不会。
    /// </para>
    /// </param>
    /// <exception cref="ArgumentNullException">运行时、组合或修订存储为空。</exception>
    public AcquisitionManagementPresenter(
        VisionAcquisitionRuntime runtime,
        VisionAcquisitionProviderComposition composition,
        VisionAcquisitionMachineConfigurationRevisionStore revisions,
        Func<VisionAcquisitionProviderRegistration, IVisionAcquisitionProvider>? discoveryProviderFactory = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _discoveryProviderFactory = discoveryProviderFactory ?? (registration => registration.Factory());
    }

    /// <summary>
    /// 遍历当前组合里已注册的Provider做一次设备发现，把各Provider的候选合并成顺序确定的列表。
    /// <para>
    /// 单个Provider失败（例如未装配SDK）只记为该Provider的一条诊断，不整体失败，
    /// 也不会被伪装成"现场没有设备"；候选顺序按 ProviderId、规范资源键、显示名、绑定身份依次排序，
    /// 因此同一次枚举在任何机器上都得到同一顺序，界面不会抖动。
    /// </para>
    /// <para>
    /// 发现不使用Provider私有配置，也不需要设备已打开：它的用途正是"还没有配置时先看见现场有哪些设备"。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>候选设备与失败诊断。</returns>
    public async ValueTask<AcquisitionDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        var configuredKeys = ConfiguredResourceKeys();
        var devices = new List<AcquisitionDiscoveredDevice>();
        var failures = new List<AcquisitionDiscoveryFailure>();

        foreach (var providerId in ProviderIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_composition.TryGetProvider(providerId, out var registration) || registration is null)
                continue;

            try
            {
                var provider = _discoveryProviderFactory(registration);
                if (provider is null)
                {
                    failures.Add(new AcquisitionDiscoveryFailure(providerId, "Provider工厂返回空实例，无法发现设备。"));
                    continue;
                }

                await using (provider)
                {
                    if (!(provider is IVisionDeviceDiscovery discovery))
                        continue;

                    var found = await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    if (found is null)
                        continue;

                    foreach (var descriptor in found)
                    {
                        if (descriptor is null)
                            continue;
                        devices.Add(new AcquisitionDiscoveredDevice(descriptor, IsConfigured(descriptor, configuredKeys)));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new AcquisitionDiscoveryFailure(providerId, Describe(exception)));
            }
        }

        return new AcquisitionDiscoverySnapshot(
            devices
                .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
                .ThenBy(item => item.CanonicalKey ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
                .ThenBy(item => item.ProviderBindingId, StringComparer.Ordinal)
                .ToArray(),
            failures
                .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
                .ThenBy(item => item.Message, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// 设置候选机器相机配置原文；本方法只记录不校验，校验是显式动作。
    /// <para>分成两步是为了让界面可以边编辑边反复校验，并在候选非法时一次拿到多条错误。</para>
    /// </summary>
    /// <param name="json">机器相机配置 JSON。</param>
    /// <exception cref="ArgumentException">配置为空。</exception>
    public void SetCandidate(string json) => _revisions.SetCandidate(json);

    /// <summary>校验候选配置；本方法不抛配置异常，校验结果里带组合身份与不可用源清单。</summary>
    /// <returns>校验结果；候选缺失、解析失败或组合失败时为无效。</returns>
    public VisionAcquisitionMachineConfigurationValidationResult ValidateCandidate() =>
        _revisions.ValidateCandidate();

    /// <summary>发布候选配置：先校验，通过后追加一条新修订并清空候选。</summary>
    /// <returns>刚发布的修订。</returns>
    /// <exception cref="VisionSourceConfigurationException">
    /// 候选缺失或校验不通过；消息包含全部校验错误，同时可在失败后的配置快照里读到同一份文本。
    /// </exception>
    public VisionAcquisitionMachineConfigurationRevision Publish()
    {
        try
        {
            var revision = _revisions.Publish();
            _configurationFailure = null;
            return revision;
        }
        catch (VisionAcquisitionException exception)
        {
            _configurationFailure = Describe(exception);
            throw;
        }
    }

    /// <summary>回滚到任一历史修订：以该修订的配置内容追加一条新修订，不删除中间修订。</summary>
    /// <param name="revision">目标修订号；必须是历史中出现过的修订号。</param>
    /// <returns>由回滚产生的新修订。</returns>
    /// <exception cref="VisionSourceConfigurationException">修订号不存在，或目标修订内容在当前Catalog下校验不通过。</exception>
    public VisionAcquisitionMachineConfigurationRevision Rollback(int revision)
    {
        try
        {
            var rolledBack = _revisions.Rollback(revision);
            _configurationFailure = null;
            return rolledBack;
        }
        catch (VisionAcquisitionException exception)
        {
            _configurationFailure = Describe(exception);
            throw;
        }
    }

    /// <summary>
    /// 采样一份配置面板快照：当前生效修订、候选状态、已发布源与历史修订。
    /// <para>快照不包含配置原文——原文里可能有设备身份标识，面板不需要它。</para>
    /// </summary>
    /// <returns>配置面板快照。</returns>
    public AcquisitionConfigurationSnapshot CaptureConfiguration()
    {
        var catalog = _composition.SourceCatalog;
        var sources = new AcquisitionConfigurationSourceRow[catalog.Count];
        for (var index = 0; index < catalog.Count; index++)
        {
            var info = catalog[index];
            sources[index] = new AcquisitionConfigurationSourceRow(
                info,
                _composition.GetConfigurationSummary(info.SourceId));
        }

        var history = _revisions.History();
        var revisions = new AcquisitionConfigurationRevisionRow[history.Count];
        for (var index = 0; index < history.Count; index++)
            revisions[index] = new AcquisitionConfigurationRevisionRow(history[index]);

        var current = _revisions.Current;
        return new AcquisitionConfigurationSnapshot(
            current?.Revision,
            current?.CompositionId,
            _revisions.HasCandidate,
            _revisions.CandidateErrors,
            _configurationFailure,
            sources,
            revisions);
    }

    /// <summary>
    /// 采样一份监视面板快照：运行时整体状态与每个逻辑源一行诊断。
    /// <para>
    /// 运行时尚未启动或已经停止时同样可采样：行来自组合而不是来自已打开的会话，
    /// 因此面板在这两段时间里显示的是"Created/Stopped + 尚未打开"，而不是空白。
    /// </para>
    /// </summary>
    /// <returns>监视面板快照。</returns>
    public AcquisitionMonitorSnapshot CaptureMonitor()
    {
        var diagnostics = _runtime.GetDiagnostics();
        var rows = new AcquisitionMonitorRow[diagnostics.Count];
        for (var index = 0; index < diagnostics.Count; index++)
            rows[index] = new AcquisitionMonitorRow(diagnostics[index]);

        return new AcquisitionMonitorSnapshot(
            _runtime.RuntimeState,
            _runtime.CompositionId,
            _revisions.Current?.Revision ?? 0,
            DateTimeOffset.UtcNow,
            rows);
    }

    /// <summary>
    /// 对指定逻辑源执行一次试拍。
    /// <para>
    /// 调用前会确保运行时已经 <c>StartAsync</c>：未启动时先启动并保持启动状态（设备生命周期归宿主，
    /// 试拍不负责停止）；运行时已经停止时无法重启，作为失败结果返回原因。
    /// </para>
    /// <para>
    /// 除取消外的不成功都返回结果而不是异常：配置错误、资源冲突、设备离线、超时与SDK故障
    /// 分别落在 <see cref="AcquisitionTrialCaptureResult.FailureKind"/> 上，界面显示原因即可。
    /// 试拍会占用资源键上的采集互斥门，与节点采集遵守同一套共享策略。
    /// </para>
    /// </summary>
    /// <param name="sourceId">目标逻辑源标识。</param>
    /// <param name="request">采集请求；为空时使用本类默认超时（5 秒）。</param>
    /// <param name="cancellationToken">协作取消；取消原样传播为 <see cref="OperationCanceledException"/>。</param>
    /// <returns>试拍结果；成功时给出采集身份与图像元数据。</returns>
    /// <exception cref="ArgumentException">逻辑源标识为空。</exception>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    public async ValueTask<AcquisitionTrialCaptureResult> TrialCaptureAsync(
        string sourceId,
        VisionCaptureRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("试拍目标逻辑源标识不能为空。", nameof(sourceId));

        var target = sourceId.Trim();
        var effectiveRequest = request ?? new VisionCaptureRequest(DefaultTrialTimeout);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            using (var captured = await _runtime
                .CaptureAsync(
                    new VisionSourceReference(target),
                    effectiveRequest,
                    new VisionAcquisitionOwner(TrialOwnerId, "trial:" + target, "采集试拍"),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                var info = captured.Frame.Image.Info;
                return new AcquisitionTrialCaptureResult(
                    target,
                    succeeded: true,
                    watch.Elapsed,
                    captured.Metadata.CaptureId,
                    captured.Metadata.ProviderId,
                    captured.Metadata.ResourceKey,
                    captured.Metadata.CapturedAtUtc,
                    captured.Metadata.DeviceSequence,
                    info.Width,
                    info.Height,
                    info.Layout,
                    info.ByteLength);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AcquisitionTrialCaptureResult(
                target,
                succeeded: false,
                watch.Elapsed,
                failureKind: ClassifyFailure(exception),
                failureMessage: Describe(exception));
        }
    }

    /// <summary>确保运行时处于可采集状态：仅未启动时启动，从不停止。</summary>
    private async ValueTask EnsureStartedAsync(CancellationToken cancellationToken)
    {
        switch (_runtime.RuntimeState)
        {
            case EVisionRuntimeState.Created:
                await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
                return;
            case EVisionRuntimeState.Stopped:
                throw new VisionDeviceOfflineException(
                    "采集运行时已经停止，无法试拍；运行时只能启动一次，请重建运行时后再试。");
            default:
                return;
        }
    }

    /// <summary>
    /// 当前组合里已注册的Provider身份，按身份排序。
    /// <para>
    /// 只用组合对外的Provider注册清单，不能用 <see cref="VisionAcquisitionProviderComposition.Sources"/>：
    /// 发现的意义正是"还没有给这个Provider配置任何Source时先看见现场有哪些设备"，
    /// 按已发布Source反推会让尚未配置的Provider永远发现不到设备。
    /// </para>
    /// </summary>
    private IReadOnlyList<string> ProviderIds() =>
        _composition.Providers.Select(provider => provider.ProviderId).ToArray();

    /// <summary>当前组合里已发布Source占用的规范资源键；空键（未安装Type）不参与比对。</summary>
    private HashSet<string> ConfiguredResourceKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in _composition.Sources)
        {
            if (!string.IsNullOrWhiteSpace(binding.ResourceKey))
                keys.Add(binding.ResourceKey);
        }

        return keys;
    }

    /// <summary>候选的规范资源键是否已被当前机器配置引用；键缺失时无法比对，按未配置处理。</summary>
    private static bool IsConfigured(VisionDeviceDescriptor descriptor, HashSet<string> configuredKeys) =>
        !string.IsNullOrWhiteSpace(descriptor.CanonicalKey) && configuredKeys.Contains(descriptor.CanonicalKey!);

    /// <summary>失败文本：优先异常消息；消息为空时退回异常类型名，避免界面显示空原因。</summary>
    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

    /// <summary>把异常映射为界面可分组显示的失败类别。</summary>
    private static string ClassifyFailure(Exception exception)
    {
        switch (exception)
        {
            case VisionSourceConfigurationException:
                return "SourceConfiguration";
            case VisionResourceConflictException:
                return "ResourceConflict";
            case VisionDeviceOfflineException:
                return "DeviceOffline";
            case VisionParameterNotSupportedException:
                return "ParameterNotSupported";
            case VisionCaptureTimeoutException:
                return "CaptureTimeout";
            case VisionDataException:
                return "Data";
            case VisionProviderUnavailableException:
                return "ProviderUnavailable";
            case VisionAcquisitionException:
                return "Acquisition";
            default:
                return "Unexpected";
        }
    }
}