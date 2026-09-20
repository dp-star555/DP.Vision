using System;
using System.IO;
using System.Linq;
using DP.Vision.UI;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Demo;

internal static partial class Program
{
    private static ToolChoice[] Choices()
    {
        return new[]
        {
            new ToolChoice(ERoiTool.Select, "选择 / 编辑"),
            new ToolChoice(ERoiTool.Rectangle, "矩形"),
            new ToolChoice(ERoiTool.RotatedRectangle, "旋转矩形"),
            new ToolChoice(ERoiTool.Circle, "圆（中心→半径）"),
            new ToolChoice(ERoiTool.Ellipse, "椭圆"),
            new ToolChoice(ERoiTool.Polygon, "多边形"),
            new ToolChoice(ERoiTool.Polyline, "折线 / XLD"),
            new ToolChoice(ERoiTool.Point, "点"),
            new ToolChoice(ERoiTool.InsertVertex, "选中轮廓：插入顶点"),
            new ToolChoice(ERoiTool.DeleteVertex, "选中轮廓：删除顶点"),
        };
    }

    private static Forms.Control AttachFormsEditor(
        DP.Vision.Winform.VisionCanvasControl canvas,
        Forms.FlowLayoutPanel toolbar,
        Forms.Label status
    )
    {
        var editor = new RoiEditor();
        canvas.Editor = editor;
        var choices = Choices();
        var tool = new Forms.ComboBox { DropDownStyle = Forms.ComboBoxStyle.DropDownList, Width = 150 };
        tool.Items.AddRange(choices);
        tool.SelectedIndex = 0;
        toolbar.Controls.Add(tool);
        var list = new Forms.ListBox
        {
            Dock = Forms.DockStyle.Right,
            Width = 170,
            DisplayMember = "Id",
        };
        bool syncing = false;
        var enabled = new Forms.CheckBox
        {
            Text = "启用选中ROI",
            Checked = true,
            AutoSize = true,
        };
        var exclude = new Forms.CheckBox { Text = "排除范围", AutoSize = true };
        toolbar.Controls.Add(enabled);
        toolbar.Controls.Add(exclude);
        void Refresh()
        {
            syncing = true;
            try
            {
                tool.SelectedItem = choices.First(c => c.Tool == editor.Tool);
                list.SelectedItem = editor.Document.Rois.FirstOrDefault(r => r.Id == editor.SelectedId);
                var selected = list.SelectedItem as RoiDefinition;
                enabled.Enabled = exclude.Enabled = selected != null;
                enabled.Checked = selected?.Enabled ?? true;
                exclude.Checked = selected?.Purpose == ERoiPurpose.Exclude;
                status.Text =
                    editor.ValidationError
                    ?? $"ROI {editor.Document.Rois.Count}；选中 {editor.SelectedId ?? "无"}；Enter/双击结束折线，Esc取消，Delete删除，Ctrl+Z/Y撤销重做。";
            }
            finally
            {
                syncing = false;
            }
        }

        editor.Changed += (_, __) => Refresh();
        editor.DocumentChanged += (_, __) =>
        {
            syncing = true;
            try
            {
                list.DataSource = editor.Document.Rois.ToArray();
            }
            finally
            {
                syncing = false;
            }

            Refresh();
        };
        tool.SelectedIndexChanged += (_, __) =>
        {
            if (!syncing && tool.SelectedItem is ToolChoice choice)
            {
                editor.Tool = choice.Tool;
                canvas.Focus();
            }
        };
        list.SelectedIndexChanged += (_, __) =>
        {
            if (!syncing)
            {
                editor.Select((list.SelectedItem as RoiDefinition)?.Id);
            }
        };
        enabled.CheckedChanged += (_, __) =>
        {
            if (!syncing)
            {
                editor.SetSelectedMetadata(
                    exclude.Checked ? ERoiPurpose.Exclude : ERoiPurpose.Include,
                    enabled.Checked
                );
            }
        };
        exclude.CheckedChanged += (_, __) =>
        {
            if (!syncing)
            {
                editor.SetSelectedMetadata(
                    exclude.Checked ? ERoiPurpose.Exclude : ERoiPurpose.Include,
                    enabled.Checked
                );
            }
        };
        void Button(string label, Action click)
        {
            var button = new Forms.Button { Text = label, AutoSize = true };
            button.Click += (_, __) =>
            {
                try
                {
                    click();
                    canvas.Focus();
                }
                catch (Exception error)
                {
                    Forms.MessageBox.Show(
                        error.Message,
                        "ROI配置",
                        Forms.MessageBoxButtons.OK,
                        Forms.MessageBoxIcon.Error
                    );
                }
            };
            toolbar.Controls.Add(button);
        }

        Button("撤销", editor.Undo);
        Button("重做", editor.Redo);
        Button("删除ROI", editor.DeleteSelected);
        Button("完成多边形", () => editor.Finish());
        Button(
            "保存ROI",
            () =>
            {
                using var dialog = new Forms.SaveFileDialog
                {
                    Filter = "ROI配置 XML|*.roi.xml",
                    FileName = "vision.roi.xml",
                };
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    File.WriteAllText(dialog.FileName, RoiDocumentXml.Serialize(editor.Document));
                }
            }
        );
        Button(
            "加载ROI",
            () =>
            {
                using var dialog = new Forms.OpenFileDialog { Filter = "ROI配置 XML|*.roi.xml|XML|*.xml" };
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    using var reader = File.OpenText(dialog.FileName);
                    var document = RoiDocumentXml.Deserialize(reader);
                    editor.Load(document);
                }
            }
        );
        Refresh();
        return list;
    }

    private static Controls.ListBox AttachWpfEditor(
        DP.Vision.WPF.VisionCanvasControl canvas,
        Controls.WrapPanel toolbar,
        Controls.TextBlock status
    )
    {
        var editor = new RoiEditor();
        canvas.Editor = editor;
        var choices = Choices();
        var tool = new Controls.ComboBox
        {
            ItemsSource = choices,
            SelectedIndex = 0,
            Width = 150,
            Margin = new Wpf.Thickness(4),
        };
        toolbar.Children.Add(tool);
        var list = new Controls.ListBox { Width = 170, DisplayMemberPath = "Id" };
        bool syncing = false;
        var enabled = new Controls.CheckBox
        {
            Content = "启用选中ROI",
            IsChecked = true,
            Margin = new Wpf.Thickness(6),
        };
        var exclude = new Controls.CheckBox { Content = "排除范围", Margin = new Wpf.Thickness(6) };
        toolbar.Children.Add(enabled);
        toolbar.Children.Add(exclude);
        void Refresh()
        {
            syncing = true;
            try
            {
                tool.SelectedItem = choices.First(c => c.Tool == editor.Tool);
                list.SelectedItem = editor.Document.Rois.FirstOrDefault(r => r.Id == editor.SelectedId);
                var selected = list.SelectedItem as RoiDefinition;
                enabled.IsEnabled = exclude.IsEnabled = selected != null;
                enabled.IsChecked = selected?.Enabled ?? true;
                exclude.IsChecked = selected?.Purpose == ERoiPurpose.Exclude;
                status.Text =
                    editor.ValidationError
                    ?? $"ROI {editor.Document.Rois.Count}；选中 {editor.SelectedId ?? "无"}；Enter/双击结束折线，Esc取消，Delete删除，Ctrl+Z/Y撤销重做。";
            }
            finally
            {
                syncing = false;
            }
        }

        editor.Changed += (_, __) => Refresh();
        editor.DocumentChanged += (_, __) =>
        {
            syncing = true;
            try
            {
                list.ItemsSource = editor.Document.Rois.ToArray();
            }
            finally
            {
                syncing = false;
            }

            Refresh();
        };
        tool.SelectionChanged += (_, __) =>
        {
            if (!syncing && tool.SelectedItem is ToolChoice choice)
            {
                editor.Tool = choice.Tool;
                canvas.Focus();
            }
        };
        list.SelectionChanged += (_, __) =>
        {
            if (!syncing)
            {
                editor.Select((list.SelectedItem as RoiDefinition)?.Id);
            }
        };
        void Metadata()
        {
            if (!syncing)
            {
                editor.SetSelectedMetadata(
                    exclude.IsChecked == true ? ERoiPurpose.Exclude : ERoiPurpose.Include,
                    enabled.IsChecked == true
                );
            }
        }

        enabled.Checked += (_, __) => Metadata();
        enabled.Unchecked += (_, __) => Metadata();
        exclude.Checked += (_, __) => Metadata();
        exclude.Unchecked += (_, __) => Metadata();
        void Button(string label, Action click)
        {
            var button = new Controls.Button
            {
                Content = label,
                Margin = new Wpf.Thickness(3),
                Padding = new Wpf.Thickness(6, 3, 6, 3),
            };
            button.Click += (_, __) =>
            {
                try
                {
                    click();
                    canvas.Focus();
                }
                catch (Exception error)
                {
                    Wpf.MessageBox.Show(
                        error.Message,
                        "ROI配置",
                        Wpf.MessageBoxButton.OK,
                        Wpf.MessageBoxImage.Error
                    );
                }
            };
            toolbar.Children.Add(button);
        }

        Button("撤销", editor.Undo);
        Button("重做", editor.Redo);
        Button("删除ROI", editor.DeleteSelected);
        Button("完成多边形", () => editor.Finish());
        Button(
            "保存ROI",
            () =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "ROI配置 XML|*.roi.xml",
                    FileName = "vision.roi.xml",
                };
                if (dialog.ShowDialog() == true)
                {
                    File.WriteAllText(dialog.FileName, RoiDocumentXml.Serialize(editor.Document));
                }
            }
        );
        Button(
            "加载ROI",
            () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "ROI配置 XML|*.roi.xml|XML|*.xml",
                };
                if (dialog.ShowDialog() == true)
                {
                    using var reader = File.OpenText(dialog.FileName);
                    var document = RoiDocumentXml.Deserialize(reader);
                    editor.Load(document);
                }
            }
        );
        Refresh();
        return list;
    }
}
