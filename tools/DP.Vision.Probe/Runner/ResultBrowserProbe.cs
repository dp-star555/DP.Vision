using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using DP.Vision.UI;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Probe;

/// <summary>通过实际视图下拉、复选事件和渲染像素验证两种原生浏览器，不冒称物理输入验收。</summary>
internal static partial class ResultBrowserProbe
{
    private static int _readTiles;

    internal static void Run(string output)
    {
        using (var form = new Forms.Form { ClientSize = new Size(900, 600) })
        using (var browser = new DP.Vision.Winform.ResultBrowserControl { Dock = Forms.DockStyle.Fill })
        {
            form.Controls.Add(browser);
            form.Show();
            Forms.Application.DoEvents();
            using var pool = Populate(browser.Results);
            browser.RefreshResults();
            Require(browser.Controls.Find("NodeSelector", true).Length == 0, "WinForms不应含节点选择器。");
            var views = (Forms.ComboBox)browser.Controls.Find("ViewSelector", true).Single();
            Select(views, "binary");
            Require(browser.DisplayedFrameId == "image-b", "WinForms视图未切换底图。");
            Select(views, "raw");
            Require(browser.DisplayedFrameId == "image-a", "WinForms切回视图被旧呈现序号拒绝。");
            var canvas = browser.Controls.OfType<DP.Vision.Winform.VisionCanvasControl>().Single();
            using (var before = Capture(canvas))
                CheckLayer(before, canvas.Viewport, true);
            int reads = _readTiles;
            Field<Forms.CheckedListBox>(browser, "_layers").SetItemChecked(0, false);
            browser.RefreshResults();
            using (var after = Capture(canvas))
                CheckLayer(after, canvas.Viewport, false);
            Require(_readTiles == reads, "WinForms切层重新读取了底图。");
            UpdateOverlay(browser.Results);
            browser.RefreshResults();
            using (var after = Capture(canvas))
                CheckLayer(after, canvas.Viewport, false);
            Require(_readTiles == reads, "WinForms同底图叠加更新使缓存失效。");
            CheckMenu(browser, output);
            using (var reset = Capture(canvas))
                CheckLayer(reset, canvas.Viewport, true);
            Require(_readTiles == reads, "WinForms图层批量开关使底图缓存失效。");
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height));
                image.Save(Path.Combine(output, "result-browser-winforms.png"));
            }
            CheckClearAndRelease(browser.Results, browser.RefreshResults, () => browser.DisplayedFrameId);
            CheckPoolReturned(pool);
            using var disposalPool = Populate(browser.Results);
            browser.RefreshResults();
            browser.Dispose();
            CheckPoolReturned(disposalPool);
            form.Close();
        }

        using (var browser = new DP.Vision.WPF.ResultBrowserControl())
        {
            var window = new Wpf.Window
            {
                Content = browser,
                Width = 900,
                Height = 600,
            };
            window.Show();
            window.UpdateLayout();
            using var pool = Populate(browser.Results);
            browser.RefreshResults();
            Require(
                Wpf.LogicalTreeHelper.FindLogicalNode(browser, "NodeSelector") == null,
                "WPF不应含节点选择器。"
            );
            var views = (Controls.ComboBox)Wpf.LogicalTreeHelper.FindLogicalNode(browser, "ViewSelector");
            Select(views, "binary");
            Require(browser.DisplayedFrameId == "image-b", "WPF视图未切换底图。");
            Select(views, "raw");
            Require(browser.DisplayedFrameId == "image-a", "WPF切回视图被旧呈现序号拒绝。");
            var canvas = Field<DP.Vision.WPF.VisionCanvasControl>(browser, "_canvas");
            using (var before = Capture(canvas))
                CheckLayer(before, canvas.Viewport, true);
            int reads = _readTiles;
            var layers = Field<Controls.StackPanel>(browser, "_layers");
            ((Controls.CheckBox)layers.Children[0]).IsChecked = false;
            browser.RefreshResults();
            using (var after = Capture(canvas))
                CheckLayer(after, canvas.Viewport, false);
            Require(_readTiles == reads, "WPF切层重新读取了底图。");
            UpdateOverlay(browser.Results);
            browser.RefreshResults();
            using (var after = Capture(canvas))
                CheckLayer(after, canvas.Viewport, false);
            Require(_readTiles == reads, "WPF同底图叠加更新使缓存失效。");
            CheckMenu(browser, output);
            using (var reset = Capture(canvas))
                CheckLayer(reset, canvas.Viewport, true);
            Require(_readTiles == reads, "WPF图层批量开关使底图缓存失效。");
            browser.UpdateLayout();
            var image = new RenderTargetBitmap(
                (int)browser.ActualWidth,
                (int)browser.ActualHeight,
                96,
                96,
                System.Windows.Media.PixelFormats.Pbgra32
            );
            image.Render(browser);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(output, "result-browser-wpf.png")))
                encoder.Save(stream);
            CheckClearAndRelease(browser.Results, browser.RefreshResults, () => browser.DisplayedFrameId);
            CheckPoolReturned(pool);
            using var disposalPool = Populate(browser.Results);
            browser.RefreshResults();
            browser.Dispose();
            CheckPoolReturned(disposalPool);
            window.Close();
        }
        Console.WriteLine(
            "PASS: native view browsers: no node selector, view dropdown, layer menu/pixels, cache reuse, clear and dispose pool release."
        );
    }

    private static FrameBufferPool Populate(ResultBrowserSession session)
    {
        var pool = new FrameBufferPool(new ImageInfo(64, 64, EPixelLayout.Gray8), 2, 8192);
        pool.TryRent(out var a);
        pool.TryRent(out var b);
        using var writerA = a!;
        using var writerB = b!;
        writerA.Write(0, Enumerable.Repeat((byte)160, 4096).ToArray(), 0, 4096);
        writerB.Write(0, Enumerable.Repeat((byte)70, 4096).ToArray(), 0, 4096);
        using var sourceA = new CountingSource(writerA.Publish());
        using var sourceB = new CountingSource(writerB.Publish());
        session.SetViews(
            new[] { View("raw", "原图", "image-a", sourceA), View("binary", "二值图", "image-b", sourceB) }
        );
        return pool;
    }

    private static VisionView View(string id, string name, string frame, IImageSource source) =>
        new VisionView(
            id,
            name,
            frame,
            source,
            new GeometryOverlay(
                frame,
                new[]
                {
                    new CanvasLayer(
                        "roi",
                        ELayerKind.Roi,
                        new[]
                        {
                            new Visual(
                                "box",
                                new RectangleGeometry(new PointD(32, 32), 30, 30),
                                VisionColors.Red
                            ),
                        },
                        name: "检测范围"
                    ),
                }
            )
        );

    private static void UpdateOverlay(ResultBrowserSession session)
    {
        using var source = new CountingSource(
            VisionImage.CopyFrom(
                new ImageInfo(64, 64, EPixelLayout.Gray8),
                Enumerable.Repeat((byte)160, 4096).ToArray()
            )
        );
        var layer = new CanvasLayer(
            "roi",
            ELayerKind.Roi,
            new[]
            {
                new Visual("box-new", new RectangleGeometry(new PointD(32, 32), 30, 30), VisionColors.Red),
            },
            name: "新的检测范围"
        );
        session.SetViews(
            new[]
            {
                new VisionView(
                    "raw",
                    "原图",
                    "image-a",
                    source,
                    new GeometryOverlay("image-a", new[] { layer })
                ),
            }
        );
    }

    private static void CheckClearAndRelease(
        ResultBrowserSession session,
        Action refresh,
        Func<string?> displayed
    )
    {
        using var pool = Populate(session);
        refresh();
        session.Clear();
        refresh();
        Require(displayed() == null, "清空集合后仍显示旧底图。");
        CheckPoolReturned(pool);
    }

    private static void CheckPoolReturned(FrameBufferPool pool)
    {
        Require(pool.TryRent(out var a), "第一份源未归还。");
        Require(pool.TryRent(out var b), "第二份源未归还。");
        a!.Dispose();
        b!.Dispose();
    }

    private static Bitmap Capture(DP.Vision.Winform.VisionCanvasControl canvas)
    {
        canvas.Refresh();
        var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        return bitmap;
    }

    private static Bitmap Capture(DP.Vision.WPF.VisionCanvasControl canvas)
    {
        canvas.UpdateLayout();
        canvas.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
        var image = new RenderTargetBitmap(
            (int)canvas.ActualWidth,
            (int)canvas.ActualHeight,
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32
        );
        image.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }

    private static void CheckLayer(Bitmap bitmap, CanvasViewport viewport, bool visible)
    {
        Require(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).R == 160, "切回视图后底图像素不正确。");
        var point = new PointD(
            viewport.Origin.X + 17 * viewport.Scale,
            viewport.Origin.Y + 32 * viewport.Scale
        );
        bool red = false;
        for (int y = (int)point.Y - 2; y <= (int)point.Y + 2; y++)
        for (int x = (int)point.X - 2; x <= (int)point.X + 2; x++)
        {
            var color = bitmap.GetPixel(x, y);
            red |= color.R > color.G + 80;
        }
        Require(red == visible, "渲染像素与图层显隐状态不一致。");
    }

    private static void Select(Forms.ComboBox combo, string id) =>
        combo.SelectedItem = combo.Items.Cast<BrowserChoice>().Single(c => c.Id == id);

    private static void Select(Controls.ComboBox combo, string id) =>
        combo.SelectedItem = combo.Items.Cast<BrowserChoice>().Single(c => c.Id == id);

    private static T Field<T>(object instance, string name) =>
        (T)
            instance
                .GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(instance)!;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
