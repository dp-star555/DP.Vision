using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using DP.Vision.UI;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DP.Vision.Probe;

internal static class RoiInteractionProbe
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);

    internal static void Run(string output)
    {
        using var image = VisionImage.CopyFrom(
            new ImageInfo(240, 160, EPixelLayout.Gray8),
            Enumerable.Repeat((byte)255, 240 * 160).ToArray()
        );
        using var source = image.Retain();
        var evidence = new RectangleGeometry(new PointD(190, 110), 30, 20);
        var layer = new CanvasLayer(
            "readonly-roi",
            ELayerKind.Roi,
            new[] { new Visual("evidence-not-config", evidence) }
        );
        using var frame = new CanvasFrame(
            "edit-1",
            1,
            source,
            new GeometryOverlay("edit-1", new[] { layer })
        );
        using var next = new CanvasFrame("edit-2", 2, source);
        using (
            var form = new Forms.Form { ClientSize = new Size(800, 600), Text = "ROI input probe - WinForms" }
        )
        using (
            var canvas = new DP.Vision.Winform.VisionCanvasControl
            {
                Dock = Forms.DockStyle.Fill,
                Editor = new RoiEditor(),
            }
        )
        {
            form.Controls.Add(canvas);
            form.Show();
            form.Activate();
            Forms.Application.DoEvents();
            canvas.Present(frame);
            canvas.FitToWindow();
            Point Client(PointD p)
            {
                return new Point(
                    (int)Math.Round(canvas.Viewport.Origin.X + p.X * canvas.Viewport.Scale),
                    (int)Math.Round(canvas.Viewport.Origin.Y + p.Y * canvas.Viewport.Scale)
                );
            }

            Exercise(
                canvas,
                (message, p, key) =>
                {
                    var point = Client(p);
                    SendMessage(
                        canvas.Handle,
                        message,
                        new IntPtr(key),
                        message == 0x0100 ? new IntPtr(1) : Pack(point.X, point.Y)
                    );
                    Forms.Application.DoEvents();
                },
                () => Forms.Application.DoEvents(),
                next
            );
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
            CheckStroke(canvas, bitmap.GetPixel);
            bitmap.Save(Path.Combine(output, "roi-editor-winform.png"));
            form.Close();
        }

        using (
            var canvas = new DP.Vision.WPF.VisionCanvasControl
            {
                Width = 800,
                Height = 600,
                Editor = new RoiEditor(),
            }
        )
        {
            var window = new Wpf.Window
            {
                Content = canvas,
                SizeToContent = Wpf.SizeToContent.WidthAndHeight,
                Title = "ROI protocol probe - native WPF",
            };
            window.Show();
            window.UpdateLayout();
            canvas.Present(frame);
            canvas.FitToWindow();
            void Pump()
            {
                var dispatcherFrame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(40),
                };
                timer.Tick += (_, __) =>
                {
                    timer.Stop();
                    dispatcherFrame.Continue = false;
                };
                timer.Start();
                Dispatcher.PushFrame(dispatcherFrame);
            }

            // 不注入全局输入：本环境物理命中目标与WPF窗口句柄不一致。
            // 检查原生事件处理器共用的公开指针接口，以及真实WPF渲染器和调度器。
            Exercise(
                canvas,
                (message, p, key) =>
                {
                    if (message == 0x0100)
                    {
                        if (key == 27)
                        {
                            canvas.Editor!.Cancel();
                        }
                        else if (key == 13)
                        {
                            canvas.Editor!.Finish();
                        }
                    }
                    else
                    {
                        var client = new PointD(
                            canvas.Viewport.Origin.X + p.X * canvas.Viewport.Scale,
                            canvas.Viewport.Origin.Y + p.Y * canvas.Viewport.Scale
                        );
                        canvas.ProcessRoiPointer(
                            message == 0x0201 ? ERoiPointerAction.Down
                                : message == 0x0202 ? ERoiPointerAction.Up
                                : ERoiPointerAction.Move,
                            client
                        );
                    }

                    Pump();
                },
                Pump,
                next
            );
            Pump();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                800,
                600,
                96,
                96,
                System.Windows.Media.PixelFormats.Pbgra32
            );
            bitmap.Render(canvas);
            var pixels = new byte[800 * 600 * 4];
            bitmap.CopyPixels(pixels, 800 * 4, 0);
            CheckStroke(
                canvas,
                (x, y) =>
                {
                    int offset = (y * 800 + x) * 4;
                    return Color.FromArgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                }
            );
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "roi-editor-wpf.png")))
            {
                encoder.Save(stream);
            }

            window.Close();
        }

        if (evidence.Center.X != 190)
        {
            throw new InvalidOperationException("Readonly evidence was mutated.");
        }

        File.WriteAllText(
            Path.Combine(output, "ROI-PASS.txt"),
            "PASS: WinForms Windows-message interaction; WPF shared pointer seam + native rendering/dispatcher (physical mouse routing NOT verified). Creation/move/cancel/config-only selection/polygon completion/vertex insert-delete and undo; latest preview held during gesture and resumed; evidence unchanged."
        );
    }

    private static void CheckStroke(IVisionCanvas canvas, Func<int, int, Color> pixel)
    {
        var roi = (RectangleGeometry)canvas.Editor!.Document.Rois[0].Shape;
        int x = (int)Math.Round(canvas.Viewport.Origin.X + roi.Center.X * canvas.Viewport.Scale),
            y =
                (int)
                    Math.Round(
                        canvas.Viewport.Origin.Y + (roi.Center.Y - roi.Height / 2) * canvas.Viewport.Scale
                    );
        for (int dy = -3; dy <= 3; dy++)
        {
            for (int dx = -3; dx <= 3; dx++)
            {
                var color = pixel(x + dx, y + dy);
                if (color.R < 160 && color.G > 140 && color.B > 140)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException("ROI config stroke missing on a frame with no evidence overlay.");
    }

    private static IntPtr Pack(int x, int y)
    {
        return new IntPtr((y & 65535) << 16 | (x & 65535));
    }

    private static void Exercise(
        IVisionCanvas canvas,
        Action<int, PointD, int> send,
        Action pump,
        CanvasFrame next
    )
    {
        var editor = canvas.Editor!;
        void Down(PointD p)
        {
            send(0x0201, p, 1);
        }

        void Move(PointD p)
        {
            send(0x0200, p, 1);
        }

        void Up(PointD p)
        {
            send(0x0202, p, 0);
        }

        void Key(int key)
        {
            send(0x0100, new PointD(0, 0), key);
        }

        editor.Tool = ERoiTool.Rectangle;
        Down(new PointD(20, 20));
        Move(new PointD(90, 60));
        if (editor.Document.Rois.Count != 0)
        {
            throw new InvalidOperationException("Pointer preview committed early.");
        }

        Up(new PointD(90, 60));
        if (editor.Document.Rois.Count != 1)
        {
            throw new InvalidOperationException("Rectangle creation failed.");
        }

        var original = (RectangleGeometry)editor.Document.Rois[0].Shape;
        if (Math.Abs(original.Width - 70) > 1 || Math.Abs(original.Height - 40) > 1)
        {
            throw new InvalidOperationException("ROI coordinates do not match the image transform.");
        }

        Down(original.Center);
        Move(new PointD(original.Center.X + 10, original.Center.Y + 5));
        Up(new PointD(original.Center.X + 10, original.Center.Y + 5));
        var moved = (RectangleGeometry)editor.Document.Rois[0].Shape;
        if (Math.Abs(moved.Center.X - original.Center.X - 10) > 1)
        {
            throw new InvalidOperationException("ROI move failed.");
        }

        var committed = editor.Document;
        Down(moved.Center);
        Move(new PointD(moved.Center.X + 20, moved.Center.Y));
        Key(27);
        Up(new PointD(moved.Center.X + 20, moved.Center.Y));
        if (!ReferenceEquals(committed, editor.Document))
        {
            throw new InvalidOperationException("Escape committed a move.");
        }

        Down(new PointD(190, 110));
        Up(new PointD(190, 110));
        if (editor.SelectedId != null || editor.Document.Rois.Count != 1)
        {
            throw new InvalidOperationException("Evidence became editable configuration.");
        }

        editor.Tool = ERoiTool.Polygon;
        foreach (var p in new[] { new PointD(120, 20), new PointD(150, 20), new PointD(150, 50) })
        {
            Down(p);
            Up(p);
        }

        Key(13);
        if (editor.Document.Rois.Count != 2)
        {
            throw new InvalidOperationException("Polygon completion failed.");
        }

        editor.Tool = ERoiTool.InsertVertex;
        Down(new PointD(135, 20));
        Up(new PointD(135, 20));
        if (((ContourGeometry)editor.Document.Rois[1].Shape).Points.Count != 4)
        {
            throw new InvalidOperationException("Contour vertex insertion failed through the canvas.");
        }

        editor.Tool = ERoiTool.DeleteVertex;
        Down(new PointD(135, 20));
        Up(new PointD(135, 20));
        if (((ContourGeometry)editor.Document.Rois[1].Shape).Points.Count != 3)
        {
            throw new InvalidOperationException("Contour vertex deletion failed through the canvas.");
        }

        editor.Undo();
        if (((ContourGeometry)editor.Document.Rois[1].Shape).Points.Count != 4)
        {
            throw new InvalidOperationException("Vertex deletion undo failed.");
        }

        editor.Undo();
        if (((ContourGeometry)editor.Document.Rois[1].Shape).Points.Count != 3)
        {
            throw new InvalidOperationException("Vertex insertion undo failed.");
        }

        editor.Tool = ERoiTool.Polyline;
        Down(new PointD(10, 100));
        Up(new PointD(10, 100));
        canvas.PostFrame(next);
        Thread.Sleep(40);
        pump();
        if (canvas.DisplayedFrameId != "edit-1")
        {
            throw new InvalidOperationException("Preview replaced the editing image mid-gesture.");
        }

        Key(27);
        Thread.Sleep(40);
        pump();
        if (canvas.DisplayedFrameId != "edit-2")
        {
            throw new InvalidOperationException("Latest preview did not resume after cancellation.");
        }

        editor.Undo();
        if (editor.Document.Rois.Count != 1)
        {
            throw new InvalidOperationException("Undo failed.");
        }

        editor.Redo();
        if (editor.Document.Rois.Count != 2)
        {
            throw new InvalidOperationException("Redo failed.");
        }
    }
}
