using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DP.Vision.UI;

namespace DP.Vision.WPF;

/// <summary>原生WPF分块画布，不使用WindowsFormsHost、GDI位图或厂商运行时；宿主显式释放保留的源。</summary>
public sealed partial class VisionCanvasControl : FrameworkElement, IVisionCanvas, IDisposable
{
    private CanvasFrame? _frame;
    private long _revision;
    private bool _disposed;
    private readonly LatestFrameMailbox _mailbox = new LatestFrameMailbox();
    private readonly DispatcherTimer _timer;
    private RenderCache<Tile> _tiles;
    private RenderCache<Tile> _masks;
    private readonly Dictionary<DP.Vision.Geometry, PathItem> _paths =
        new Dictionary<DP.Vision.Geometry, PathItem>();
    private readonly Dictionary<DP.Vision.Geometry, long> _identities =
        new Dictionary<DP.Vision.Geometry, long>();
    private long _nextId;
    private int _lodChecks = 4000000;
    private readonly Dictionary<string, bool> _visibility = new Dictionary<string, bool>();
    private CanvasOptions _options = new CanvasOptions();
    private Point? _pan;
    private bool _fit = true;
    private RoiEditor? _editor;
    private CanvasLayer? _editLayer;
    private bool _roiDrag;
    private readonly HashSet<DP.Vision.Geometry> _editableGeometry = new HashSet<DP.Vision.Geometry>();

