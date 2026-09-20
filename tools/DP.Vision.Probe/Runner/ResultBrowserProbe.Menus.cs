using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Probe;

internal static partial class ResultBrowserProbe
{
    /// <summary>打开实际弹出框并触发三个原生按钮；反射仅定位测试控件，不绕开事件处理逻辑。</summary>
    private static void CheckMenu(DP.Vision.Winform.ResultBrowserControl browser, string output)
    {
        Field<Forms.Button>(browser, "_layerButton").PerformClick();
        Forms.Application.DoEvents();
        var popup = Field<Forms.ToolStripDropDown>(browser, "_popup");
        Require(popup.Visible, "WinForms图层下拉未打开。");
        var panel = ((Forms.ToolStripControlHost)popup.Items[0]).Control;
        var actions = panel.Controls.OfType<Forms.FlowLayoutPanel>().Single();
        foreach (var command in Commands())
        {
            actions.Controls.OfType<Forms.Button>().Single(b => b.Text == command.Item1).PerformClick();
            Require(
                browser.Results.Snapshot.Layers.All(l => l.Visible == command.Item2),
                "WinForms图层批量按钮未生效。"
            );
        }
        using var image = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(image, popup.ClientRectangle);
        image.Save(Path.Combine(output, "result-browser-layers-winforms.png"));
        popup.Close();
    }

    private static void CheckMenu(DP.Vision.WPF.ResultBrowserControl browser, string output)
    {
        Field<Controls.Button>(browser, "_layerButton")
            .RaiseEvent(new Wpf.RoutedEventArgs(Controls.Primitives.ButtonBase.ClickEvent));
        var popup = Field<Controls.Primitives.Popup>(browser, "_popup");
        Require(popup.IsOpen, "WPF图层下拉未打开。");
        var border = (Controls.Border)popup.Child;
        var panel = (Controls.DockPanel)border.Child;
        var actions = panel.Children.OfType<Controls.StackPanel>().Single();
        foreach (var command in Commands())
        {
            actions
                .Children.OfType<Controls.Button>()
                .Single(b => (string)b.Content == command.Item1)
                .RaiseEvent(new Wpf.RoutedEventArgs(Controls.Primitives.ButtonBase.ClickEvent));
            Require(
                browser.Results.Snapshot.Layers.All(l => l.Visible == command.Item2),
                "WPF图层批量按钮未生效。"
            );
        }
        border.UpdateLayout();
        var image = new RenderTargetBitmap(
            (int)Math.Ceiling(border.ActualWidth),
            (int)Math.Ceiling(border.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32
        );
        image.Render(border);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(output, "result-browser-layers-wpf.png")))
            encoder.Save(stream);
        popup.IsOpen = false;
    }

    private static Tuple<string, bool>[] Commands() =>
        new[] { Tuple.Create("全选", true), Tuple.Create("全不选", false), Tuple.Create("恢复默认", true) };
}
