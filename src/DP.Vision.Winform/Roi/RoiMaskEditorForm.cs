using System;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using DP.Vision.UI;

namespace DP.Vision.Winform;

/// <summary>
/// 弹出式“ROI与涂抹”编辑窗口：几何ROI和画笔涂抹在同一画布、同一文档中编辑，实时显示合成后的有效区域。
/// 确定后通过<see cref="Document"/>取回结果；窗口不修改调用方传入的文档。
/// </summary>
public sealed class RoiMaskEditorForm : Form
{
    private readonly RoiMaskSession _session;
    private readonly VisionCanvasControl _canvas = new VisionCanvasControl { Dock = DockStyle.Fill };
    private readonly ComboBox _tool = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _purpose = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly NumericUpDown _radius = new NumericUpDown
    {
        Minimum = 1,
        Maximum = 1000,
        DecimalPlaces = 0,
        Width = 64,
    };
    private readonly Label _status = new Label
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
    };
    private bool _syncing;

    /// <summary>以样图和已有文档创建窗口。</summary>
    /// <param name="image">样图；窗口持有独立租约，关闭时归还。</param>
    /// <param name="document">已有ROI文档；null表示从空文档开始。</param>
    public RoiMaskEditorForm(IImageSource image, RoiDocument? document = null)
    {
        _session = new RoiMaskSession(image, document);
        Text = "ROI与涂抹编辑";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1100, 760);
        MinimumSize = new Size(640, 420);

        var tools = Row();
        tools.Controls.Add(Caption("区域类型"));
        _tool.Items.AddRange(RoiToolChoice.Areas.ToArray<object>());
        _tool.SelectedIndexChanged += (_, __) =>
        {
            if (!_syncing && _tool.SelectedItem is RoiToolChoice choice)
            {
                _session.Editor.Tool = choice.Tool;
                _canvas.Focus();
            }
        };
        tools.Controls.Add(_tool);
        tools.Controls.Add(Caption("涂抹用途"));
        _purpose.Items.AddRange(new object[] { "包含", "排除" });
        _purpose.SelectedIndexChanged += (_, __) =>
        {
            if (!_syncing)
            {
                _session.Editor.PaintPurpose = _purpose.SelectedIndex == 1 ? ERoiPurpose.Exclude : ERoiPurpose.Include;
            }
        };
        tools.Controls.Add(_purpose);
        tools.Controls.Add(Caption("笔刷半径"));
        _radius.ValueChanged += (_, __) =>
        {
            if (!_syncing)
            {
                _session.Editor.BrushRadius = (double)_radius.Value;
            }
        };
        tools.Controls.Add(_radius);

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
        var effective = new CheckBox { Text = "显示有效区域", Checked = true, AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        effective.CheckedChanged += (_, __) => _session.ShowEffective = effective.Checked;
        actions.Controls.Add(effective);

        var rule = new Label
        {
            Text = RoiMaskSession.RuleText,
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(6, 2, 6, 2),
            ForeColor = SystemColors.GrayText,
        };

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 80 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80 };
        AcceptButton = null; // Enter留给画布结束多边形。
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        bottom.Controls.Add(_status);
        bottom.Controls.Add(buttons);

        Controls.Add(_canvas);
        Controls.Add(rule);
        Controls.Add(actions);
        Controls.Add(tools);
        Controls.Add(bottom);

        _canvas.Editor = _session.Editor;
        _session.Changed += (_, __) => Sync();
        _session.FrameChanged += (_, __) => PresentFrame();
        PresentFrame();
        Sync();
    }

    /// <summary>当前编辑结果（几何ROI与涂抹层）；对话框返回OK后读取。</summary>
    public RoiDocument Document => _session.Document;

    /// <summary>最近一次合成的有效区域；合成失败时为null。</summary>
    public RegionGeometry? Effective => _session.Effective;

    /// <summary>弹出模态窗口编辑ROI与涂抹。</summary>
    /// <param name="owner">父窗口，可为null。</param>
    /// <param name="image">样图，调用方仍拥有原句柄。</param>
    /// <param name="document">已有文档，可为null。</param>
    /// <returns>确定时返回新文档，取消时返回null。</returns>
    public static RoiDocument? Edit(IWin32Window? owner, IImageSource image, RoiDocument? document = null)
    {
        using var form = new RoiMaskEditorForm(image, document);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Document : null;
    }

    private static FlowLayoutPanel Row()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(2),
        };
    }

    private static Label Caption(string text)
    {
        return new Label { Text = text, AutoSize = true, Margin = new Padding(10, 8, 2, 0) };
    }

    private static void AddAction(Control row, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, __) => action();
        row.Controls.Add(button);
    }

    private static void AddAction(Control row, string text, Func<bool> action)
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
            var choice = RoiToolChoice.Find(RoiToolChoice.Areas, editor.Tool);
            _tool.SelectedItem = choice;
            // 笔刷半径与涂抹用途只对画笔/橡皮有意义。
            _purpose.Enabled = _radius.Enabled = choice?.IsPaint == true;

            _purpose.SelectedIndex = editor.PaintPurpose == ERoiPurpose.Exclude ? 1 : 0;
            _radius.Value = Math.Max(_radius.Minimum, Math.Min(_radius.Maximum, (decimal)editor.BrushRadius));
            _status.Text = _session.Status;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _canvas.Editor = null;
            _canvas.Dispose();
            _session.Dispose();
        }

        base.Dispose(disposing);
    }
}