    /// <inheritdoc/>
    public RoiEditor? Editor
    {
        get => _editor;
        set
        {
            Ui();
            if (ReferenceEquals(_editor, value))
            {
                return;
            }

            if (_editor != null)
            {
                _editor.Cancel();
                _editor.Changed -= EditorChanged;
            }

            _editor = value;
            if (_editor != null)
            {
                _editor.Changed += EditorChanged;
            }

            EditorChanged(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc/>
    public void ProcessRoiPointer(ERoiPointerAction action, PointD clientPoint)
    {
        Ui();
        if (!Enum.IsDefined(typeof(ERoiPointerAction), action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        if (_frame == null || _editor == null)
        {
            return;
        }

        var point = Viewport.ToImage(clientPoint);
        switch (action)
        {
            case ERoiPointerAction.Down:
                _editor.PointerDown(point, 5 / Viewport.Scale);
                break;
            case ERoiPointerAction.Move:
                _editor.PointerMove(point);
                break;
            case ERoiPointerAction.Up:
                _editor.PointerUp(point);
                break;
        }
    }

    private void EditorChanged(object? sender, EventArgs e)
    {
        _editLayer = _editor?.DisplayLayer();
        _editableGeometry.Clear();
        if (_editLayer != null)
        {
            foreach (var v in _editLayer.Visuals)
            {
                _editableGeometry.Add(v.Geometry);
            }
        }

        if (_roiDrag && _editor?.IsEditing != true)
        {
            _roiDrag = false;
            ReleaseMouseCapture();
        }

        PrunePaths();
        InvalidateVisual();
    }

    private IEnumerable<CanvasLayer> ActiveLayers()
    {
        return (_frame?.Overlay?.Layers ?? Array.Empty<CanvasLayer>()).Concat(
            _editLayer == null ? Enumerable.Empty<CanvasLayer>() : new[] { _editLayer }
        );
    }

    private void PrunePaths()
    {
        var live = new HashSet<DP.Vision.Geometry>(
            ActiveLayers().SelectMany(l => l.Visuals).Select(v => v.Geometry)
        );
        foreach (var key in _paths.Keys.Where(k => !live.Contains(k)).ToArray())
        {
            _paths.Remove(key);
        }

        foreach (var key in _identities.Keys.Where(k => !live.Contains(k)).ToArray())
        {
            _identities.Remove(key);
        }
    }

    /// <summary>创建与厂商无关的原生控件，渲染坐标单位为WPF DIP。</summary>
    public VisionCanvasControl()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _tiles = NewCache();
        _masks = NewCache();
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

    private void Tick(object? sender, EventArgs e)
    {
        if (_editor?.IsEditing == true)
        {
            return;
        }

        using var frame = _mailbox.Take();
        if (frame != null)
        {
            Present(frame);
        }
    }

    private RenderCache<Tile> NewCache()
    {
        return new RenderCache<Tile>(_options.TileCacheBytes / 2, _ => { });
    }

    private void Ui()
    {
        VerifyAccess();
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VisionCanvasControl));
        }
    }

    /// <inheritdoc/>
    public CanvasOptions Options
    {
        get => _options;
        set
        {
            Ui();
            _options = value ?? throw new ArgumentNullException(nameof(value));
            _tiles.Dispose();
            _masks.Dispose();
            _tiles = NewCache();
            _masks = NewCache();
            _paths.Clear();
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    public CanvasViewport Viewport { get; } = new CanvasViewport();

    /// <inheritdoc/>
    public string? DisplayedFrameId => _frame?.FrameId;

    /// <summary>计入缓存的像素载荷，不含WPF合成器副本和源存储。</summary>
    public long CachedPixelBytes => _tiles.Bytes + _masks.Bytes;

    /// <inheritdoc/>
    public bool PostFrame(CanvasFrame frame)
    {
        return _mailbox.Post(frame);
    }

    /// <inheritdoc/>
    public void Present(CanvasFrame frame)
    {
        Ui();
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (_frame != null && frame.Sequence <= _frame.Sequence)
        {
            return;
        }

        var next = frame.Retain();
        bool fit =
            _frame == null || _frame.Info.Width != next.Info.Width || _frame.Info.Height != next.Info.Height;
        var old = _frame;
        if (old != null && old.FrameId != next.FrameId)
        {
            _editor?.Cancel();
        }

        _frame = next;
        if (old == null || old.FrameId != next.FrameId || old.Info.Layout != next.Info.Layout || fit)
        {
            _revision++;
        }

        _mailbox.AdvanceTo(next.Sequence);
        old?.Dispose();
        _lodChecks = Math.Max(
            1,
            4000000
                / Math.Max(
                    1,
                    next.Overlay?.Layers.Sum(l => l.Visuals.Count(v => v.Geometry is ContourGeometry)) ?? 0
                )
        );
        PrunePaths();
        if (fit)
        {
            FitToWindow();
        }
        else
        {
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    public void ClearImage()
    {
        Ui();
        _editor?.Cancel();
        using var pending = _mailbox.Take();
        var old = _frame;
        _frame = null;
        old?.Dispose();
        _revision++;
        _tiles.Dispose();
        _masks.Dispose();
        PrunePaths();
        InvalidateVisual();
    }

    /// <summary>改变单个图层的可见性，不修改源几何。</summary>
    /// <param name = "id">要覆盖显示状态的图层标识。</param>
    /// <param name = "visible">是否显示该图层，不删除原始几何。</param>
    public void SetLayerVisible(string id, bool visible)
    {
        Ui();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Layer ID required.");
        }

        _visibility[id] = visible;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    public void FitToWindow()
    {
        Ui();
        _editor?.Cancel();
        _fit = true;
        if (_frame != null)
        {
            Viewport.Fit(_frame.Info.Width, _frame.Info.Height, ActualWidth, ActualHeight);
        }

        InvalidateVisual();
    }

    /// <summary>独立于显示LOD的精确源几何命中测试。</summary>
    /// <param name = "client">WPF客户区坐标，单位为DIP，不是原图坐标。</param>
    public Visual? Pick(Point client)
    {
        if (_frame?.Overlay == null)
        {
            return null;
        }

        var p = Viewport.ToImage(new PointD(client.X, client.Y));
        return _frame
            .Overlay.Layers.Reverse()
            .Where(Visible)
            .SelectMany(l => l.Visuals.Reverse())
            .FirstOrDefault(v => v.Geometry.Contains(p, 3 / Viewport.Scale));
    }

    private bool Visible(CanvasLayer l)
    {
        return _visibility.TryGetValue(l.Id, out bool v) ? v : l.Visible;
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
        if (_frame == null)
        {
            return;
        }

        var requests = CanvasPlanning.Tiles(
            _frame.Info,
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
        foreach (var layer in ActiveLayers().Where(Visible))
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
                    drawing.DrawText(text, new Point(box.X, box.Y));
                }
            }
        }

        if (_editor != null)
        {
            foreach (var handle in _editor.Handles(25 / Viewport.Scale))
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
        _tiles.TryGet(request.Key, out var entry);
        if (entry != null && entry.Revision == _revision)
        {
            return entry;
        }

        using var source = _frame!.ReadTile(request.Level, request.X, request.Y, Options.TileSize);
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
            entry = new Tile { Bitmap = bitmap, Revision = _revision };
            if (!_tiles.Add(request.Key, entry, (long)Options.TileSize * Options.TileSize * 4))
            {
                throw new InvalidOperationException("Tile exceeds cache budget.");
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
            entry.Revision = _revision;
        }

        return entry;
    }

    private Tile MaskTile(Visual visual, RegionGeometry region, TileRequest request)
    {
        if (!_identities.TryGetValue(region, out long id))
        {
            id = ++_nextId;
            _identities.Add(region, id);
        }

        string key =
            id + ":" + visual.Argb + ":" + _frame!.Info.Width + ":" + _frame.Info.Height + ":" + request.Key;
        if (_masks.TryGet(key, out var existing))
        {
            return existing!;
        }

        int factor = 1 << request.Level,
            width = (int)Math.Ceiling(request.Bounds.Width / factor),
            height = (int)Math.Ceiling(request.Bounds.Height / factor);
        var mask = RegionMask.Tile(
            region,
            (int)request.Bounds.X,
            (int)request.Bounds.Y,
            width,
            height,
            request.Level
        );
        var palette = new BitmapPalette(
            Enumerable
                .Range(0, 256)
                .Select(i => i == 255 ? ColorOf(visual.Argb) : Colors.Transparent)
                .ToList()
        );
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, palette, mask, width);
        bitmap.Freeze();
        var tile = new Tile { Bitmap = bitmap };
        if (!_masks.Add(key, tile, (long)Options.TileSize * Options.TileSize * 4))
        {
            throw new InvalidOperationException("Mask exceeds cache budget.");
        }

        return tile;
    }

    private System.Windows.Media.Geometry Path(DP.Vision.Geometry geometry)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double tolerance =
            geometry is ContourGeometry && !_editableGeometry.Contains(geometry)
                ? CanvasPlanning.LodTolerance(
                    Options,
                    Viewport.Scale * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)
                )
                : 0;
        if (_paths.TryGetValue(geometry, out var cached) && cached.Tolerance == tolerance)
        {
            return cached.Path;
        }

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
                points = ContourLod.Simplify(contour, tolerance, _lodChecks);
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
                throw new NotSupportedException("Unknown geometry renderer.");
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
        _paths[geometry] = new PathItem { Path = path, Tolerance = tolerance };
        return path;
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _editor?.Cancel();
        _fit = false;
        var p = e.GetPosition(this);
        Viewport.Zoom(Math.Pow(1.2, e.Delta / 120.0), new PointD(p.X, p.Y));
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right)
        {
            _editor?.Cancel();
            _fit = false;
            _pan = e.GetPosition(this);
            CaptureMouse();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _frame != null && _editor != null)
        {
            if (e.ClickCount > 1 && (_editor.Tool == ERoiTool.Polygon || _editor.Tool == ERoiTool.Polyline))
            {
                _editor.Finish();
                e.Handled = true;
                return;
            }

            var p = e.GetPosition(this);
            ProcessRoiPointer(ERoiPointerAction.Down, new PointD(p.X, p.Y));
            _roiDrag =
                _editor.IsEditing && _editor.Tool != ERoiTool.Polygon && _editor.Tool != ERoiTool.Polyline;
            if (_roiDrag)
            {
                CaptureMouse();
            }

            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_pan.HasValue)
        {
            Viewport.Pan(p.X - _pan.Value.X, p.Y - _pan.Value.Y);
            _pan = p;
            InvalidateVisual();
        }
        else if (_frame != null)
        {
            ProcessRoiPointer(ERoiPointerAction.Move, new PointD(p.X, p.Y));
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Left && _roiDrag)
        {
            _roiDrag = false;
            var p = e.GetPosition(this);
            ProcessRoiPointer(ERoiPointerAction.Up, new PointD(p.X, p.Y));
        }

        _pan = null;
        ReleaseMouseCapture();
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pan = null;
        if (_roiDrag)
        {
            _roiDrag = false;
            _editor?.Cancel();
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Home)
        {
            FitToWindow();
            e.Handled = true;
        }

        if (_editor == null)
        {
            return;
        }

        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && e.Key == Key.Z)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _editor.Redo();
            }
            else
            {
                _editor.Undo();
            }
        }
        else if (control && e.Key == Key.Y)
        {
            _editor.Redo();
        }
        else if (e.Key == Key.Delete)
        {
            _editor.DeleteSelected();
        }
        else if (e.Key == Key.Escape)
        {
            _editor.Cancel();
        }
        else if (e.Key == Key.Enter)
        {
            _editor.Finish();
        }
        else if (e.Key == Key.Back)
        {
            _editor.Backspace();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fit && _frame != null && !_disposed)
        {
            FitToWindow();
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
        if (_editor != null)
        {
            _editor.Changed -= EditorChanged;
            _editor.Cancel();
            _editor = null;
        }

        _editLayer = null;
        _editableGeometry.Clear();
        _timer.Stop();
        _timer.Tick -= Tick;
        _mailbox.Dispose();
        _frame?.Dispose();
        _frame = null;
        _tiles.Dispose();
        _masks.Dispose();
        _paths.Clear();
        _identities.Clear();
        InvalidateVisual();
    }
}
