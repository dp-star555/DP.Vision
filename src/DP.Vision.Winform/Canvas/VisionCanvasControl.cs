using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DP.Vision.UI;
using MaskKey = (long Region, uint Argb, int Width, int Height, int Level, int X, int Y);
using TileKey = (int Level, int X, int Y);

namespace DP.Vision.Winform;

/// <summary>原生GDI+分块画布；UI操作在所属线程执行，PostFrame线程安全且只保留最新预览。</summary>
public sealed class VisionCanvasControl : Control, IVisionCanvas
{
    private readonly CanvasCore<GraphicsPath> _core;
    private readonly Timer _timer;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private RenderCache<TileKey, Tile> _tiles;
    private RenderCache<MaskKey, Tile> _masks;

    /// <summary>创建不加载厂商运行时、可安全用于设计器的画布。</summary>
    public VisionCanvasControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(30, 32, 36);
        TabStop = true;
        _core = new CanvasCore<GraphicsPath>(
            Invalidate,
            () => Capture = false,
            () => (ClientSize.Width, ClientSize.Height),
            path => path.Dispose()
        );
        _tiles = NewTileCache();
        _masks = NewMaskCache();
        _timer = new Timer { Interval = 16 };
        _timer.Tick += (_, __) => _core.Tick();
        _timer.Start();
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
            _tiles = NewTileCache();
            _masks = NewMaskCache();
            _core.ClearPaths();
            Invalidate();
        }
    }

    /// <inheritdoc/>
    public CanvasViewport Viewport => _core.Viewport;

    /// <inheritdoc/>
    public string? DisplayedFrameId => _core.Frame?.FrameId;

    /// <summary>计入缓存的原生图块/掩码像素载荷，不含源图、几何和图形子系统开销。</summary>
    public long CachedPixelBytes => _tiles.Bytes + _masks.Bytes;

    private RenderCache<TileKey, Tile> NewTileCache()
    {
        return new RenderCache<TileKey, Tile>(_core.Options.TileCacheBytes / 2, t => t.Dispose());
    }

    private RenderCache<MaskKey, Tile> NewMaskCache()
    {
        return new RenderCache<MaskKey, Tile>(
            _core.Options.TileCacheBytes / 2,
            t => t.Dispose()
        );
    }

    private void Ui()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            throw new InvalidOperationException("只能在控件所属UI线程调用；生产者线程请使用PostFrame。");
        }

        if (IsDisposed)
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

    /// <summary>单独覆盖一个图层的可见性，不重建其他图层。</summary>
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

    /// <summary>基于精确源几何的命中测试，不受LOD影响。</summary>
    /// <param name = "client">控件客户区坐标，单位为屏幕像素，不是原图坐标。</param>
    public Visual? HitTest(Point client)
    {
        return _core.HitTest(new PointD(client.X, client.Y));
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
            throw new ArgumentNullException(nameof(graphics), "绘图上下文不能为空。");
        }

        var frame = _core.Frame;
        if (frame == null)
        {
            return;
        }

        var g = graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var requests = CanvasPlanning.Tiles(frame.Info, Viewport, Width, Height, Options.TileSize);
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

        if (_core.Editor != null)
        {
            foreach (var handle in _core.Editor.Handles(25 / Viewport.Scale))
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
        var key = (request.Level, request.X, request.Y);
        _tiles.TryGet(key, out var entry);
        if (entry != null && entry.Revision == _core.Revision)
        {
            return entry;
        }

        using var source = _core.Frame!.ReadTile(request.Level, request.X, request.Y, Options.TileSize);
        var pixels = DisplayPixels.From(source, Options);
        var format = PixelFormatFor(pixels.Layout);
        if (entry == null)
        {
            entry = new Tile
            {
                Bitmap = CreateBitmap(pixels.Width, pixels.Height, format),
                Revision = _core.Revision,
            };
            try
            {
                Copy(entry.Bitmap, pixels.Bytes, pixels.Stride);
                if (!_tiles.Add(key, entry, (long)Options.TileSize * Options.TileSize * 4))
                {
                    throw new InvalidOperationException("图块超出缓存预算。");
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
                throw new InvalidOperationException("Region掩码超出缓存预算。");
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
        return _core.GetPath(geometry, Viewport.Scale, BuildPath);
    }

    private GraphicsPath BuildPath(DP.Vision.Geometry geometry, double tolerance)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        try
        {
            if (geometry is ContourGeometry contour)
            {
                var points = ContourLod
                    .Simplify(contour, tolerance, _core.LodChecks)
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
                throw new NotSupportedException("画布不支持绘制此类几何。");
            }

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

    private static ECanvasButton? ButtonOf(MouseButtons button)
    {
        return button switch
        {
            MouseButtons.Left => ECanvasButton.Left,
            MouseButtons.Middle => ECanvasButton.Middle,
            MouseButtons.Right => ECanvasButton.Right,
            _ => null,
        };
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _core.Wheel(e.Delta, new PointD(e.X, e.Y));
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var button = ButtonOf(e.Button);
        if (button != null && _core.MouseDown(button.Value, new PointD(e.X, e.Y), e.Clicks).Capture)
        {
            Capture = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _core.MouseMove(new PointD(e.X, e.Y));
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var button = ButtonOf(e.Button);
        if (button != null)
        {
            var drag = SystemInformation.DragSize;
            _core.MouseUp(button.Value, new PointD(e.X, e.Y), drag.Width, drag.Height);
        }

        Capture = false;
    }

    /// <inheritdoc/>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            _core.CaptureLost();
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
        var key = e.KeyCode switch
        {
            Keys.Home => ECanvasKey.Home,
            Keys.Z => ECanvasKey.Z,
            Keys.Y => ECanvasKey.Y,
            Keys.Delete => ECanvasKey.Delete,
            Keys.Escape => ECanvasKey.Escape,
            Keys.Enter => ECanvasKey.Enter,
            Keys.Back => ECanvasKey.Backspace,
            _ => ECanvasKey.Other,
        };
        if (_core.KeyDown(key, e.Control, e.Shift))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 基类构造期间也可能触发尺寸变化，此时_core尚未创建。
        if (_core != null && !IsDisposed)
        {
            _core.Resized();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _core.Dispose();
            _tiles.Dispose();
            _masks.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class Tile : IDisposable
    {
        internal Bitmap Bitmap = null!;
        internal long Revision = -1;

        public void Dispose()
        {
            Bitmap.Dispose();
        }
    }
}
