using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.UI;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Demo;

internal static partial class Program
{
    private static long _sequence;
    private static string? _smokeOutput;

    [STAThread]
    private static void Main(string[] args)
    {
        int smoke = Array.IndexOf(args, "--smoke");
        if (smoke >= 0)
        {
            _smokeOutput = args[smoke + 1];
        }

        if (args.Contains("--results"))
        {
            ResultBrowserDemo.Run(args.Contains("--wpf"), _smokeOutput);
            return;
        }

        if (args.Contains("--wpf"))
        {
            RunWpf();
        }
        else
        {
            RunForms();
        }
    }

    private static void RunForms()
    {
        Forms.Application.EnableVisualStyles();
        using var window = new Forms.Form
        {
            Text = "DP.Vision.Winform — 独立通用视觉画布",
            Width = 1200,
            Height = 850,
        };
        using var canvas = new DP.Vision.Winform.VisionCanvasControl { Dock = Forms.DockStyle.Fill };
        var toolbar = new Forms.FlowLayoutPanel
        {
            Dock = Forms.DockStyle.Top,
            Height = 112,
            AutoSize = false,
        };
        var status = new Forms.Label
        {
            Dock = Forms.DockStyle.Bottom,
            Height = 28,
            Text = "滚轮缩放；右键/中键平移；Home适应；左键按原始几何拾取。大图为按需生成的演示源。",
        };
        void Button(string text, Action click)
        {
            var button = new Forms.Button { Text = text, AutoSize = true };
            button.Click += (_, __) => click();
            toolbar.Controls.Add(button);
        }

        Button("4MP / 10万XLD点", () => Show(canvas, 2544, 1608));
        Button("8K × 8K", () => Show(canvas, 8192, 8192));
        Button("16K × 16K", () => Show(canvas, 16384, 16384));
        Button("适应", canvas.FitToWindow);
        Button(
            "1:1",
            () =>
            {
                canvas.Editor?.Cancel();
                canvas.Viewport.Zoom(
                    1 / canvas.Viewport.Scale,
                    new PointD(canvas.Width / 2, canvas.Height / 2)
                );
                canvas.Invalidate();
            }
        );
        var lod = new Forms.CheckBox { Text = "显示LOD（≤0.5px；不修改原始点）", AutoSize = true };
        lod.CheckedChanged += (_, __) => canvas.Options = new CanvasOptions(contourLod: lod.Checked);
        toolbar.Controls.Add(lod);
        foreach (string id in new[] { "regions", "xld", "rois", "notes" })
        {
            var check = new Forms.CheckBox
            {
                Text = id,
                Checked = true,
                AutoSize = true,
            };
            check.CheckedChanged += (_, __) => canvas.SetLayerVisible(id, check.Checked);
            toolbar.Controls.Add(check);
        }

        canvas.MouseDown += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                var found = canvas.HitTest(e.Location);
                status.Text =
                    found == null
                        ? "未选中几何"
                        : "原始几何拾取：" + found.Id + " / " + found.Geometry.GetType().Name;
            }
        };
        var roiList = AttachFormsEditor(canvas, toolbar, status);
        window.Controls.Add(canvas);
        window.Controls.Add(roiList);
        window.Controls.Add(status);
        window.Controls.Add(toolbar);
        window.Shown += (_, __) =>
        {
            Show(canvas, 2544, 1608);
            if (_smokeOutput != null)
            {
                SmokeEditor(canvas);
                window.BeginInvoke(
                    new Action(() =>
                    {
                        using var bitmap = new System.Drawing.Bitmap(window.Width, window.Height);
                        window.DrawToBitmap(
                            bitmap,
                            new System.Drawing.Rectangle(0, 0, window.Width, window.Height)
                        );
                        bitmap.Save(_smokeOutput);
                        window.Close();
                    })
                );
            }
        };
        Forms.Application.Run(window);
    }

    private static void RunWpf()
    {
        var application = new Wpf.Application();
        using var canvas = new DP.Vision.WPF.VisionCanvasControl();
        var toolbar = new Controls.WrapPanel { Margin = new Wpf.Thickness(6) };
        var status = new Controls.TextBlock
        {
            Text = "滚轮缩放；右键/中键平移；Home适应；左键原始几何拾取。原生WPF，无WinForms托管。",
            Margin = new Wpf.Thickness(6),
        };
        void Button(string text, Action click)
        {
            var button = new Controls.Button
            {
                Content = text,
                Margin = new Wpf.Thickness(3),
                Padding = new Wpf.Thickness(8, 4, 8, 4),
            };
            button.Click += (_, __) => click();
            toolbar.Children.Add(button);
        }

        Button("4MP / 10万XLD点", () => Show(canvas, 2544, 1608));
        Button("8K × 8K", () => Show(canvas, 8192, 8192));
        Button("16K × 16K", () => Show(canvas, 16384, 16384));
        Button("适应", canvas.FitToWindow);
        Button(
            "1:1",
            () =>
            {
                canvas.Editor?.Cancel();
                canvas.Viewport.Zoom(
                    1 / canvas.Viewport.Scale,
                    new PointD(canvas.ActualWidth / 2, canvas.ActualHeight / 2)
                );
                canvas.InvalidateVisual();
            }
        );
        var lod = new Controls.CheckBox { Content = "显示LOD ≤0.5设备像素", Margin = new Wpf.Thickness(6) };
        lod.Checked += (_, __) => canvas.Options = new CanvasOptions(true);
        lod.Unchecked += (_, __) => canvas.Options = new CanvasOptions();
        toolbar.Children.Add(lod);
        foreach (string id in new[] { "regions", "xld", "rois", "notes" })
        {
            var check = new Controls.CheckBox
            {
                Content = id,
                IsChecked = true,
                Margin = new Wpf.Thickness(6),
            };
            check.Checked += (_, __) => canvas.SetLayerVisible(id, true);
            check.Unchecked += (_, __) => canvas.SetLayerVisible(id, false);
            toolbar.Children.Add(check);
        }

        canvas.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                var found = canvas.Pick(e.GetPosition(canvas));
                status.Text =
                    found == null
                        ? "未选中几何"
                        : "原始几何拾取：" + found.Id + " / " + found.Geometry.GetType().Name;
            }
        };
        var roiList = AttachWpfEditor(canvas, toolbar, status);
        var panel = new Controls.DockPanel();
        Controls.DockPanel.SetDock(toolbar, Controls.Dock.Top);
        Controls.DockPanel.SetDock(status, Controls.Dock.Bottom);
        panel.Children.Add(toolbar);
        panel.Children.Add(status);
        Controls.DockPanel.SetDock(roiList, Controls.Dock.Right);
        panel.Children.Add(roiList);
        panel.Children.Add(canvas);
        var window = new Wpf.Window
        {
            Title = "DP.Vision.WPF — 独立通用视觉画布",
            Width = 1200,
            Height = 850,
            Content = panel,
        };
        window.Loaded += (_, __) =>
        {
            Show(canvas, 2544, 1608);
            if (_smokeOutput != null)
            {
                SmokeEditor(canvas);
                window.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            (int)panel.ActualWidth,
                            (int)panel.ActualHeight,
                            96,
                            96,
                            System.Windows.Media.PixelFormats.Pbgra32
                        );
                        bitmap.Render(panel);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using (var output = System.IO.File.Create(_smokeOutput))
                        {
                            encoder.Save(output);
                        }

                        window.Close();
                    })
                );
            }
        };
        application.Run(window);
    }

    private static void SmokeEditor(IVisionCanvas canvas)
    {
        var editor = canvas.Editor!;
        editor.Tool = ERoiTool.Rectangle;
        editor.PointerDown(new PointD(150, 150), 1);
        editor.PointerUp(new PointD(650, 450));
        editor.SetSelectedMetadata(ERoiPurpose.Exclude, false);
        editor.Undo();
        editor.Redo();
        editor.Tool = ERoiTool.Circle;
        editor.PointerDown(new PointD(700, 900), 1);
        editor.PointerUp(new PointD(900, 900));
        editor.Tool = ERoiTool.Polyline;
        editor.PointerDown(new PointD(1200, 1000), 1);
        editor.PointerDown(new PointD(1400, 1000), 1);
        editor.Finish();
        editor.Tool = ERoiTool.InsertVertex;
        editor.PointerDown(new PointD(1300, 1000), 1);
        if (((ContourGeometry)editor.Document.Rois[2].Shape).Points.Count != 3)
        {
            throw new InvalidOperationException("Demo vertex insertion binding failed.");
        }

        editor.Tool = ERoiTool.DeleteVertex;
        editor.PointerDown(new PointD(1300, 1000), 1);
        if (((ContourGeometry)editor.Document.Rois[2].Shape).Points.Count != 2)
        {
            throw new InvalidOperationException("Demo vertex deletion binding failed.");
        }

        editor.DeleteSelected();
        editor.Tool = ERoiTool.Select;
        var xml = RoiDocumentXml.Serialize(editor.Document);
        editor.Load(RoiDocumentXml.Deserialize(xml));
        editor.Select(editor.Document.Rois[1].Id);
        if (editor.Document.Rois.Count != 2 || editor.Document.Rois[0].Enabled)
        {
            throw new InvalidOperationException("Demo ROI tool bindings failed.");
        }
    }

    private static void Show(IVisionCanvas canvas, int width, int height)
    {
        long sequence = ++_sequence;
        string id = "demo-" + sequence;
        using var source = new PatternSource(width, height);
        var runs = new List<RegionRun>();
        for (int y = height / 8; y < height / 3; y++)
        {
            int left = width / 8,
                right = width / 3;
            if (y > height / 6 && y < height / 4)
            {
                runs.Add(new RegionRun(y, left, width / 6));
                runs.Add(new RegionRun(y, width / 4, right));
            }
            else
            {
                runs.Add(new RegionRun(y, left, right));
            }
        }

        var contours = Enumerable
            .Range(0, 20)
            .Select(c => new Visual(
                "xld-" + c,
                new ContourGeometry(
                    Enumerable
                        .Range(0, 5000)
                        .Select(i => new PointD(
                            width * .4 + width * .5 * i / 4999,
                            height * .1 + c * height * .038 + Math.Sin(i * .03) * height * .01
                        ))
                )
            ))
            .ToArray();
        var layers = new[]
        {
            new CanvasLayer(
                "regions",
                ELayerKind.Region,
                new[] { new Visual("孔洞Region", new RegionGeometry(runs), 0x9033CC66) }
            ),
            new CanvasLayer("xld", ELayerKind.Xld, contours, 1),
            new CanvasLayer(
                "rois",
                ELayerKind.Roi,
                new[]
                {
                    new Visual(
                        "圆ROI",
                        new EllipseGeometry(new PointD(width * .2, height * .6), width * .06, width * .06),
                        0xFF33AAFF
                    ),
                    new Visual(
                        "旋转矩形ROI",
                        new RectangleGeometry(
                            new PointD(width * .2, height * .82),
                            width * .15,
                            height * .08,
                            .2
                        ),
                        0xFF66FFFF
                    ),
                },
                2
            ),
            new CanvasLayer(
                "notes",
                ELayerKind.Annotation,
                new[]
                {
                    new Visual(
                        "说明",
                        new ContourGeometry(new[] { new PointD(width * .05, height * .05) }),
                        0xFFFFFF55,
                        $"{width}×{height} Gray8；20条XLD / 100000原始点；只读证据"
                    ),
                },
                3
            ),
        };
        using var frame = new CanvasFrame(id, sequence, source, new GeometryOverlay(id, layers));
        canvas.Present(frame);
        canvas.FitToWindow();
    }
}
