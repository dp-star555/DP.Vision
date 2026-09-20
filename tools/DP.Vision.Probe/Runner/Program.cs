using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DP.Vision.UI;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Probe;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            var application = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
            if (args.Length != 1)
            {
                throw new ArgumentException("Usage: DP.Vision.Probe <new-output-directory>");
            }

            if (Directory.Exists(args[0]))
            {
                throw new IOException("Output already exists.");
            }

            Directory.CreateDirectory(args[0]);
            Forms.Application.EnableVisualStyles();
            using var image = VisionImage.CopyFrom(
                new ImageInfo(400, 300, EPixelLayout.Gray8),
                Enumerable.Repeat((byte)255, 120000).ToArray()
            );
            using var source = image.Retain();
            var runs = new List<RegionRun>();
            for (int y = 20; y < 140; y++)
            {
                if (y >= 50 && y < 90)
                {
                    runs.Add(new RegionRun(y, 20, 50));
                    runs.Add(new RegionRun(y, 100, 180));
                }
                else
                {
                    runs.Add(new RegionRun(y, 20, 180));
                }
            }

            for (int y = 200; y < 220; y++)
            {
                runs.Add(new RegionRun(y, 250, 280));
            }

            var region = new Visual("region", new RegionGeometry(runs), 0xFF33CC66);
            var contour = new Visual(
                "xld",
                new ContourGeometry(
                    new[] { new PointD(230.75, 30.25), new PointD(285.5, 55.875), new PointD(301.125, 99.5) }
                )
            );
            var overlay = new GeometryOverlay(
                "f1",
                new[]
                {
                    new CanvasLayer("regions", ELayerKind.Region, new[] { region }),
                    new CanvasLayer("xlds", ELayerKind.Xld, new[] { contour }, 1),
                    new CanvasLayer(
                        "rois",
                        ELayerKind.Roi,
                        new[]
                        {
                            new Visual(
                                "circle",
                                new EllipseGeometry(new PointD(200, 230), 15, 15),
                                0xFF3366FF
                            ),
                        },
                        2
                    ),
                }
            );
            using var packet = new CanvasFrame("f1", 1, source, overlay);
            using var empty = new CanvasFrame("f2", 2, source);
            using (var form = new Forms.Form { ClientSize = new Size(800, 600) })
            using (var canvas = new DP.Vision.Winform.VisionCanvasControl { Dock = Forms.DockStyle.Fill })
            {
                form.Controls.Add(canvas);
                form.Show();
                Forms.Application.DoEvents();
                canvas.Present(packet);
                canvas.FitToWindow();
                using (var bitmap = Capture(canvas))
                {
                    Check(bitmap, canvas.Viewport, true);
                    bitmap.Save(Path.Combine(args[0], "winform.png"));
                }

                canvas.Options = new CanvasOptions(true);
                canvas.Viewport.Zoom(1.1, new PointD(400, 300));
                canvas.Viewport.Pan(10, 10);
                using (var bitmap = Capture(canvas))
                {
                    Check(bitmap, canvas.Viewport, true);
                }

                canvas.SetLayerVisible("regions", false);
                using (var bitmap = Capture(canvas))
                {
                    Check(bitmap, canvas.Viewport, false);
                }

                canvas.SetLayerVisible("regions", true);
                canvas.Present(empty);
                canvas.Present(packet);
                using (var bitmap = Capture(canvas))
                {
                    Check(bitmap, canvas.Viewport, false);
                }

                CheckFormats(canvas, () => Capture(canvas));
                form.Close();
            }

            using (var canvas = new DP.Vision.WPF.VisionCanvasControl { Width = 800, Height = 600 })
            {
                var window = new Wpf.Window
                {
                    Content = canvas,
                    SizeToContent = Wpf.SizeToContent.WidthAndHeight,
                };
                window.Show();
                window.UpdateLayout();
                canvas.Present(packet);
                canvas.FitToWindow();
                using (var bitmap = Capture(canvas))
                {
                    Check(bitmap, canvas.Viewport, true);
                    bitmap.Save(Path.Combine(args[0], "wpf.png"));
                }

                canvas.Options = new CanvasOptions(true);
                canvas.Viewport.Zoom(1.1, new PointD(400, 300));
                canvas.Viewport.Pan(10, 10);
                using (var bitmap = Capture(canvas))
                {
                    Check(bitmap, canvas.Viewport, true);
                }

                canvas.SetLayerVisible("regions", false);
                using (var bitmap = Capture(canvas))
                {
                    Check(bitmap, canvas.Viewport, false);
                }

                canvas.Present(empty);
                canvas.Present(packet);
                using (var bitmap = Capture(canvas))
                {
                    Check(bitmap, canvas.Viewport, false);
                }

                CheckFormats(canvas, () => Capture(canvas));
                window.Close();
            }

            ResultBrowserProbe.Run(args[0]);
            RoiInteractionProbe.Run(args[0]);
            Large(args[0]);
            if (
                AppDomain
                    .CurrentDomain.GetAssemblies()
                    .Any(a =>
                        a.GetName().Name!.StartsWith("halcon", StringComparison.OrdinalIgnoreCase)
                        || a.GetName()
                            .Name!.StartsWith("DP.LabelInspection", StringComparison.OrdinalIgnoreCase)
                    )
            )
            {
                throw new InvalidOperationException("Business/vendor dependency leaked.");
            }

            File.WriteAllText(
                Path.Combine(args[0], "PASS.txt"),
                "PASS: native WinForms/WPF Region hole/island/subpixel contour, layer toggle, zoom/pan, stale-frame rejection, 8K/16K tiled source/cache smoke; no HALCON or label assemblies."
            );
            Console.WriteLine("PASS DP.Vision native control probe.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static Bitmap Capture(DP.Vision.Winform.VisionCanvasControl canvas)
    {
        canvas.Refresh();
        var image = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(image, canvas.ClientRectangle);
        return image;
    }

    private static Bitmap Capture(DP.Vision.WPF.VisionCanvasControl canvas)
    {
        canvas.InvalidateVisual();
        canvas.UpdateLayout();
        canvas.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
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
        using var temp = new Bitmap(stream);
        return new Bitmap(temp);
    }

    private static void Check(Bitmap bitmap, CanvasViewport view, bool regions)
    {
        Color Pixel(double x, double y)
        {
            return bitmap.GetPixel(
                (int)Math.Floor(view.Origin.X + x * view.Scale),
                (int)Math.Floor(view.Origin.Y + y * view.Scale)
            );
        }

        if (
            (Pixel(25.5, 25.5).G > Pixel(25.5, 25.5).R) != regions
            || (Pixel(260.5, 210.5).G > Pixel(260.5, 210.5).R) != regions
            || Pixel(80.5, 80.5).ToArgb() != Color.White.ToArgb()
        )
        {
            throw new InvalidOperationException("Region mask/layer/viewport mismatch.");
        }

        if (
            Pixel(256, 150).ToArgb() != Color.White.ToArgb()
            || Pixel(200, 256).ToArgb() != Color.White.ToArgb()
        )
        {
            throw new InvalidOperationException("Tile seam altered uniform image pixels.");
        }

        if (regions)
        {
            var p = Pixel(230.75, 30.25);
            if (p.G >= 240)
            {
                throw new InvalidOperationException("Contour not rendered.");
            }
        }
    }

    private static void CheckFormats(IVisionCanvas canvas, Func<Bitmap> capture)
    {
        var layouts = new[]
        {
            EPixelLayout.Gray8,
            EPixelLayout.Gray16,
            EPixelLayout.Bgr24,
            EPixelLayout.Rgb24,
            EPixelLayout.Bgra32,
            EPixelLayout.Rgba32,
        };
        var values = new[]
        {
            new byte[] { 200 },
            new byte[] { 0, 128 },
            new byte[] { 30, 20, 10 },
            new byte[] { 10, 20, 30 },
            new byte[] { 30, 20, 10, 255 },
            new byte[] { 10, 20, 30, 255 },
        };
        for (int i = 0; i < layouts.Length; i++)
        {
            using var source = VisionImage.CopyFrom(new ImageInfo(1, 1, layouts[i]), values[i]);
            canvas.SetImage(source, "format-" + i, 100 + i);
            canvas.FitToWindow();
            using var bitmap = capture();
            var actual = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            var expected =
                i == 0 ? Color.FromArgb(200, 200, 200)
                : i == 1 ? Color.FromArgb(127, 127, 127)
                : Color.FromArgb(10, 20, 30);
            if (actual.ToArgb() != expected.ToArgb())
            {
                throw new InvalidOperationException("Native canvas pixel format mismatch: " + layouts[i]);
            }
        }
        CheckSourceLifetime(canvas, capture);
    }

    private static void Large(string output)
    {
        // 程序生成的不可变源仅证明显示读取有界，不证明相机/解码器吞吐或整图存储成本。
        foreach (int size in new[] { 8192, 16384 })
        {
            using var source = new PatternSource(size);
            using var packet = new CanvasFrame("large", 1, source);
            using var form = new Forms.Form { ClientSize = new Size(1000, 700) };
            using var canvas = new DP.Vision.Winform.VisionCanvasControl
            {
                Dock = Forms.DockStyle.Fill,
                Options = new CanvasOptions(tileCacheBytes: 8 * 1024 * 1024),
            };
            form.Controls.Add(canvas);
            form.Show();
            Forms.Application.DoEvents();
            canvas.Present(packet);
            canvas.FitToWindow();
            var timer = Stopwatch.StartNew();
            canvas.Refresh();
            timer.Stop();
            double first = timer.Elapsed.TotalMilliseconds;
            var times = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                timer.Restart();
                canvas.Refresh();
                timer.Stop();
                times.Add(timer.Elapsed.TotalMilliseconds);
            }

            canvas.Viewport.Zoom(1 / canvas.Viewport.Scale, new PointD(500, 350));
            canvas.Viewport.Pan(100, 60);
            using var bitmap = Capture(canvas);
            if (canvas.CachedPixelBytes > 8 * 1024 * 1024)
            {
                throw new InvalidOperationException("Cache budget exceeded.");
            }

            bitmap.Save(Path.Combine(output, "large-" + size + ".png"));
            times.Sort();
            File.WriteAllText(
                Path.Combine(output, "large-" + size + ".txt"),
                $"Procedural Gray8 {size}x{size}; first display={first:F3} ms; warm redraw median={times[15]:F3} ms; p95={times[28]:F3} ms; accounted display cache={canvas.CachedPixelBytes} bytes. No full-image allocation; not an end-to-end camera/algorithm benchmark."
            );
            form.Close();
            using var wpf = new DP.Vision.WPF.VisionCanvasControl
            {
                Width = 1000,
                Height = 700,
                Options = new CanvasOptions(tileCacheBytes: 8 * 1024 * 1024),
            };
            var window = new Wpf.Window { Content = wpf, SizeToContent = Wpf.SizeToContent.WidthAndHeight };
            window.Show();
            window.UpdateLayout();
            wpf.Present(packet);
            wpf.FitToWindow();
            using var wpfBitmap = Capture(wpf);
            if (wpf.CachedPixelBytes > 8 * 1024 * 1024)
            {
                throw new InvalidOperationException("WPF cache budget exceeded.");
            }

            wpfBitmap.Save(Path.Combine(output, "large-wpf-" + size + ".png"));
            window.Close();
        }
    }
}
