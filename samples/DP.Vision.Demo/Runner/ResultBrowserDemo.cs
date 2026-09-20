using System;
using System.Drawing;
using System.Linq;
using System.Windows.Media.Imaging;
using DP.Vision.UI;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Demo;

/// <summary>演示多视图与多图层浏览；示例几何用于操作演示，不冒充真实检测结论。</summary>
internal static class ResultBrowserDemo
{
    internal static void Run(bool wpf, string? smoke)
    {
        if (wpf)
            RunWpf(smoke);
        else
            RunForms(smoke);
    }

    private static void RunForms(string? smoke)
    {
        Forms.Application.EnableVisualStyles();
        using var window = new Forms.Form
        {
            Text = "视图浏览器 · 演示数据，非检测验收",
            Width = 1100,
            Height = 760,
        };
        using var browser = new DP.Vision.Winform.ResultBrowserControl { Dock = Forms.DockStyle.Fill };
        var reset = new Forms.Button
        {
            Text = "重新加载示例视图",
            Dock = Forms.DockStyle.Top,
            Height = 32,
        };
        reset.Click += (_, __) => Populate(browser.Results);
        window.Controls.Add(browser);
        window.Controls.Add(reset);
        window.Shown += (_, __) =>
        {
            Populate(browser.Results);
            browser.RefreshResults();
            if (smoke != null)
                window.BeginInvoke(
                    new Action(() =>
                    {
                        using var image = new Bitmap(window.Width, window.Height);
                        window.DrawToBitmap(image, new Rectangle(0, 0, window.Width, window.Height));
                        image.Save(smoke);
                        window.Close();
                    })
                );
        };
        Forms.Application.Run(window);
    }

    private static void RunWpf(string? smoke)
    {
        var app = new Wpf.Application();
        using var browser = new DP.Vision.WPF.ResultBrowserControl();
        var root = new Controls.DockPanel();
        var reset = new Controls.Button { Content = "重新加载示例视图", Height = 32 };
        reset.Click += (_, __) => Populate(browser.Results);
        Controls.DockPanel.SetDock(reset, Controls.Dock.Top);
        root.Children.Add(reset);
        root.Children.Add(browser);
        var window = new Wpf.Window
        {
            Title = "视图浏览器 · 演示数据，非检测验收",
            Width = 1100,
            Height = 760,
            Content = root,
        };
        window.ContentRendered += (_, __) =>
        {
            Populate(browser.Results);
            browser.RefreshResults();
            if (smoke != null)
                window.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        window.UpdateLayout();
                        var image = new RenderTargetBitmap(
                            (int)root.ActualWidth,
                            (int)root.ActualHeight,
                            96,
                            96,
                            System.Windows.Media.PixelFormats.Pbgra32
                        );
                        image.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        using (var stream = System.IO.File.Create(smoke))
                            encoder.Save(stream);
                        window.Close();
                    }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle
                );
        };
        app.Run(window);
    }

    private static void Populate(ResultBrowserSession session)
    {
        string imageIdentity = Guid.NewGuid().ToString("N");
        var info = new ImageInfo(400, 240, EPixelLayout.Gray8);
        var bytes = Enumerable.Repeat((byte)235, info.ByteLength).ToArray();
        for (int y = 70; y < 165; y++)
        for (int x = 65; x < 335; x++)
            if ((x - 65) % 55 < 30)
                bytes[y * info.Width + x] = 45;
        using var raw = VisionImage.CopyFrom(info, bytes);
        using var binary = VisionImage.CopyFrom(
            info,
            bytes.Select(v => v < 128 ? (byte)255 : (byte)0).ToArray()
        );
        string rawId = imageIdentity + "-raw",
            binaryId = imageIdentity + "-binary";
        var roi = new CanvasLayer(
            "roi",
            ELayerKind.Roi,
            new[]
            {
                new Visual(
                    "roi-main",
                    new RectangleGeometry(new PointD(200, 120), 320, 150),
                    VisionColors.RoyalBlue
                ),
            },
            name: "处理范围"
        );
        var boxes = new CanvasLayer(
            "boxes",
            ELayerKind.Annotation,
            Enumerable
                .Range(0, 5)
                .Select(i => new Visual(
                    "box-" + i,
                    new RectangleGeometry(new PointD(80 + i * 55, 117), 30, 95),
                    VisionColors.Lime
                )),
            order: 1,
            name: "示例目标框"
        );
        var contour = new CanvasLayer(
            "contours",
            ELayerKind.Xld,
            new[]
            {
                new Visual(
                    "curve",
                    new ContourGeometry(
                        new[]
                        {
                            new PointD(50, 190),
                            new PointD(150, 185),
                            new PointD(250, 195),
                            new PointD(350, 187),
                        }
                    ),
                    VisionColors.Orange
                ),
            },
            order: 2,
            visible: false,
            name: "示例轮廓"
        );
        var marks = new CanvasLayer(
            "marks",
            ELayerKind.Annotation,
            new[]
            {
                new Visual(
                    "mark-1",
                    new EllipseGeometry(new PointD(190, 110), 22, 22),
                    VisionColors.Crimson,
                    "示例标记"
                ),
            },
            order: 3,
            name: "示例异常标记"
        );
        VisionView View(
            string id,
            string name,
            string frame,
            IImageSource source,
            params CanvasLayer[] layers
        ) => new VisionView(id, name, frame, source, new GeometryOverlay(frame, layers));
        session.SetViews(
            new[]
            {
                View("raw", "原图", rawId, raw, roi),
                View("binary", "二值图", binaryId, binary, roi, boxes),
                View("result", "原图与叠加（示例）", rawId, raw, roi, boxes, contour, marks),
            }
        );
        session.SelectView("result");
        // 方法返回时客户源自动释放，浏览器继续持有所需的独立租约。
    }
}
