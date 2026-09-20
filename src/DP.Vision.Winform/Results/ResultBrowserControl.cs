using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DP.Vision.UI;

namespace DP.Vision.Winform;

/// <summary>原生WinForms视图浏览器：视图下拉选择、图层多选和独立画布。</summary>
public sealed class ResultBrowserControl : UserControl, IViewDisplaySink
{
    private readonly VisionCanvasControl _canvas = new VisionCanvasControl { Dock = DockStyle.Fill };
    private readonly ComboBox _views = new ComboBox
    {
        Name = "ViewSelector",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 180,
    };
    private readonly CheckedListBox _layers = new CheckedListBox
    {
        Name = "LayerSelector",
        CheckOnClick = true,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
    };
    private readonly Button _layerButton = new Button { Text = "图层 ▼", AutoSize = true };
    private readonly Label _status = new Label
    {
        Name = "BrowserStatus",
        Dock = DockStyle.Bottom,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly Label _viewLabel = new Label
    {
        Text = "视图",
        AutoSize = true,
        Margin = new Padding(6, 8, 0, 0),
    };
    private readonly ToolStripDropDown _popup = new ToolStripDropDown { Padding = Padding.Empty };
    private readonly ToolTip _details = new ToolTip { AutoPopDelay = 20000 };
    private readonly Timer _timer = new Timer { Interval = 33 };
    private readonly ResultBrowserPresenter _presenter;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _updating;
    private ResultBrowserSnapshot? _shown;

    /// <summary>创建默认预算的独立浏览器，可用于设计器。</summary>
    public ResultBrowserControl()
        : this(new ResultBrowserOptions()) { }

    /// <summary>创建指定保留上限的浏览器，控件拥有内部会话和画布。</summary>
    /// <param name="options">结果预览保留上限。</param>
    public ResultBrowserControl(ResultBrowserOptions options)
    {
        Results = new ResultBrowserSession(
            options ?? throw new ArgumentNullException(nameof(options), "结果浏览器配置不能为空。")
        );
        _presenter = new ResultBrowserPresenter(Results, _canvas);
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            WrapContents = false,
            AutoScroll = true,
        };
        toolbar.Controls.Add(_viewLabel);
        toolbar.Controls.Add(_views);
        toolbar.Controls.Add(_layerButton);
        var fit = new Button { Text = "适应窗口", AutoSize = true };
        fit.Click += (_, __) => _canvas.FitToWindow();
        toolbar.Controls.Add(fit);
        Controls.Add(_canvas);
        Controls.Add(_status);
        Controls.Add(toolbar);

        var panel = new Panel { Width = 330, Height = 300 };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38 };
        AddAction(actions, "全选", () => ChangeAll(true));
        AddAction(actions, "全不选", () => ChangeAll(false));
        AddAction(actions, "恢复默认", () => ChangeAll(null));
        panel.Controls.Add(_layers);
        panel.Controls.Add(actions);
        _popup.Items.Add(
            new ToolStripControlHost(panel)
            {
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                AutoSize = false,
                Size = panel.Size,
            }
        );
        _layerButton.Click += (_, __) => _popup.Show(_layerButton, new Point(0, _layerButton.Height));
        _views.SelectedIndexChanged += (_, __) =>
        {
            if (!_updating && _views.SelectedItem is BrowserChoice choice)
            {
                if (_shown != null)
                    Results.TrySelectView(_shown, choice.Id);
                RefreshResults();
            }
        };
        _layers.ItemCheck += (_, e) =>
        {
            if (!_updating && _shown != null && _layers.Items[e.Index] is BrowserLayerChoice layer)
                Results.TrySetLayerVisible(_shown, layer.Id, e.NewValue == CheckState.Checked);
            // 等ItemCheck结束后由定时器统一刷新，避免在原生列表事件中重建Items。
        };
        _timer.Tick += (_, __) => RefreshResults();
        _timer.Start();
        RefreshResults();
    }

    /// <summary>借用内部线程安全会话，可选择视图和图层；客户不得释放此会话。</summary>
    public ResultBrowserSession Results { get; }

    /// <summary>当前实际显示的图像内容身份。</summary>
    public string? DisplayedFrameId => _canvas.DisplayedFrameId;

    /// <summary>画布显示设置，只允许UI线程修改。</summary>
    public CanvasOptions Options
    {
        get => _canvas.Options;
        set => _canvas.Options = value;
    }

    /// <inheritdoc/>
    public bool SetViews(IEnumerable<VisionView> views) => Results.SetViews(views);

    /// <summary>清空视图集合，允许后台调用；UI在下次刷新清理底图。</summary>
    public void ClearViews() => Results.Clear();

    /// <summary>在UI线程立即应用最新结果；通常由控件内部定时器合并刷新。</summary>
    public void RefreshResults()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("界面刷新必须在控件所属线程执行。");
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(ResultBrowserControl), "结果浏览器已释放。");
        var snapshot = _presenter.Refresh();
        if (snapshot == null)
            return;
        _shown = snapshot;
        _updating = true;
        try
        {
            Fill(_views, snapshot.Views.ToArray(), snapshot.ViewId);
            _views.Visible = _viewLabel.Visible = snapshot.Views.Count > 1;
            var oldLayers = _layers.Items.Cast<BrowserLayerChoice>().ToArray();
            if (
                !oldLayers
                    .Select(l => Tuple.Create(l.Id, l.Name))
                    .SequenceEqual(snapshot.Layers.Select(l => Tuple.Create(l.Id, l.Name)))
            )
            {
                _layers.Items.Clear();
                foreach (var layer in snapshot.Layers)
                    _layers.Items.Add(layer);
            }
            for (int i = 0; i < snapshot.Layers.Count; i++)
                _layers.SetItemChecked(i, snapshot.Layers[i].Visible);
            _layerButton.Enabled = snapshot.Layers.Count > 0;
            _layerButton.Text =
                "图层 " + snapshot.Layers.Count(l => l.Visible) + "/" + snapshot.Layers.Count + " ▼";
            _status.Text = snapshot.Status;
            _details.SetToolTip(_status, _status.Text);
        }
        finally
        {
            _updating = false;
        }
    }

    private static void Fill(ComboBox combo, BrowserChoice[] choices, string? selected)
    {
        // 名称和键未变化时不重建下拉列表，减少集合更新对正在选择的用户的干扰。
        var old = combo.Items.Cast<BrowserChoice>().ToArray();
        if (
            !old.Select(c => Tuple.Create(c.Id, c.Name))
                .SequenceEqual(choices.Select(c => Tuple.Create(c.Id, c.Name)))
        )
        {
            combo.Items.Clear();
            combo.Items.AddRange(choices);
        }
        combo.SelectedItem = combo.Items.Cast<BrowserChoice>().FirstOrDefault(c => c.Id == selected);
    }

    private void ChangeAll(bool? visible)
    {
        if (_shown != null)
            Results.TrySetAllLayersVisible(_shown, visible);
    }

    private void AddAction(FlowLayoutPanel panel, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, __) =>
        {
            action();
            RefreshResults();
        };
        panel.Controls.Add(button);
    }

    /// <summary>停止刷新，释放内部预览、弹出面板及画布资源。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                _details.Dispose();
                _popup.Dispose();
                Results.Dispose();
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }
}
