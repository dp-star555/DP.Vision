using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DP.Vision.Acquisition;
using DP.Vision.Acquisition.Management;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace DP.Vision.Acquisition.WinForms;

/// <summary>
/// 采集管理控件：把 <see cref="AcquisitionManagementPresenter"/> 的快照绑定到"设备发现 / 配置修订 / 运行监控"三个页签。
/// <para>
/// 本控件只做视图绑定：不枚举设备、不解释机器配置、不驱动运行时，操作员的每个动作都原样转成对表现层的调用。
/// 界面唯一的自主行为是按构造参数周期性采样监视快照，采样本身也是只读的。
/// </para>
/// <para>
/// 失败一律如实显示：发现失败、发布失败、回滚失败与试拍失败都给出表现层报告的原因文本，
/// 并且"某个Provider没发现成"与"现场确实没有设备"在界面上分属两块区域，不允许互相冒充。
/// </para>
/// </summary>
public sealed class AcquisitionManagementControl : UserControl
{
    /// <summary>缺失值占位符：用于区分"没有观测"与"观测到0"。</summary>
    private const string Missing = "—";

    /// <summary>运行监控页的列顺序；<see cref="RenderMonitorRow"/>按下标写入，两者必须保持一致。</summary>
    private static readonly string[] MonitorColumns =
    {
        "SourceId",
        "状态",
        "连接消息",
        "取流",
        "连接修订",
        "Epoch",
        "接收",
        "领取",
        "超龄",
        "无代次拒绝",
        "溢出拒绝",
        "待领取数",
        "待领取字节",
        "待领取帧水位",
        "待领取字节水位",
        "设备序号缺口",
        "落地次数",
        "落地总字节",
        "落地总耗时ms",
        "最近落地字节",
        "最近落地耗时ms",
        "故障类别",
        "故障消息",
    };

    private readonly AcquisitionManagementPresenter _presenter;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly Dictionary<string, ListViewItem> _monitorItems =
        new Dictionary<string, ListViewItem>(StringComparer.Ordinal);

    private readonly Button _discoveryRefresh = MakeButton("DiscoveryRefresh", "刷新");
    private readonly Button _discoveryCopyKey = MakeButton("DiscoveryCopyKey", "复制资源键");
    private readonly ListView _discoveryDevices = Grid(
        "DiscoveryDevices",
        true,
        "显示名",
        "厂商",
        "型号",
        "序列号",
        "规范资源键",
        "绑定身份",
        "是否已被配置引用");
    private readonly ListView _discoveryFailures = Grid("DiscoveryFailures", false, "ProviderId", "发现失败原因");
    private readonly Label _discoveryStatus = new Label
    {
        Name = "DiscoveryStatus",
        Dock = DockStyle.Bottom,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private readonly TextBox _configurationJson = new TextBox
    {
        Name = "ConfigurationJson",
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Top,
        Height = 160,
    };
    private readonly Button _configurationValidate = MakeButton("ConfigurationValidate", "校验");
    private readonly Button _configurationPublish = MakeButton("ConfigurationPublish", "发布");
    private readonly Button _configurationRollback = MakeButton("ConfigurationRollback", "回滚选中修订");
    private readonly Button _configurationTrial = MakeButton("ConfigurationTrial", "试拍选中源");
    private readonly Button _configurationRefresh = MakeButton("ConfigurationRefresh", "刷新");
    private readonly TextBox _configurationResult = new TextBox
    {
        Name = "ConfigurationResult",
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
    };
    private readonly ListView _configurationSources = Grid(
        "ConfigurationSources",
        false,
        "SourceId",
        "AcquisitionTypeId",
        "规范资源键",
        "采集形态",
        "采集模式",
        "共享策略",
        "是否可用",
        "诊断",
        "配置摘要");
    private readonly ListView _configurationHistory = Grid(
        "ConfigurationHistory",
        false,
        "Revision",
        "组合身份",
        "发布时刻(UTC)",
        "恢复自修订",
        "是否回滚");

    private readonly Label _monitorStatus = new Label
    {
        Name = "MonitorStatus",
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly CheckBox _monitorPause = new CheckBox
    {
        Name = "MonitorPause",
        Text = "暂停刷新",
        AutoSize = true,
        Margin = new Padding(6, 8, 0, 0),
    };
    private readonly ListView _monitorRows = Grid("MonitorRows", true, MonitorColumns);
    private readonly WinFormsTimer _monitorTimer = new WinFormsTimer();

    /// <summary>创建采集管理控件。</summary>
    /// <param name="presenter">采集管理表现层；控件的全部动作都转成对它的调用，本控件不释放它。</param>
    /// <param name="refreshIntervalMilliseconds">运行监控页的采样间隔（毫秒），默认 500。</param>
    /// <exception cref="ArgumentNullException">表现层为空。</exception>
    public AcquisitionManagementControl(
        AcquisitionManagementPresenter presenter,
        int refreshIntervalMilliseconds = 500)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter), "采集管理表现层不能为空。");
        _monitorTimer.Interval = refreshIntervalMilliseconds;
        Size = new Size(1024, 720);

