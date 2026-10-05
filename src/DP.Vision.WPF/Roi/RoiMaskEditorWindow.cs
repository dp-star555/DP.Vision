using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DP.Vision.UI;

namespace DP.Vision.WPF;

/// <summary>
/// 弹出式“ROI与涂抹”编辑窗口：几何ROI和画笔涂抹在同一画布、同一文档中编辑，实时显示合成后的有效区域。
/// 确定后通过<see cref="Document"/>取回结果；窗口不修改调用方传入的文档，关闭时释放画布和样图租约。
/// </summary>
public sealed class RoiMaskEditorWindow : Window
{
    private readonly RoiMaskSession _session;
    private readonly VisionCanvasControl _canvas = new VisionCanvasControl();
    private readonly Dictionary<ERoiTool, ToggleButton> _tools = new Dictionary<ERoiTool, ToggleButton>();
    private readonly ComboBox _purpose = new ComboBox { Width = 70, Margin = new Thickness(4) };
    private readonly Slider _radius = new Slider
    {
        Minimum = 1,
        Maximum = 200,
        Width = 140,
        IsSnapToTickEnabled = true,
        TickFrequency = 1,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4),
    };
    private readonly TextBlock _radiusText = new TextBlock { Width = 36, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new TextBlock
    {
        Margin = new Thickness(6),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private bool _syncing;

    /// <summary>以样图和已有文档创建窗口。</summary>
    /// <param name="image">样图；窗口持有独立租约，关闭时归还。</param>
    /// <param name="document">已有ROI文档；null表示从空文档开始。</param>
    public RoiMaskEditorWindow(IImageSource image, RoiDocument? document = null)
    {
        _session = new RoiMaskSession(image, document);
        Title = "ROI与涂抹编辑";
        Width = 1100;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tools = Row();
        foreach (var (tool, text) in RoiMaskSession.Tools)
        {
            var button = new ToggleButton { Content = text, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2) };
            button.Checked += (_, __) =>
            {
                if (!_syncing)
                {
                    _session.Editor.Tool = tool;
                }
            };
            // 工具是单选：再次点击已选中的工具保持选中。
            button.Unchecked += (_, __) =>
            {
                if (!_syncing)
                {
                    Sync();
                }
            };
            _tools.Add(tool, button);
            tools.Children.Add(button);
        }

        tools.Children.Add(Caption("涂抹用途"));
        _purpose.Items.Add("包含");
        _purpose.Items.Add("排除");
        _purpose.SelectionChanged += (_, __) =>
        {
            if (!_syncing)
            {
                _session.Editor.PaintPurpose = _purpose.SelectedIndex == 1 ? ERoiPurpose.Exclude : ERoiPurpose.Include;
            }
        };
        tools.Children.Add(_purpose);
        tools.Children.Add(Caption("笔刷半径"));
        _radius.ValueChanged += (_, __) =>
        {
            if (!_syncing)
            {
                _session.Editor.BrushRadius = _radius.Value;
            }
        };
        tools.Children.Add(_radius);
        tools.Children.Add(_radiusText);

        var actions = Row();
        AddAction(actions, "选中ROI拍平为涂抹", () => _session.Editor.FlattenSelected());
        AddAction(actions, "涂抹取反", () => _session.Editor.InvertPaint(_session.Editor.PaintPurpose));
        AddAction(actions, "涂抹包含⇄排除", () => _session.Editor.SwapPaintPurpose());
        AddAction(actions, "清空涂抹", () => _session.Editor.ClearPaint());
        AddAction(actions, "选中ROI包含⇄排除", _session.ToggleSelectedPurpose);
        AddAction(actions, "删除选中", _session.Editor.DeleteSelected);
        AddAction(actions, "撤销", _session.Editor.Undo);
        AddAction(actions, "重做", _session.Editor.Redo);
        AddAction(actions, "适应窗口", _canvas.FitToWindow);
        var effective = new CheckBox
        {
            Content = "显示有效区域",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        effective.Click += (_, __) => _session.ShowEffective = effective.IsChecked == true;
        actions.Children.Add(effective);

        var rule = new TextBlock
        {
            Text = RoiMaskSession.RuleText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 2, 6, 4),
            Foreground = SystemColors.GrayTextBrush,
        };

        var ok = new Button { Content = "确定", Width = 80, Margin = new Thickness(4) };
        ok.Click += (_, __) => DialogResult = true;
        var cancel = new Button { Content = "取消", Width = 80, Margin = new Thickness(4), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var bottom = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel();
        foreach (var top in new UIElement[] { Scroll(tools), Scroll(actions), rule })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }

        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_canvas);
        Content = root;

        _canvas.Editor = _session.Editor;
        _session.Changed += (_, __) => Sync();
        _session.FrameChanged += (_, __) => PresentFrame();
        Loaded += (_, __) =>
        {
            PresentFrame();
            _canvas.FitToWindow();
            _canvas.Focus();
        };
        Closed += (_, __) =>
        {
            _canvas.Editor = null;
            _canvas.Dispose();
            _session.Dispose();
        };
        Sync();
    }

    /// <summary>当前编辑结果（几何ROI与涂抹层）；对话框返回true后读取。</summary>
    public RoiDocument Document => _session.Document;

    /// <summary>最近一次合成的有效区域；合成失败时为null。</summary>
    public RegionGeometry? Effective => _session.Effective;

    /// <summary>弹出模态窗口编辑ROI与涂抹。</summary>
    /// <param name="owner">父窗口，可为null。</param>
    /// <param name="image">样图，调用方仍拥有原句柄。</param>
    /// <param name="document">已有文档，可为null。</param>
    /// <returns>确定时返回新文档，取消时返回null。</returns>
    public static RoiDocument? Edit(Window? owner, IImageSource image, RoiDocument? document = null)
    {
        var window = new RoiMaskEditorWindow(image, document) { Owner = owner };
        if (owner == null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window.ShowDialog() == true ? window.Document : null;
    }

    private static StackPanel Row()
    {
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
    }

    private static ScrollViewer Scroll(UIElement content)
    {
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private static TextBlock Caption(string text)
    {
        return new TextBlock { Text = text, Margin = new Thickness(10, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    private static void AddAction(Panel row, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2) };
        button.Click += (_, __) => action();
        row.Children.Add(button);
    }

    private static void AddAction(Panel row, string text, Func<bool> action)
    {
        AddAction(row, text, () => { action(); });
    }

    private void PresentFrame()
    {
        using var frame = _session.CreateFrame();
        _canvas.Present(frame);
    }

    // 编辑器状态（工具会在创建形状后自动回到“选择”）同步到工具栏，并刷新状态栏。
    private void Sync()
    {
        _syncing = true;
        try
        {
            var editor = _session.Editor;
            foreach (var pair in _tools)
            {
                pair.Value.IsChecked = pair.Key == editor.Tool;
            }

            _purpose.SelectedIndex = editor.PaintPurpose == ERoiPurpose.Exclude ? 1 : 0;
            _radius.Value = Math.Max(_radius.Minimum, Math.Min(_radius.Maximum, editor.BrushRadius));
            _radiusText.Text = ((int)Math.Round(editor.BrushRadius)).ToString();
            _status.Text = _session.Status;
        }
        finally
        {
            _syncing = false;
        }
    }
}
