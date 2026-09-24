using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DP.Vision.UI;

namespace DP.Vision.WPF;

/// <summary>原生WPF视图浏览器，无WinForms嵌套；宿主结束使用时必须Dispose以归还预览源。</summary>
public sealed class ResultBrowserControl : UserControl, IViewDisplaySink, IDisposable
{
    private readonly VisionCanvasControl _canvas = new VisionCanvasControl();
    private readonly ComboBox _views = new ComboBox
    {
        Name = "ViewSelector",
        Width = 180,
        Margin = new Thickness(4),
    };
    private readonly StackPanel _layers = new StackPanel { Name = "LayerSelector" };
    private readonly Button _layerButton = new Button
    {
        Content = "图层 ▼",
        Margin = new Thickness(4),
        Padding = new Thickness(8, 2, 8, 2),
    };
    private readonly TextBlock _status = new TextBlock
    {
        Name = "BrowserStatus",
        Margin = new Thickness(6),
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = 40,
    };
    private readonly TextBlock _viewLabel = new TextBlock
    {
        Text = "视图",
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Popup _popup = new Popup
    {
        StaysOpen = false,
        Placement = PlacementMode.Bottom,
        AllowsTransparency = true,
    };
    private readonly DispatcherTimer _timer;
    private readonly ResultBrowserPresenter _presenter;
    private bool _updating,
        _disposed;
    private ResultBrowserSnapshot? _shown;

    /// <summary>创建默认预算浏览器，支持XAML实例化。</summary>
    public ResultBrowserControl()
        : this(new ResultBrowserOptions()) { }

    /// <summary>创建独立结果浏览器及其预览会话。</summary>
    /// <param name="options">结果预览保留上限。</param>
    public ResultBrowserControl(ResultBrowserOptions options)
    {
        Results = new ResultBrowserSession(
            options ?? throw new ArgumentNullException(nameof(options), "结果浏览器配置不能为空。")
        );
        _presenter = new ResultBrowserPresenter(Results, _canvas);
        SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);
        SetResourceReference(ForegroundProperty, SystemColors.ControlTextBrushKey);
        var root = new DockPanel();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(_viewLabel);
        toolbar.Children.Add(_views);
        toolbar.Children.Add(_layerButton);
        var fit = new Button { Content = "适应窗口", Margin = new Thickness(4) };
        fit.Click += (_, __) => _canvas.FitToWindow();
        toolbar.Children.Add(fit);
        var toolbarScroll = new ScrollViewer
        {
            Content = toolbar,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        DockPanel.SetDock(toolbarScroll, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(toolbarScroll);
        root.Children.Add(_status);
        root.Children.Add(_canvas);
        Content = root;

        var popupPanel = new DockPanel { Width = 330, MaxHeight = 340 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        AddAction(actions, "全选", () => ChangeAll(true));
        AddAction(actions, "全不选", () => ChangeAll(false));
        AddAction(actions, "恢复默认", () => ChangeAll(null));
        DockPanel.SetDock(actions, Dock.Bottom);
        popupPanel.Children.Add(actions);
        popupPanel.Children.Add(
            new ScrollViewer { Content = _layers, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        );
        _popup.PlacementTarget = _layerButton;
        _popup.Child = new Border
        {
            Background = SystemColors.WindowBrush,
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = popupPanel,
        };
        _layerButton.Click += (_, __) => _popup.IsOpen = !_popup.IsOpen;
        _views.SelectionChanged += (_, __) =>
        {
            if (!_updating && _views.SelectedItem is BrowserChoice choice)
            {
                if (_shown != null)
                    Results.TrySelectView(_shown, choice.Id);
                RefreshResults();
            }
        };
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick += Tick;
        Loaded += (_, __) =>
        {
            if (!_disposed)
            {
                RefreshResults();
                _timer.Start();
            }
        };
        Unloaded += (_, __) =>
        {
            _timer.Stop();
            _popup.IsOpen = false;
        };
        RefreshResults();
    }

    /// <summary>借用的线程安全浏览会话，客户不得释放控件拥有的会话。</summary>
    public ResultBrowserSession Results { get; }

    /// <summary>当前实际显示的图像身份。</summary>
    public string? DisplayedFrameId => _canvas.DisplayedFrameId;

    /// <summary>原生画布显示设置，只允许UI线程修改。</summary>
    public CanvasOptions Options
    {
        get => _canvas.Options;
        set => _canvas.Options = value;
    }

    /// <inheritdoc/>
    public bool SetViews(IEnumerable<VisionView> views) => Results.SetViews(views);

    /// <summary>清空视图集合，允许后台调用；UI在下次刷新清理底图。</summary>
    public void ClearViews() => Results.Clear();

    private void Tick(object? sender, EventArgs e) => RefreshResults();

    /// <summary>在Dispatcher线程立即应用最新快照；常规使用由内部定时器合并刷新。</summary>
    public void RefreshResults()
    {
        VerifyAccess();
        if (_disposed)
            throw new ObjectDisposedException(nameof(ResultBrowserControl), "结果浏览器已释放。");
        var snapshot = _presenter.Refresh();
        if (snapshot == null)
            return;
        _shown = snapshot;
        _updating = true;
        try
        {
            Fill(_views, snapshot.Views.ToArray(), snapshot.ViewId);
            _views.Visibility = _viewLabel.Visibility =
                snapshot.Views.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            FillLayers(snapshot);
            _layerButton.IsEnabled = snapshot.Layers.Count > 0;
            _layerButton.Content =
                "图层 " + snapshot.Layers.Count(l => l.Visible) + "/" + snapshot.Layers.Count + " ▼";
            _status.Text = snapshot.Status;
            _status.ToolTip = _status.Text;
        }
        finally
        {
            _updating = false;
        }
    }

    private static void Fill(ComboBox combo, BrowserChoice[] choices, string? selected)
    {
        var old = combo.Items.Cast<BrowserChoice>().ToArray();
        if (
            !old.Select(c => (c.Id, c.Name))
                .SequenceEqual(choices.Select(c => (c.Id, c.Name)))
        )
            combo.ItemsSource = choices;
        combo.SelectedItem = combo.Items.Cast<BrowserChoice>().FirstOrDefault(c => c.Id == selected);
    }

    private void FillLayers(ResultBrowserSnapshot snapshot)
    {
        var old = _layers.Children.Cast<CheckBox>().ToArray();
        bool same = old.Select(c => ((string)c.Tag, (string)c.Content))
            .SequenceEqual(snapshot.Layers.Select(l => (l.Id, l.Name)));
        if (!same)
        {
            _layers.Children.Clear();
            foreach (var layer in snapshot.Layers)
            {
                var check = new CheckBox
                {
                    Content = layer.Name,
                    Tag = layer.Id,
                    Margin = new Thickness(4),
                };
                RoutedEventHandler changed = (_, __) =>
                {
                    if (!_updating && _shown != null)
                        Results.TrySetLayerVisible(_shown, layer.Id, check.IsChecked == true);
                };
                check.Checked += changed;
                check.Unchecked += changed;
                _layers.Children.Add(check);
            }
        }
        // 仅显隐变化不替换复选控件，保留弹出列表的焦点和滚动位置。
        for (int i = 0; i < snapshot.Layers.Count; i++)
            ((CheckBox)_layers.Children[i]).IsChecked = snapshot.Layers[i].Visible;
    }

    private void ChangeAll(bool? visible)
    {
        if (_shown != null)
            Results.TrySetAllLayersVisible(_shown, visible);
    }

    private void AddAction(StackPanel panel, string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(4),
            Padding = new Thickness(6, 2, 6, 2),
        };
        button.Click += (_, __) =>
        {
            action();
            RefreshResults();
        };
        panel.Children.Add(button);
    }

    /// <summary>在UI线程停止刷新并释放预览和画布；Unloaded只暂停刷新，不代替最终释放。</summary>
    public void Dispose()
    {
        VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        _popup.IsOpen = false;
        try
        {
            Results.Dispose();
        }
        finally
        {
            _canvas.Dispose();
        }
    }
}