        var tabs = new TabControl { Name = "AcquisitionTabs", Dock = DockStyle.Fill };
        tabs.TabPages.Add(DiscoveryPage());
        tabs.TabPages.Add(ConfigurationPage());
        tabs.TabPages.Add(MonitorPage());
        Controls.Add(tabs);

        _discoveryRefresh.Click += OnDiscoveryRefresh;
        _discoveryCopyKey.Click += OnCopyDiscoveryKey;
        _configurationValidate.Click += OnValidateCandidate;
        _configurationPublish.Click += OnPublish;
        _configurationRollback.Click += OnRollback;
        _configurationTrial.Click += OnTrialCapture;
        _configurationRefresh.Click += OnRefreshConfiguration;
        _monitorTimer.Tick += OnMonitorTick;

        _discoveryStatus.Text = "尚未发现设备：点“刷新”枚举现场候选。";
        RefreshConfiguration();
        RefreshMonitor();
        _monitorTimer.Start();
    }

    /// <summary>构造"设备发现"页：候选、Provider失败诊断与候选汇总，三者互不遮挡。</summary>
    private TabPage DiscoveryPage()
    {
        var page = new TabPage("设备发现");
        page.Controls.Add(_discoveryDevices);
        page.Controls.Add(
            Section(
                "发现失败诊断（这些 Provider 未完成发现，不代表现场没有设备）",
                _discoveryFailures,
                120));
        page.Controls.Add(_discoveryStatus);
        page.Controls.Add(Toolbar(_discoveryRefresh, _discoveryCopyKey));
        return page;
    }

    /// <summary>构造"配置修订"页：配置原文、结果区、已发布源与修订历史。</summary>
    private TabPage ConfigurationPage()
    {
        var page = new TabPage("配置修订");
        page.Controls.Add(_configurationSources);
        page.Controls.Add(Section("修订历史（选中一行即为回滚目标）", _configurationHistory, 170));
        page.Controls.Add(Section("结果", _configurationResult, 150));
        page.Controls.Add(_configurationJson);
        page.Controls.Add(
            Toolbar(
                _configurationValidate,
                _configurationPublish,
                _configurationRollback,
                _configurationTrial,
                _configurationRefresh));
        return page;
    }

    /// <summary>构造"运行监控"页：顶部状态标签、暂停开关与逐源诊断表。</summary>
    private TabPage MonitorPage()
    {
        var page = new TabPage("运行监控");
        page.Controls.Add(_monitorRows);
        page.Controls.Add(_monitorStatus);
        page.Controls.Add(Toolbar(_monitorPause));
        return page;
    }

    /// <summary>设备发现：异步枚举候选；失败只更新汇总文本，不制造一条假候选。</summary>
    private async void OnDiscoveryRefresh(object? sender, EventArgs e)
    {
        _discoveryRefresh.Enabled = false;
        try
        {
            var snapshot = await _presenter.DiscoverAsync(_lifetime.Token);
            if (IsDisposed)
            {
                return;
            }

            ShowDiscovery(snapshot);
        }
        catch (OperationCanceledException)
        {
            // 控件释放或调用方取消：保持界面现状，不把它显示成一次发现失败。
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                _discoveryStatus.Text = "设备发现失败：" + Describe(exception);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _discoveryRefresh.Enabled = true;
            }
        }
    }

    /// <summary>把候选与失败诊断填进各自的列表，并汇总"共N个候选、M个Provider发现失败"。</summary>
    /// <param name="snapshot">一次设备发现的结果。</param>
    private void ShowDiscovery(AcquisitionDiscoverySnapshot snapshot)
    {
        _discoveryDevices.BeginUpdate();
        try
        {
            _discoveryDevices.Items.Clear();
            foreach (var device in snapshot.Devices)
            {
                var item = new ListViewItem(device.DisplayName);
                item.SubItems.Add(Display(device.VendorName));
                item.SubItems.Add(Display(device.ModelName));
                item.SubItems.Add(Display(device.SerialNumber));
                item.SubItems.Add(Display(device.CanonicalKey));
                item.SubItems.Add(Display(device.ProviderBindingId));
                item.SubItems.Add(device.IsConfigured ? "已引用" : "未引用");
                item.Tag = device;
                _discoveryDevices.Items.Add(item);
            }
        }
        finally
        {
            _discoveryDevices.EndUpdate();
        }

        _discoveryFailures.BeginUpdate();
        try
        {
            _discoveryFailures.Items.Clear();
            foreach (var failure in snapshot.ProviderFailures)
            {
                var item = new ListViewItem(failure.ProviderId);
                item.SubItems.Add(failure.Message);
                _discoveryFailures.Items.Add(item);
            }
        }
        finally
        {
            _discoveryFailures.EndUpdate();
        }

        var summary =
            "共 "
            + snapshot.Devices.Count.ToString(CultureInfo.InvariantCulture)
            + " 个候选、"
            + snapshot.ProviderFailures.Count.ToString(CultureInfo.InvariantCulture)
            + " 个 Provider 发现失败。";
        _discoveryStatus.Text =
            snapshot.ProviderFailures.Count == 0
                ? summary + "全部 Provider 已完成发现。"
                : summary + "发现失败的 Provider 并不代表现场没有设备，请先排除插件缺失或 SDK 故障。";
    }

    /// <summary>复制选中候选的规范资源键；没有规范资源键时退回序列号，两者都缺则明确拒绝。</summary>
    private void OnCopyDiscoveryKey(object? sender, EventArgs e)
    {
        var device = SelectedDevice();
        if (device is null)
        {
            _discoveryStatus.Text = "请先在候选列表中选中一台设备，再复制资源键。";
            return;
        }

        var text = string.IsNullOrWhiteSpace(device.CanonicalKey) ? device.SerialNumber : device.CanonicalKey;
        if (string.IsNullOrWhiteSpace(text))
        {
            _discoveryStatus.Text = "该候选既无规范资源键也无序列号，没有可复制的设备身份。";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _discoveryStatus.Text = "已复制 " + device.DisplayName + " 的身份：" + text;
        }
        catch (Exception exception)
        {
            _discoveryStatus.Text = "复制到剪贴板失败：" + Describe(exception);
        }
    }

    /// <summary>校验候选配置：先把编辑框内容设为候选，再把全部错误一次性显示在结果区。</summary>
    private void OnValidateCandidate(object? sender, EventArgs e)
    {
        try
        {
            _presenter.SetCandidate(_configurationJson.Text);
            ShowValidation(_presenter.ValidateCandidate());
        }
        catch (ArgumentException exception)
        {
            _configurationResult.Text = "校验未开始：" + Describe(exception);
        }
        catch (Exception exception)
        {
            _configurationResult.Text = "校验未完成：" + Describe(exception);
        }
    }

    /// <summary>把校验结论、组合身份、逻辑源清单与未安装Type的源显示在结果区；多条错误全部列出。</summary>
    /// <param name="validation">候选校验结果。</param>
    private void ShowValidation(VisionAcquisitionMachineConfigurationValidationResult validation)
    {
        var builder = new StringBuilder();
        builder.AppendLine(validation.IsValid ? "候选校验：通过。" : "候选校验：不通过。");

        if (!validation.IsValid)
        {
            builder.AppendLine(
                "共 " + validation.Errors.Count.ToString(CultureInfo.InvariantCulture) + " 条错误：");
            for (var index = 0; index < validation.Errors.Count; index++)
            {
                builder.AppendLine(
                    (index + 1).ToString(CultureInfo.InvariantCulture) + ". " + validation.Errors[index]);
            }

            _configurationResult.Text = builder.ToString();
            return;
        }

        builder.AppendLine("组合身份：" + Display(validation.CompositionId));
        var sources = validation.SourceIds ?? Array.Empty<string>();
        builder.AppendLine(
            "逻辑源（" + sources.Count.ToString(CultureInfo.InvariantCulture) + "）：" + Join(sources));
        var unavailable = validation.UnavailableSourceIds ?? Array.Empty<string>();
        builder.AppendLine(
            "AcquisitionType 未安装的源（"
            + unavailable.Count.ToString(CultureInfo.InvariantCulture)
            + "）："
            + Join(unavailable));
        _configurationResult.Text = builder.ToString();
    }

    /// <summary>发布候选配置：成功给出新修订，失败把消息显示出来而不是崩溃。</summary>
    private void OnPublish(object? sender, EventArgs e)
    {
        try
        {
            _presenter.SetCandidate(_configurationJson.Text);
            var revision = _presenter.Publish();
            _configurationResult.Text =
                "发布成功：修订 "
                + revision.Revision.ToString(CultureInfo.InvariantCulture)
                + "，组合 "
                + revision.CompositionId
                + "。"
                + Environment.NewLine
                + "配置原文已写入修订历史，可直接用于回滚与差异比对。";
        }
        catch (ArgumentException exception)
        {
            _configurationResult.Text = "发布未开始：" + Describe(exception);
        }
        catch (Exception exception)
        {
            _configurationResult.Text =
                "发布失败："
                + Describe(exception)
                + Environment.NewLine
                + "发布没有成功，未在历史中产生新修订；同一份失败文本已留档在配置快照里，可点“刷新”核对后再试。";
        }
        finally
        {
            RefreshConfiguration();
        }
    }

    /// <summary>回滚到选中的历史修订：失败同样显示原因，并保留历史不动。</summary>
    private void OnRollback(object? sender, EventArgs e)
    {
        var target = SelectedRevision();
        if (target is null)
        {
            _configurationResult.Text = "请先在“修订历史”中选中一行，再执行回滚。";
            return;
        }

        var revision = target.Value.ToString(CultureInfo.InvariantCulture);
        try
        {
            var rolledBack = _presenter.Rollback(target.Value);
            _configurationResult.Text =
                "回滚成功：以修订 "
                + revision
                + " 追加了新修订 "
                + rolledBack.Revision.ToString(CultureInfo.InvariantCulture)
                + "；中间修订未被删除，历史只追加。";
        }
        catch (Exception exception)
        {
            _configurationResult.Text =
                "回滚到修订 "
                + revision
                + " 失败："
                + Describe(exception)
                + Environment.NewLine
                + "回滚没有产生新修订；若该修订原文在当前 Catalog 下已不合法，请修改配置后重新发布。";
        }
        finally
        {
            RefreshConfiguration();
        }
    }

    /// <summary>试拍选中的已发布源：执行期间禁用按钮，完成后恢复，并把成败原因写进结果区。</summary>
    private async void OnTrialCapture(object? sender, EventArgs e)
    {
        var sourceId = SelectedSourceId();
        if (sourceId is null)
        {
            _configurationResult.Text = "请先在“已发布源”列表中选中一个逻辑源，再试拍。";
            return;
        }

        _configurationTrial.Enabled = false;
        try
        {
            var result = await _presenter.TrialCaptureAsync(sourceId, cancellationToken: _lifetime.Token);
            if (!IsDisposed)
            {
                _configurationResult.Text = DescribeTrial(result);
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                _configurationResult.Text = "试拍已取消。";
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                _configurationResult.Text = "试拍未完成：" + Describe(exception);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _configurationTrial.Enabled = true;
            }
        }
    }

    /// <summary>把一次试拍的成败信息整理成可直接阅读的结果文本。</summary>
    /// <param name="result">表现层给出的试拍结果。</param>
    /// <returns>结果区文本。</returns>
    private static string DescribeTrial(AcquisitionTrialCaptureResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("试拍源：" + result.SourceId);
        builder.AppendLine("结果：" + (result.Succeeded ? "成功" : "失败"));
        builder.AppendLine(
            "耗时："
            + result.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)
            + " ms");

        if (!result.Succeeded)
        {
            builder.AppendLine("失败类别：" + Display(result.FailureKind));
            builder.AppendLine("原因：" + Display(result.FailureMessage));
            return builder.ToString();
        }

        builder.AppendLine("采集身份：" + Display(result.CaptureId));
        builder.AppendLine("Provider：" + Display(result.ProviderId));
        builder.AppendLine("资源键：" + Display(result.ResourceKey));
        builder.AppendLine("采集时刻(UTC)：" + Utc(result.CapturedAtUtc));
        builder.AppendLine(
            "设备序号：" + (result.DeviceSequence?.ToString(CultureInfo.InvariantCulture) ?? Missing));
        builder.AppendLine(
            "尺寸："
            + (result.ImageWidth?.ToString(CultureInfo.InvariantCulture) ?? Missing)
            + " x "
            + (result.ImageHeight?.ToString(CultureInfo.InvariantCulture) ?? Missing));
        builder.AppendLine("像素布局：" + (result.PixelLayout?.ToString() ?? Missing));
        builder.AppendLine(
            "字节数：" + (result.ImageByteLength?.ToString(CultureInfo.InvariantCulture) ?? Missing));
        return builder.ToString();
    }

    /// <summary>手动刷新配置页：重新采样已发布源与修订历史。</summary>
    private void OnRefreshConfiguration(object? sender, EventArgs e)
    {
        RefreshConfiguration();
    }

    /// <summary>采样配置面板快照并刷新两个列表；采样本身不改动配置，也不改动结果区。</summary>
    private void RefreshConfiguration()
    {
        if (IsDisposed)
        {
            return;
        }

        AcquisitionConfigurationSnapshot snapshot;
        try
        {
            snapshot = _presenter.CaptureConfiguration();
        }
        catch (Exception exception)
        {
            _configurationResult.Text = "配置采样失败：" + Describe(exception);
            return;
        }

        _configurationSources.BeginUpdate();
        try
        {
            _configurationSources.Items.Clear();
            foreach (var row in snapshot.Sources)
            {
                var item = new ListViewItem(row.SourceId);
                item.SubItems.Add(Display(row.AcquisitionTypeId));
                item.SubItems.Add(Display(row.ResourceKey));
                item.SubItems.Add(row.Kind?.ToString() ?? Missing);
                item.SubItems.Add(row.AcquisitionMode.ToString());
                item.SubItems.Add(row.SharingPolicy.ToString());
                item.SubItems.Add(row.IsAvailable ? "可用" : "不可用");
                item.SubItems.Add(Display(row.Diagnostic));
                item.SubItems.Add(Display(row.ConfigurationSummary));
                item.Tag = row.SourceId;
                _configurationSources.Items.Add(item);
            }
        }
        finally
        {
            _configurationSources.EndUpdate();
        }

        _configurationHistory.BeginUpdate();
        try
        {
            _configurationHistory.Items.Clear();
            foreach (var row in snapshot.History)
            {
                var item = new ListViewItem(row.Revision.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(row.CompositionId);
                item.SubItems.Add(Utc(row.PublishedAtUtc));
                item.SubItems.Add(
                    row.RestoredFromRevision?.ToString(CultureInfo.InvariantCulture) ?? Missing);
                item.SubItems.Add(row.IsRollback ? "是" : "否");
                item.Tag = row.Revision;
                _configurationHistory.Items.Add(item);
            }
        }
        finally
        {
            _configurationHistory.EndUpdate();
        }
    }

    /// <summary>定时器回调：未暂停时重新采样运行监控；定时器在UI线程触发，无需跨线程调度。</summary>
    private void OnMonitorTick(object? sender, EventArgs e)
    {
        if (_monitorPause.Checked)
        {
            return;
        }

        RefreshMonitor();
    }

    /// <summary>采样监视快照：顶部状态给出运行时/组合/修订/采样时刻，下列每源一行。</summary>
    private void RefreshMonitor()
    {
        if (IsDisposed)
        {
            return;
        }

        AcquisitionMonitorSnapshot snapshot;
        try
        {
            snapshot = _presenter.CaptureMonitor();
        }
        catch (Exception exception)
        {
            _monitorStatus.Text = "运行监控采样失败：" + Describe(exception);
            return;
        }

        var revision =
            snapshot.MachineConfigurationRevision == 0
                ? Missing
                : snapshot.MachineConfigurationRevision.ToString(CultureInfo.InvariantCulture);
        _monitorStatus.Text =
            "运行时 "
            + snapshot.RuntimeState
            + "；组合 "
            + Display(snapshot.CompositionId)
            + "；配置修订 "
            + revision
            + "；采样 "
            + Utc(snapshot.CapturedAtUtc)
            + "（UTC）";

        var live = new HashSet<string>(StringComparer.Ordinal);
        _monitorRows.BeginUpdate();
        try
        {
            foreach (var row in snapshot.Rows)
            {
                live.Add(row.SourceId);
                RenderMonitorRow(row);
            }

            foreach (var stale in _monitorItems.Keys.Where(key => !live.Contains(key)).ToArray())
            {
                _monitorRows.Items.Remove(_monitorItems[stale]);
                _monitorItems.Remove(stale);
            }
        }
        finally
        {
            _monitorRows.EndUpdate();
        }
    }

    /// <summary>
    /// 按 <see cref="MonitorColumns"/> 的顺序写入一行；已存在的行原地更新而不是重建，
    /// 这样滚动位置与选中状态在周期刷新中不会被反复重置。
    /// </summary>
    /// <param name="row">表现层给出的一行诊断。</param>
    private void RenderMonitorRow(AcquisitionMonitorRow row)
    {
        if (!_monitorItems.TryGetValue(row.SourceId, out var item))
        {
            item = new ListViewItem(row.SourceId);
            for (var index = 1; index < MonitorColumns.Length; index++)
            {
                item.SubItems.Add(string.Empty);
            }

            _monitorItems.Add(row.SourceId, item);
            _monitorRows.Items.Add(item);
        }

        var transfer = row.Transfer;
        var values = new string[MonitorColumns.Length];
        values[0] = row.SourceId;
        values[1] = row.ConnectionState.ToString();
        values[2] = Display(row.ConnectionMessage);
        values[3] = Display(row.TransferState);
        values[4] = row.ConnectionRevision.ToString(CultureInfo.InvariantCulture);
        values[5] = row.Epoch.ToString(CultureInfo.InvariantCulture);
        values[6] = row.FramesReceived.ToString(CultureInfo.InvariantCulture);
        values[7] = row.FramesClaimed.ToString(CultureInfo.InvariantCulture);
        values[8] = row.FramesExpired.ToString(CultureInfo.InvariantCulture);
        values[9] = row.FramesRejectedWithoutEpoch.ToString(CultureInfo.InvariantCulture);
        values[10] = row.FramesRejectedOverflow.ToString(CultureInfo.InvariantCulture);
        values[11] = row.InboxCount.ToString(CultureInfo.InvariantCulture);
        values[12] = row.InboxBytes.ToString(CultureInfo.InvariantCulture);
        values[13] = row.InboxHighWatermark.ToString(CultureInfo.InvariantCulture);
        values[14] = row.BytesHighWatermark.ToString(CultureInfo.InvariantCulture);
        values[15] = row.DeviceSequenceGaps.ToString(CultureInfo.InvariantCulture);

        // 未上报观测时必须显示占位符：折算成 0 会让"没有观测"与"观测到 0 字节"变得无法区分。
        values[16] = transfer is null ? Missing : transfer.Count.ToString(CultureInfo.InvariantCulture);
        values[17] = transfer is null ? Missing : transfer.BytesTotal.ToString(CultureInfo.InvariantCulture);
        values[18] =
            transfer is null
                ? Missing
                : transfer.DurationTotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture);
        values[19] = transfer is null ? Missing : transfer.LastBytes.ToString(CultureInfo.InvariantCulture);
        values[20] =
            transfer is null
                ? Missing
                : transfer.LastDurationMilliseconds.ToString("F2", CultureInfo.InvariantCulture);
        values[21] = Display(row.FaultKind);
        values[22] = Display(row.FaultMessage);

        for (var index = 0; index < values.Length; index++)
        {
            item.SubItems[index].Text = values[index];
        }

        item.ForeColor = row.IsFaulted ? Color.Red : _monitorRows.ForeColor;
        item.Tag = row.SourceId;
    }

    /// <summary>当前选中的候选设备；未选中时为空。</summary>
    private AcquisitionDiscoveredDevice? SelectedDevice()
    {
        if (_discoveryDevices.SelectedItems.Count == 0)
        {
            return null;
        }

        return _discoveryDevices.SelectedItems[0].Tag as AcquisitionDiscoveredDevice;
    }

    /// <summary>当前选中的已发布逻辑源标识；未选中时为空。</summary>
    private string? SelectedSourceId()
    {
        if (_configurationSources.SelectedItems.Count == 0)
        {
            return null;
        }

        return _configurationSources.SelectedItems[0].Tag as string;
    }

    /// <summary>当前选中的历史修订号；未选中时为空。</summary>
    private int? SelectedRevision()
    {
        if (_configurationHistory.SelectedItems.Count == 0)
        {
            return null;
        }

        return _configurationHistory.SelectedItems[0].Tag as int?;
    }

    /// <summary>创建带名称与自适应宽度的明细列表。</summary>
    private static ListView Grid(string name, bool multiSelect, params string[] columns)
    {
        var list = new ListView
        {
            Name = name,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = multiSelect,
            HideSelection = false,
            Dock = DockStyle.Fill,
        };
        foreach (var column in columns)
        {
            list.Columns.Add(column, -2, HorizontalAlignment.Left);
        }

        return list;
    }

    /// <summary>创建工具栏：不换行，溢出时滚动。</summary>
    private static FlowLayoutPanel Toolbar(params Control[] buttons)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            WrapContents = false,
            AutoScroll = true,
        };
        panel.Controls.AddRange(buttons);
        return panel;
    }

    /// <summary>把一块内容连同标题包成停靠面板；内容自行设为 Fill。</summary>
    private static Panel Section(string title, Control content, int height)
    {
        var header = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 20,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var panel = new Panel { Dock = DockStyle.Bottom, Height = height };
        panel.Controls.Add(content);
        panel.Controls.Add(header);
        return panel;
    }

    /// <summary>创建自适应尺寸的按钮。</summary>
    private static Button MakeButton(string name, string text)
    {
        return new Button { Name = name, Text = text, AutoSize = true };
    }

    /// <summary>缺失值的统一显示：空串与空引用都显示占位符，避免界面出现无法分辨的空白。</summary>
    /// <remarks>net48 的引用程序集不带可空性标注，这里的 <c>!</c> 是让两个目标框架同样通过可空分析的必需写法。</remarks>
    private static string Display(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? Missing : value!;
    }

    /// <summary>UTC 时刻的统一格式；为空时显示占位符。</summary>
    private static string Utc(DateTimeOffset? value)
    {
        return value is null
            ? Missing
            : value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>用顿号连接清单；空清单显示占位符。</summary>
    private static string Join(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? Missing : string.Join("、", values);
    }

    /// <summary>失败文本：优先异常消息；消息为空时退回类型名，避免结果显示空白原因。</summary>
    private static string Describe(Exception exception)
    {
        return string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
    }

    /// <summary>停止定时刷新并取消尚未完成的异步动作，避免控件释放后仍触碰界面。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                _monitorTimer.Stop();
                _monitorTimer.Tick -= OnMonitorTick;
                _monitorTimer.Dispose();
                _lifetime.Cancel();
                _lifetime.Dispose();
                _monitorItems.Clear();
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }
}