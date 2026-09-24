using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DP.Vision.UI;

namespace DP.Vision.Winform;

/// <summary>原生GDI+分块画布；UI操作在所属线程执行，PostFrame线程安全且只保留最新预览。</summary>
public sealed partial class VisionCanvasControl : Control, IVisionCanvas
{
    private CanvasFrame? _frame;
    private long _revision;
    private readonly LatestFrameMailbox _mailbox = new LatestFrameMailbox();
    private readonly Timer _timer;
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
    private Point? _rightDown;
    private bool _fit = true;
    private readonly int _thread = Environment.CurrentManagedThreadId;
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
            Capture = false;
        }

        PrunePaths();
        Invalidate();
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
            _paths[key].Dispose();
            _paths.Remove(key);
        }

        foreach (var key in _identities.Keys.Where(k => !live.Contains(k)).ToArray())
        {
            _identities.Remove(key);
        }
    }

    /// <summary>创建不加载厂商运行时、可安全用于设计器的画布。</summary>
    public VisionCanvasControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(30, 32, 36);
        TabStop = true;
        _tiles = NewCache();
        _masks = NewCache();
        _timer = new Timer { Interval = 16 };
        _timer.Tick += (_, __) =>
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
        };
        _timer.Start();
    }

    private RenderCache<Tile> NewCache()
    {
        return new RenderCache<Tile>(_options.TileCacheBytes / 2, t => t.Dispose());
    }

    private void Ui()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            throw new InvalidOperationException("Use PostFrame from producer threads.");
        }

        if (IsDisposed)
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
            ClearPaths();
            Invalidate();
        }
    }

    /// <inheritdoc/>
    public CanvasViewport Viewport { get; } = new CanvasViewport();

    /// <inheritdoc/>
    public string? DisplayedFrameId => _frame?.FrameId;

    /// <summary>计入缓存的原生图块/掩码像素载荷，不含源图、几何和图形子系统开销。</summary>
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
            Invalidate();
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
        Invalidate();
    }

    /// <summary>单独覆盖一个图层的可见性，不重建其他图层。</summary>
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
        Invalidate();
    }

    /// <inheritdoc/>
    public void FitToWindow()
    {
        Ui();
        _editor?.Cancel();
        _fit = true;
        if (_frame != null)
        {
            Viewport.Fit(_frame.Info.Width, _frame.Info.Height, ClientSize.Width, ClientSize.Height);
        }

        Invalidate();
    }

    /// <summary>基于精确源几何的命中测试，不受LOD影响。</summary>
    /// <param name = "client">控件客户区坐标，单位为屏幕像素，不是原图坐标。</param>
    public Visual? HitTest(Point client)
    {
        if (_frame?.Overlay == null)
        {
            return null;
        }

        var p = Viewport.ToImage(new PointD(client.X, client.Y));
        return _frame
            .Overlay.Layers.Reverse()
            .Where(LayerVisible)
            .SelectMany(l => l.Visuals.Reverse())
            .FirstOrDefault(v => v.Geometry.Contains(p, 3 / Viewport.Scale));
    }

    private bool LayerVisible(CanvasLayer l)
    {
        return _visibility.TryGetValue(l.Id, out bool v) ? v : l.Visible;
    }

    /// <inheritdoc/>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RenderTo(e.Graphics);
    }

    /// <summary>使用本控件客户区和视口，将当前分块场景绘制到宿主拥有的Graphics；仅限UI线程，不复制整帧，也不释放Graphics。</summary>
    /// <param name = "graphics">宿主拥有的绘图上下文，仅在调用期间借用，不由本方法释放。</param>
    public void RenderTo(Graphics graphics)
    {
        Ui();
        if (graphics == null)
        {
            throw new ArgumentNullException(nameof(graphics));
        }

        if (_frame == null)
        {
            return;
        }

        var g = graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var requests = CanvasPlanning.Tiles(_frame.Info, Viewport, Width, Height, Options.TileSize);
        if (Options.ShowImage)
        {
            foreach (var request in requests)
            {
                var tile = ImageTile(request);
                g.DrawImage(
                    tile.Bitmap,
                    Screen(request.Bounds),
                    new RectangleF(0, 0, tile.Bitmap.Width, tile.Bitmap.Height),
                    GraphicsUnit.Pixel
                );
            }
        }

        var visible = Viewport.Visible(Width, Height);
        var captions = new CaptionLayout();
        foreach (var layer in ActiveLayers().Where(LayerVisible))
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
                        if (!region.Bounds.Intersects(request.Bounds))
                        {
                            continue;
                        }

                        var tile = MaskTile(visual, region, request);
                        g.DrawImage(
                            tile.Bitmap,
                            Screen(request.Bounds),
                            new RectangleF(0, 0, tile.Bitmap.Width, tile.Bitmap.Height),
                            GraphicsUnit.Pixel
                        );
                    }
                }
                else
                {
                    var path = Path(visual.Geometry);
                    var state = g.Save();
                    try
                    {
                        g.TranslateTransform((float)Viewport.Origin.X, (float)Viewport.Origin.Y);
                        g.ScaleTransform((float)Viewport.Scale, (float)Viewport.Scale);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        var color = Color.FromArgb(unchecked((int)visual.Argb));
                        if (visual.Geometry is ContourGeometry c && c.Filled)
                        {
                            using var brush = new SolidBrush(Color.FromArgb(color.A / 3, color));
                            g.FillPath(brush, path);
                        }

                        using var pen = new Pen(color, (float)(2 / Viewport.Scale));
                        g.DrawPath(pen, path);
                    }
                    finally
                    {
                        g.Restore(state);
                    }
                }

                if (!string.IsNullOrEmpty(visual.Caption))
                {
                    // 深色底衬保证文字在任意底色（包括同色Region）上可读；重叠标注依次下移。
                    var box = Screen(visual.Geometry.Bounds);
                    var size = g.MeasureString(visual.Caption, Font);
                    float top = (float)captions.Place(box.X, box.Y, size.Width, size.Height);
                    using var back = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                    g.FillRectangle(back, box.X, top, size.Width, size.Height);
                    using var brush = new SolidBrush(Color.FromArgb(unchecked((int)visual.Argb)));
                    g.DrawString(visual.Caption, Font, brush, box.X, top);
                }
            }
        }

        if (_editor != null)
        {
            foreach (var handle in _editor.Handles(25 / Viewport.Scale))
            {
                var p = Screen(new RectD(handle.Position.X, handle.Position.Y, 0, 0));
                g.FillRectangle(Brushes.Cyan, p.X - 3, p.Y - 3, 6, 6);
                g.DrawRectangle(Pens.Black, p.X - 3, p.Y - 3, 6, 6);
            }
        }
    }

    private RectangleF Screen(RectD r)
    {
        return new RectangleF(
            (float)(Viewport.Origin.X + r.X * Viewport.Scale),
            (float)(Viewport.Origin.Y + r.Y * Viewport.Scale),
            (float)(r.Width * Viewport.Scale),
            (float)(r.Height * Viewport.Scale)
        );
    }

    private Tile ImageTile(TileRequest request)
    {
        string key = request.Key;
        _tiles.TryGet(key, out var entry);
        if (entry != null && entry.Revision == _revision)
        {
            return entry;
        }

        using var source = _frame!.ReadTile(request.Level, request.X, request.Y, Options.TileSize);
        var pixels = DisplayPixels.From(source, Options);
        var format = PixelFormatFor(pixels.Layout);
        if (entry == null)
        {
            entry = new Tile
            {
                Bitmap = CreateBitmap(pixels.Width, pixels.Height, format),
                Revision = _revision,
            };
            try
            {
                Copy(entry.Bitmap, pixels.Bytes, pixels.Stride);
                if (!_tiles.Add(key, entry, (long)Options.TileSize * Options.TileSize * 4))
                {
                    throw new InvalidOperationException("Tile exceeds cache budget.");
                }
            }
            catch
            {
                entry.Dispose();
                throw;
            }
        }
        else
        {
            if (
                entry.Bitmap.Width != pixels.Width
                || entry.Bitmap.Height != pixels.Height
                || entry.Bitmap.PixelFormat != format
            )
            {
                var bitmap = CreateBitmap(pixels.Width, pixels.Height, format);
                Copy(bitmap, pixels.Bytes, pixels.Stride);
                entry.Bitmap.Dispose();
                entry.Bitmap = bitmap;
            }
            else
            {
                Copy(entry.Bitmap, pixels.Bytes, pixels.Stride);
            }

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
        var bitmap = CreateBitmap(width, height, PixelFormat.Format8bppIndexed);
        var palette = bitmap.Palette;
        // 与填充轮廓一致使用1/3不透明度，底图仍可见；成员像素仍按原始游程逐像素绘制。
        var color = Color.FromArgb(unchecked((int)visual.Argb));
        var fill = Color.FromArgb(color.A / 3, color);
        for (int i = 0; i < 256; i++)
        {
            palette.Entries[i] = i == 255 ? fill : Color.Transparent;
        }

        bitmap.Palette = palette;
        var tile = new Tile { Bitmap = bitmap };
        try
        {
            Copy(bitmap, mask, width);
            if (!_masks.Add(key, tile, (long)Options.TileSize * Options.TileSize * 4))
            {
                throw new InvalidOperationException("Mask exceeds cache budget.");
            }

            return tile;
        }
        catch
        {
            tile.Dispose();
            throw;
        }
    }

    private GraphicsPath Path(DP.Vision.Geometry geometry)
    {
        double tolerance =
            geometry is ContourGeometry && !_editableGeometry.Contains(geometry)
                ? CanvasPlanning.LodTolerance(Options, Viewport.Scale)
                : 0;
        if (_paths.TryGetValue(geometry, out var cached) && cached.Tolerance == tolerance)
        {
            return cached.Path;
        }

        var path = new GraphicsPath(FillMode.Alternate);
        try
        {
            if (geometry is ContourGeometry contour)
            {
                var points = ContourLod
                    .Simplify(contour, tolerance, _lodChecks)
                    .Select(p => new PointF((float)p.X, (float)p.Y))
                    .ToArray();
                if (points.Length == 1)
                {
                    path.AddEllipse(points[0].X - .25f, points[0].Y - .25f, .5f, .5f);
                }
                else
                {
                    path.AddLines(points);
                    if (contour.Closed)
                    {
                        path.CloseFigure();
                    }
                }
            }
            else if (geometry is RectangleGeometry rectangle)
            {
                path.AddPolygon(rectangle.Corners.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray());
            }
            else if (geometry is EllipseGeometry ellipse)
            {
                path.AddEllipse(
                    (float)-ellipse.RadiusX,
                    (float)-ellipse.RadiusY,
                    (float)(2 * ellipse.RadiusX),
                    (float)(2 * ellipse.RadiusY)
                );
                using var matrix = new Matrix();
                matrix.Rotate((float)(ellipse.Angle * 180 / Math.PI), MatrixOrder.Append);
                matrix.Translate((float)ellipse.Center.X, (float)ellipse.Center.Y, MatrixOrder.Append);
                path.Transform(matrix);
            }
            else
            {
                throw new NotSupportedException("Unknown geometry renderer.");
            }

            cached?.Dispose();
            _paths[geometry] = new PathItem { Path = path, Tolerance = tolerance };
            return path;
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    private static PixelFormat PixelFormatFor(EPixelLayout layout)
    {
        return layout == EPixelLayout.Gray8 ? PixelFormat.Format8bppIndexed
            : layout == EPixelLayout.Bgr24 ? PixelFormat.Format24bppRgb
            : PixelFormat.Format32bppArgb;
    }

    private static Bitmap CreateBitmap(int width, int height, PixelFormat format)
    {
        var bitmap = new Bitmap(width, height, format);
        if (format == PixelFormat.Format8bppIndexed)
        {
            var palette = bitmap.Palette;
            for (int i = 0; i < 256; i++)
            {
                palette.Entries[i] = Color.FromArgb(i, i, i);
            }

            bitmap.Palette = palette;
        }

        return bitmap;
    }

    private static void Copy(Bitmap bitmap, byte[] bytes, int stride)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            bitmap.PixelFormat
        );
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(bytes, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _editor?.CancelDrag();
        _fit = false;
        Viewport.Zoom(Math.Pow(1.2, e.Delta / 120.0), new PointD(e.X, e.Y));
        Invalidate();
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
        {
            _editor?.CancelDrag();
            _fit = false;
            _pan = e.Location;
            _rightDown = e.Button == MouseButtons.Right ? e.Location : (Point?)null;
            Capture = true;
            return;
        }

        if (e.Button == MouseButtons.Left && _frame != null && _editor != null)
        {
            if (e.Clicks > 1 && (_editor.Tool == ERoiTool.Polygon || _editor.Tool == ERoiTool.Polyline))
            {
                _editor.Finish();
                return;
            }

            ProcessRoiPointer(ERoiPointerAction.Down, new PointD(e.X, e.Y));
            _roiDrag =
                _editor.IsEditing && _editor.Tool != ERoiTool.Polygon && _editor.Tool != ERoiTool.Polyline;
            if (_roiDrag)
            {
                Capture = true;
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pan.HasValue)
        {
            Viewport.Pan(e.X - _pan.Value.X, e.Y - _pan.Value.Y);
            _pan = e.Location;
            Invalidate();
        }
        else if (_frame != null)
        {
            ProcessRoiPointer(ERoiPointerAction.Move, new PointD(e.X, e.Y));
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _roiDrag)
        {
            _roiDrag = false;
            ProcessRoiPointer(ERoiPointerAction.Up, new PointD(e.X, e.Y));
        }

        // 右键单击（未拖动平移）结束正在逐点绘制的多边形/折线，闭合到首点；没有待定顶点时Finish不做任何事。
        if (e.Button == MouseButtons.Right && _rightDown.HasValue && _editor != null)
        {
            var drag = SystemInformation.DragSize;
            if (
                Math.Abs(e.X - _rightDown.Value.X) <= drag.Width
                && Math.Abs(e.Y - _rightDown.Value.Y) <= drag.Height
            )
            {
                _editor.Finish();
            }
        }

        _rightDown = null;
        _pan = null;
        Capture = false;
    }

    /// <inheritdoc/>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            _pan = null;
            if (_roiDrag)
            {
                _roiDrag = false;
                _editor?.Cancel();
            }
        }
    }

    /// <inheritdoc/>
    protected override bool IsInputKey(Keys keyData)
    {
        return keyData == Keys.Enter || keyData == Keys.Escape || base.IsInputKey(keyData);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Home)
        {
            FitToWindow();
            e.Handled = true;
        }

        if (_editor == null)
        {
            return;
        }

        if (e.Control && e.KeyCode == Keys.Z)
        {
            if (e.Shift)
            {
                _editor.Redo();
            }
            else
            {
                _editor.Undo();
            }
        }
        else if (e.Control && e.KeyCode == Keys.Y)
        {
            _editor.Redo();
        }
        else if (e.KeyCode == Keys.Delete)
        {
            _editor.DeleteSelected();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _editor.Cancel();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            _editor.Finish();
        }
        else if (e.KeyCode == Keys.Back)
        {
            _editor.Backspace();
        }
        else
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <inheritdoc/>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_fit && _frame != null && !IsDisposed)
        {
            FitToWindow();
        }
    }

    private void ClearPaths()
    {
        foreach (var value in _paths.Values)
        {
            value.Dispose();
        }

        _paths.Clear();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_editor != null)
            {
                _editor.Changed -= EditorChanged;
                _editor.Cancel();
                _editor = null;
            }

            _editLayer = null;
            _editableGeometry.Clear();
            _timer.Stop();
            _timer.Dispose();
            _mailbox.Dispose();
            _frame?.Dispose();
            _frame = null;
            _tiles.Dispose();
            _masks.Dispose();
            ClearPaths();
            _identities.Clear();
        }

        base.Dispose(disposing);
    }
}
