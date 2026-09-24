using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DP.Vision.UI;
using MaskKey = (long Region, uint Argb, int Width, int Height, int Level, int X, int Y);
using TileKey = (int Level, int X, int Y);

namespace DP.Vision.WPF;

/// <summary>原生WPF分块画布，不使用WindowsFormsHost、GDI位图或厂商运行时；宿主显式释放保留的源。</summary>
public sealed class VisionCanvasControl : FrameworkElement, IVisionCanvas, IDisposable
{
    private readonly CanvasCore<System.Windows.Media.Geometry> _core;
    private readonly DispatcherTimer _timer;
    private RenderCache<TileKey, Tile> _tiles;
    private RenderCache<MaskKey, Tile> _masks;
    private bool _disposed;

    /// <summary>创建与厂商无关的原生控件，渲染坐标单位为WPF DIP。</summary>
    public VisionCanvasControl()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _core = new CanvasCore<System.Windows.Media.Geometry>(
            InvalidateVisual,
            ReleaseMouseCapture,
            () => (ActualWidth, ActualHeight),
            _ => { }
        );
        _tiles = new RenderCache<TileKey, Tile>(_core.Options.TileCacheBytes / 2, _ => { });
        _masks = NewMaskCache();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Tick;
        Loaded += (_, __) =>
        {
            if (!_disposed)
            {
                _timer.Start();
            }
        };
        Unloaded += (_, __) => _timer.Stop();
    }

    /// <inheritdoc/>
    public RoiEditor? Editor
    {
        get => _core.Editor;
        set
        {
            Ui();
            _core.Editor = value;
        }
    }

    /// <inheritdoc/>
    public CanvasOptions Options
    {
        get => _core.Options;
        set
        {
            Ui();
            _core.Options = value ?? throw new ArgumentNullException(nameof(value), "显示设置不能为空。");
            _tiles.Dispose();
            _masks.Dispose();
            _tiles = new RenderCache<TileKey, Tile>(_core.Options.TileCacheBytes / 2, _ => { });
            _masks = NewMaskCache();
            _core.ClearPaths();
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    public CanvasViewport Viewport => _core.Viewport;

    /// <inheritdoc/>
    public string? DisplayedFrameId => _core.Frame?.FrameId;

    /// <summary>计入缓存的像素载荷，不含WPF合成器副本和源存储。</summary>
    public long CachedPixelBytes => _tiles.Bytes + _masks.Bytes;

    private RenderCache<MaskKey, Tile> NewMaskCache()
    {
        return new RenderCache<MaskKey, Tile>(
            _core.Options.TileCacheBytes / 2,
            _ => { }
        );
    }

    private void Tick(object? sender, EventArgs e)
    {
        _core.Tick();
    }

    private void Ui()
    {
        VerifyAccess();
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VisionCanvasControl), "画布已释放。");
        }
    }

    /// <inheritdoc/>
    public void ProcessRoiPointer(ERoiPointerAction action, PointD clientPoint)
    {
        Ui();
        _core.ProcessRoiPointer(action, clientPoint);
    }

    /// <inheritdoc/>
    public bool PostFrame(CanvasFrame frame)
    {
        return _core.PostFrame(frame);
    }

    /// <inheritdoc/>
    public void Present(CanvasFrame frame)
    {
        Ui();
        _core.Present(frame);
    }

    /// <inheritdoc/>
    public void ClearImage()
    {
        Ui();
        _core.ClearImage();
        _tiles.Dispose();
        _masks.Dispose();
    }

    /// <summary>改变单个图层的可见性，不修改源几何。</summary>
    /// <param name = "id">要覆盖显示状态的图层标识。</param>
    /// <param name = "visible">是否显示该图层，不删除原始几何。</param>
    public void SetLayerVisible(string id, bool visible)
    {
        Ui();
        _core.SetLayerVisible(id, visible);
    }

    /// <inheritdoc/>
    public void FitToWindow()
    {
        Ui();
        _core.FitToWindow();
    }

    /// <summary>独立于显示LOD的精确源几何命中测试。</summary>
    /// <param name = "client">WPF客户区坐标，单位为DIP，不是原图坐标。</param>
    public Visual? Pick(Point client)
    {
        return _core.HitTest(new PointD(client.X, client.Y));
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(30, 32, 36)),
            null,
            new Rect(0, 0, ActualWidth, ActualHeight)
        );
        var frame = _core.Frame;
        if (frame == null)
        {
            return;
        }

        var requests = CanvasPlanning.Tiles(
            frame.Info,
            Viewport,
            ActualWidth,
            ActualHeight,
            Options.TileSize
        );
        if (Options.ShowImage)
        {
            foreach (var request in requests)
            {
                DrawTile(drawing, ImageTile(request).Bitmap, Screen(request.Bounds));
            }
        }

        var visible = Viewport.Visible(ActualWidth, ActualHeight);
        var captions = new CaptionLayout();
        foreach (var layer in _core.ActiveLayers().Where(_core.IsLayerVisible))
        {
            foreach (var visual in layer.Visuals)
            {
                if (
                    visual.Geometry is ContourGeometry empty && empty.Points.Count == 0
                    || !visual.Geometry.Bounds.Intersects(visible, 3 / Viewport.Scale)
                )
                {
                    continue;
                }

                if (visual.Geometry is RegionGeometry region)
                {
                    foreach (var request in requests)
                    {
                        if (region.Bounds.Intersects(request.Bounds))
                        {
                            DrawTile(
                                drawing,
                                MaskTile(visual, region, request).Bitmap,
                                Screen(request.Bounds)
                            );
                        }
                    }
                }
                else
                {
                    drawing.PushTransform(
                        new MatrixTransform(
                            Viewport.Scale,
                            0,
                            0,
                            Viewport.Scale,
                            Viewport.Origin.X,
                            Viewport.Origin.Y
                        )
                    );
                    try
                    {
                        var color = ColorOf(visual.Argb);
                        Brush? fill =
                            visual.Geometry is ContourGeometry c && c.Filled
                                ? new SolidColorBrush(
                                    Color.FromArgb((byte)(color.A / 3), color.R, color.G, color.B)
                                )
                                : null;
                        drawing.DrawGeometry(
                            fill,
                            new Pen(new SolidColorBrush(color), 2 / Viewport.Scale),
                            Path(visual.Geometry)
                        );
                    }
                    finally
                    {
                        drawing.Pop();
                    }
                }

                if (!string.IsNullOrEmpty(visual.Caption))
                {
                    var box = Screen(visual.Geometry.Bounds);
                    var text = new FormattedText(
                        visual.Caption,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"),
                        12,
                        new SolidColorBrush(ColorOf(visual.Argb)),
                        VisualTreeHelper.GetDpi(this).PixelsPerDip
                    );
                    // 深色底衬保证文字在任意底色（包括同色Region）上可读；重叠标注依次下移。
                    double top = captions.Place(box.X, box.Y, text.Width, text.Height);
                    drawing.DrawRectangle(
                        new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                        null,
                        new Rect(box.X, top, text.Width, text.Height)
                    );
                    drawing.DrawText(text, new Point(box.X, top));
                }
            }
        }

        if (_core.Editor != null)
        {
            foreach (var handle in _core.Editor.Handles(25 / Viewport.Scale))
            {
                var p = Screen(new RectD(handle.Position.X, handle.Position.Y, 0, 0));
                drawing.DrawRectangle(
                    Brushes.Cyan,
                    new Pen(Brushes.Black, 1),
                    new Rect(p.X - 3, p.Y - 3, 6, 6)
                );
            }
        }
    }

    private static void DrawTile(DrawingContext drawing, BitmapSource bitmap, Rect bounds)
    {
        // 共享边缘吸附到同一设备边界，否则小数图块矩形之间会出现透明细缝。
        drawing.PushGuidelineSet(
            new GuidelineSet(new[] { bounds.Left, bounds.Right }, new[] { bounds.Top, bounds.Bottom })
        );
        try
        {
            drawing.DrawImage(bitmap, bounds);
        }
        finally
        {
            drawing.Pop();
        }
    }

    private static Color ColorOf(uint argb)
    {
        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private Rect Screen(RectD r)
    {
        return new Rect(
            Viewport.Origin.X + r.X * Viewport.Scale,
            Viewport.Origin.Y + r.Y * Viewport.Scale,
            r.Width * Viewport.Scale,
            r.Height * Viewport.Scale
        );
    }

    private Tile ImageTile(TileRequest request)
    {
        var key = (request.Level, request.X, request.Y);
        _tiles.TryGet(key, out var entry);
        if (entry != null && entry.Revision == _core.Revision)
        {
            return entry;
        }

        using var source = _core.Frame!.ReadTile(request.Level, request.X, request.Y, Options.TileSize);
        var pixels = DisplayPixels.From(source, Options);
        var format =
            pixels.Layout == EPixelLayout.Gray8 ? PixelFormats.Gray8
            : pixels.Layout == EPixelLayout.Bgr24 ? PixelFormats.Bgr24
            : PixelFormats.Bgra32;
        if (entry == null)
        {
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, format, null);
            bitmap.WritePixels(
                new Int32Rect(0, 0, pixels.Width, pixels.Height),
                pixels.Bytes,
                pixels.Stride,
                0
            );
            entry = new Tile { Bitmap = bitmap, Revision = _core.Revision };
            if (!_tiles.Add(key, entry, (long)Options.TileSize * Options.TileSize * 4))
            {
                throw new InvalidOperationException("图块超出缓存预算。");
            }
        }
        else
        {
            var bitmap = (WriteableBitmap)entry.Bitmap;
            if (
                bitmap.PixelWidth != pixels.Width
                || bitmap.PixelHeight != pixels.Height
                || bitmap.Format != format
            )
            {
                bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, format, null);
            }

            bitmap.WritePixels(
                new Int32Rect(0, 0, pixels.Width, pixels.Height),
                pixels.Bytes,
                pixels.Stride,
                0
            );
            entry.Bitmap = bitmap;
            entry.Revision = _core.Revision;
        }

        return entry;
    }

    private Tile MaskTile(Visual visual, RegionGeometry region, TileRequest request)
    {
        var key = _core.MaskKey(visual, region, request);
        if (_masks.TryGet(key, out var existing))
        {
            return existing!;
        }

        int width = request.PixelWidth,
            height = request.PixelHeight;
        var mask = RegionMask.Tile(
            region,
            (int)request.Bounds.X,
            (int)request.Bounds.Y,
            width,
            height,
            request.Level
        );
        // 与填充轮廓一致使用1/3不透明度，底图仍可见；成员像素仍按原始游程逐像素绘制。
        var color = ColorOf(visual.Argb);
        var fill = Color.FromArgb((byte)(color.A / 3), color.R, color.G, color.B);
        var palette = new BitmapPalette(
            Enumerable
                .Range(0, 256)
                .Select(i => i == 255 ? fill : Colors.Transparent)
                .ToList()
        );
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, palette, mask, width);
        bitmap.Freeze();
        var tile = new Tile { Bitmap = bitmap };
        if (!_masks.Add(key, tile, (long)Options.TileSize * Options.TileSize * 4))
        {
            throw new InvalidOperationException("Region掩码超出缓存预算。");
        }

        return tile;
    }

    private System.Windows.Media.Geometry Path(DP.Vision.Geometry geometry)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return _core.GetPath(geometry, Viewport.Scale * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY), BuildPath);
    }

    private System.Windows.Media.Geometry BuildPath(DP.Vision.Geometry geometry, double tolerance)
    {
        System.Windows.Media.Geometry path;
        if (geometry is DP.Vision.EllipseGeometry ellipse)
        {
            path = new System.Windows.Media.EllipseGeometry(
                new Point(ellipse.Center.X, ellipse.Center.Y),
                ellipse.RadiusX,
                ellipse.RadiusY
            );
            path.Transform = new RotateTransform(
                ellipse.Angle * 180 / Math.PI,
                ellipse.Center.X,
                ellipse.Center.Y
            );
        }
        else
        {
            var stream = new StreamGeometry { FillRule = FillRule.EvenOdd };
            IReadOnlyList<PointD> points;
            bool closed,
                filled;
            if (geometry is ContourGeometry contour)
            {
                points = ContourLod.Simplify(contour, tolerance, _core.LodChecks);
                closed = contour.Closed;
                filled = contour.Filled;
            }
            else if (geometry is RectangleGeometry rectangle)
            {
                points = rectangle.Corners;
                closed = true;
                filled = false;
            }
            else
            {
                throw new NotSupportedException("画布不支持绘制此类几何。");
            }

            if (points.Count == 1)
            {
                path = new System.Windows.Media.EllipseGeometry(
                    new Point(points[0].X, points[0].Y),
                    .25,
                    .25
                );
            }
            else
            {
                using (var context = stream.Open())
                {
                    context.BeginFigure(new Point(points[0].X, points[0].Y), filled, closed);
                    context.PolyLineTo(
                        points.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(),
                        true,
                        false
                    );
                }

                path = stream;
            }
        }

        path.Freeze();
        return path;
    }

    private PointD At(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return new PointD(p.X, p.Y);
    }

    private static ECanvasButton? ButtonOf(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => ECanvasButton.Left,
            MouseButton.Middle => ECanvasButton.Middle,
            MouseButton.Right => ECanvasButton.Right,
            _ => null,
        };
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _core.Wheel(e.Delta, At(e));
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var button = ButtonOf(e.ChangedButton);
        if (button == null)
        {
            return;
        }

        var (capture, handled) = _core.MouseDown(button.Value, At(e), e.ClickCount);
        if (capture)
        {
            CaptureMouse();
        }

        if (handled)
        {
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _core.MouseMove(At(e));
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var button = ButtonOf(e.ChangedButton);
        if (
            button != null
            && _core.MouseUp(
                button.Value,
                At(e),
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance
            )
        )
        {
            e.Handled = true;
        }

        ReleaseMouseCapture();
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _core.CaptureLost();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var key = e.Key switch
        {
            Key.Home => ECanvasKey.Home,
            Key.Z => ECanvasKey.Z,
            Key.Y => ECanvasKey.Y,
            Key.Delete => ECanvasKey.Delete,
            Key.Escape => ECanvasKey.Escape,
            Key.Enter => ECanvasKey.Enter,
            Key.Back => ECanvasKey.Backspace,
            _ => ECanvasKey.Other,
        };
        var modifiers = Keyboard.Modifiers;
        bool control = (modifiers & ModifierKeys.Control) != 0,
            shift = (modifiers & ModifierKeys.Shift) != 0;
        if (_core.KeyDown(key, control, shift))
        {
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_disposed)
        {
            _core.Resized();
        }
    }

    /// <summary>停止调度并释放源和缓存引用，必须在Dispatcher线程调用。</summary>
    public void Dispose()
    {
        VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        _core.Dispose();
        _tiles.Dispose();
        _masks.Dispose();
        InvalidateVisual();
    }

    private sealed class Tile
    {
        internal BitmapSource Bitmap = null!;
        internal long Revision = -1;
    }
}
