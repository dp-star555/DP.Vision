using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>画布输入按键，与具体UI框架无关。</summary>
internal enum ECanvasButton
{
    Left,
    Middle,
    Right,
}

/// <summary>画布关心的键盘键，其余键为Other。</summary>
internal enum ECanvasKey
{
    Other,
    Home,
    Z,
    Y,
    Delete,
    Escape,
    Enter,
    Backspace,
}

/// <summary>
/// WinForms与WPF画布共用的非绘制逻辑：帧生命周期、ROI编辑器接线、图层显隐、路径与Region标识缓存、
/// 命中测试以及鼠标/键盘到编辑器与视口的映射。只在UI线程使用；线程与释放检查由画布负责。
/// </summary>
/// <typeparam name="TPath">画布框架的矢量路径类型。</typeparam>
internal sealed class CanvasCore<TPath> : IDisposable
    where TPath : class
{
    private readonly Action _invalidate;
    private readonly Action _releaseCapture;
    private readonly Func<(double Width, double Height)> _clientSize;
    private readonly Action<TPath> _disposePath;
    private readonly LatestFrameMailbox _mailbox = new LatestFrameMailbox();
    private readonly Dictionary<Geometry, (TPath Path, double Tolerance)> _paths =
        new Dictionary<Geometry, (TPath Path, double Tolerance)>();
    private readonly Dictionary<Geometry, long> _regionIds = new Dictionary<Geometry, long>();
    private readonly Dictionary<string, bool> _visibility = new Dictionary<string, bool>();
    private HashSet<Geometry> _overlayGeometry = new HashSet<Geometry>();
    private HashSet<Geometry> _editableGeometry = new HashSet<Geometry>();
    private RoiEditor? _editor;
    private CanvasLayer? _editLayer;
    private long _nextRegionId;
    private PointD? _pan;
    private PointD? _rightDown;
    private bool _roiDrag;

    /// <param name="invalidate">请求重绘。</param>
    /// <param name="releaseCapture">释放鼠标捕获。</param>
    /// <param name="clientSize">当前客户区尺寸，单位为控件像素或DIP。</param>
    /// <param name="disposePath">释放被替换或清除的路径；不需要释放时传空操作。</param>
    internal CanvasCore(
        Action invalidate,
        Action releaseCapture,
        Func<(double Width, double Height)> clientSize,
        Action<TPath> disposePath
    )
    {
        _invalidate = invalidate;
        _releaseCapture = releaseCapture;
        _clientSize = clientSize;
        _disposePath = disposePath;
    }

    internal CanvasFrame? Frame { get; private set; }

    /// <summary>像素内容版本；帧身份、布局或尺寸变化时递增，图块缓存据此失效。</summary>
    internal long Revision { get; private set; }

    /// <summary>每条轮廓可用的LOD距离计算次数，按当前帧轮廓数均分总预算。</summary>
    internal int LodChecks { get; private set; } = ContourLod.DefaultMaximumDistanceChecks;

    internal CanvasOptions Options { get; set; } = new CanvasOptions();

    internal CanvasViewport Viewport { get; } = new CanvasViewport();

    /// <summary>视口是否处于适应窗口状态；缩放或平移后为false，尺寸变化时不再自动适配。</summary>
    internal bool FitMode { get; private set; } = true;

    internal RoiEditor? Editor
    {
        get => _editor;
        set
        {
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

    /// <summary>按绘制顺序排列的帧叠加图层，最后是ROI编辑层。</summary>
    internal IEnumerable<CanvasLayer> ActiveLayers()
    {
        var layers = Frame?.Overlay?.Layers ?? (IReadOnlyList<CanvasLayer>)Array.Empty<CanvasLayer>();
        return _editLayer == null ? layers : layers.Concat(new[] { _editLayer });
    }

    internal bool IsLayerVisible(CanvasLayer layer)
    {
        return _visibility.TryGetValue(layer.Id, out bool visible) ? visible : layer.Visible;
    }

    internal void SetLayerVisible(string id, bool visible)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("图层标识不能为空白。", nameof(id));
        }

        _visibility[id] = visible;
        _invalidate();
    }

    internal bool PostFrame(CanvasFrame frame)
    {
        return _mailbox.Post(frame);
    }

    /// <summary>定时器回调：编辑手势进行中冻结预览，否则显示邮箱中最新的帧。</summary>
    internal void Tick()
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

    internal void Present(CanvasFrame frame)
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame), "预览包不能为空。");
        }

        if (Frame != null && frame.Sequence <= Frame.Sequence)
        {
            return;
        }

        var next = frame.Retain();
        var old = Frame;
        bool fit = old == null || old.Info.Width != next.Info.Width || old.Info.Height != next.Info.Height;
        if (old != null && old.FrameId != next.FrameId)
        {
            // 换成另一张图像时，未提交的编辑不再对应当前图像。
            _editor?.Cancel();
        }

        Frame = next;
        if (old == null || old.FrameId != next.FrameId || old.Info.Layout != next.Info.Layout || fit)
        {
            Revision++;
        }

        _mailbox.AdvanceTo(next.Sequence);
        old?.Dispose();
        int contours =
            next.Overlay?.Layers.Sum(l => l.Visuals.Count(v => v.Geometry is ContourGeometry)) ?? 0;
        LodChecks = Math.Max(1, ContourLod.DefaultMaximumDistanceChecks / Math.Max(1, contours));
        RebuildOverlayGeometry();
        if (fit)
        {
            FitToWindow();
        }
        else
        {
            _invalidate();
        }
    }

    /// <summary>清空当前图像及排队的预览；画布随后自行清空像素缓存。</summary>
    internal void ClearImage()
    {
        _editor?.Cancel();
        using var pending = _mailbox.Take();
        var old = Frame;
        Frame = null;
        old?.Dispose();
        Revision++;
        RebuildOverlayGeometry();
        _invalidate();
    }

    /// <summary>适配窗口；只取消进行中的拖动，逐点绘制的多边形顶点保留。</summary>
    internal void FitToWindow()
    {
        _editor?.CancelDrag();
        FitMode = true;
        if (Frame != null)
        {
            var size = _clientSize();
            Viewport.Fit(Frame.Info.Width, Frame.Info.Height, size.Width, size.Height);
        }

        _invalidate();
    }

    /// <summary>控件尺寸变化：仍处于适应窗口状态时重新适配。</summary>
    internal void Resized()
    {
        if (FitMode && Frame != null)
        {
            FitToWindow();
        }
    }

    /// <summary>基于精确源几何的命中测试，不受LOD影响，不包含ROI编辑层。</summary>
    internal Visual? HitTest(PointD client)
    {
        if (Frame?.Overlay == null)
        {
            return null;
        }

        var p = Viewport.ToImage(client);
        return Frame
            .Overlay.Layers.Reverse()
            .Where(IsLayerVisible)
            .SelectMany(l => l.Visuals.Reverse())
            .FirstOrDefault(v => v.Geometry.Contains(p, 3 / Viewport.Scale));
    }

    internal void ProcessRoiPointer(ERoiPointerAction action, PointD client)
    {
        if (!Enum.IsDefined(typeof(ERoiPointerAction), action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), "未定义的指针动作。");
        }

        if (Frame == null || _editor == null)
        {
            return;
        }

        var point = Viewport.ToImage(client);
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

    /// <summary>缓存几何的矢量路径；可编辑几何与非轮廓几何不做LOD简化。</summary>
    /// <param name="geometry">原始几何。</param>
    /// <param name="scale">每原图像素对应的设备像素数，用于计算LOD容差。</param>
    /// <param name="build">按几何和容差创建新路径。</param>
    internal TPath GetPath(Geometry geometry, double scale, Func<Geometry, double, TPath> build)
    {
        double tolerance =
            geometry is ContourGeometry && !_editableGeometry.Contains(geometry)
                ? CanvasPlanning.LodTolerance(Options, scale)
                : 0;
        bool found = _paths.TryGetValue(geometry, out var cached);
        if (found && cached.Tolerance == tolerance)
        {
            return cached.Path;
        }

        var path = build(geometry, tolerance);
        if (found)
        {
            _disposePath(cached.Path);
        }

        _paths[geometry] = (path, tolerance);
        return path;
    }

    /// <summary>Region掩码缓存键：Region标识、颜色、原图尺寸与图块位置。</summary>
    internal (long Region, uint Argb, int Width, int Height, int Level, int X, int Y) MaskKey(
        Visual visual,
        RegionGeometry region,
        TileRequest request
    )
    {
        if (!_regionIds.TryGetValue(region, out long id))
        {
            id = ++_nextRegionId;
            _regionIds.Add(region, id);
        }

        return (id, visual.Argb, Frame!.Info.Width, Frame.Info.Height, request.Level, request.X, request.Y);
    }

    internal void ClearPaths()
    {
        foreach (var item in _paths.Values)
        {
            _disposePath(item.Path);
        }

        _paths.Clear();
    }

    /// <summary>鼠标按下。</summary>
    /// <returns>Capture：画布应捕获鼠标；Handled：事件已由画布处理。</returns>
    internal (bool Capture, bool Handled) MouseDown(ECanvasButton button, PointD client, int clicks)
    {
        if (button == ECanvasButton.Middle || button == ECanvasButton.Right)
        {
            // 视口平移只取消拖动，逐点绘制的多边形顶点保留。
            _editor?.CancelDrag();
            FitMode = false;
            _pan = client;
            _rightDown = button == ECanvasButton.Right ? client : (PointD?)null;
            return (true, false);
        }

        if (Frame == null || _editor == null)
        {
            return (false, false);
        }

        bool drawing = _editor.Tool == ERoiTool.Polygon || _editor.Tool == ERoiTool.Polyline;
        if (clicks > 1 && drawing)
        {
            _editor.Finish();
            return (false, true);
        }

        ProcessRoiPointer(ERoiPointerAction.Down, client);
        _roiDrag = _editor.IsEditing && !drawing;
        return (_roiDrag, true);
    }

    internal void MouseMove(PointD client)
    {
        if (_pan.HasValue)
        {
            Viewport.Pan(client.X - _pan.Value.X, client.Y - _pan.Value.Y);
            _pan = client;
            _invalidate();
        }
        else if (Frame != null)
        {
            ProcessRoiPointer(ERoiPointerAction.Move, client);
        }
    }

    /// <summary>鼠标释放；画布随后释放捕获。</summary>
    /// <param name="button">释放的按键。</param>
    /// <param name="client">释放位置。</param>
    /// <param name="dragWidth">系统横向拖动阈值。</param>
    /// <param name="dragHeight">系统纵向拖动阈值。</param>
    /// <returns>事件是否已由画布处理（右键单击结束多边形）。</returns>
    internal bool MouseUp(ECanvasButton button, PointD client, double dragWidth, double dragHeight)
    {
        bool handled = false;
        if (button == ECanvasButton.Left && _roiDrag)
        {
            _roiDrag = false;
            ProcessRoiPointer(ERoiPointerAction.Up, client);
        }

        // 右键单击（未拖动平移）结束正在逐点绘制的多边形/折线，闭合到首点；没有待定顶点时Finish不做任何事。
        if (button == ECanvasButton.Right && _rightDown.HasValue && _editor != null)
        {
            if (
                Math.Abs(client.X - _rightDown.Value.X) <= dragWidth
                && Math.Abs(client.Y - _rightDown.Value.Y) <= dragHeight
            )
            {
                _editor.Finish();
                handled = true;
            }
        }

        _rightDown = null;
        _pan = null;
        return handled;
    }

    /// <summary>鼠标捕获意外丢失：结束平移并取消未完成的拖动。</summary>
    internal void CaptureLost()
    {
        _pan = null;
        if (_roiDrag)
        {
            _roiDrag = false;
            _editor?.Cancel();
        }
    }

    internal void Wheel(double delta, PointD client)
    {
        _editor?.CancelDrag();
        FitMode = false;
        Viewport.Zoom(Math.Pow(1.2, delta / 120.0), client);
        _invalidate();
    }

    /// <summary>键盘命令：Home适应窗口；有编辑器时处理撤销、重做、删除、取消、结束与退格。</summary>
    /// <returns>是否已处理。</returns>
    internal bool KeyDown(ECanvasKey key, bool control, bool shift)
    {
        bool handled = false;
        if (key == ECanvasKey.Home)
        {
            FitToWindow();
            handled = true;
        }

        if (_editor == null)
        {
            return handled;
        }

        if (control && key == ECanvasKey.Z)
        {
            if (shift)
            {
                _editor.Redo();
            }
            else
            {
                _editor.Undo();
            }
        }
        else if (control && key == ECanvasKey.Y)
        {
            _editor.Redo();
        }
        else if (key == ECanvasKey.Delete)
        {
            _editor.DeleteSelected();
        }
        else if (key == ECanvasKey.Escape)
        {
            _editor.Cancel();
        }
        else if (key == ECanvasKey.Enter)
        {
            _editor.Finish();
        }
        else if (key == ECanvasKey.Backspace)
        {
            _editor.Backspace();
        }
        else
        {
            return handled;
        }

        return true;
    }

    private void EditorChanged(object? sender, EventArgs e)
    {
        var previous = _editableGeometry;
        _editLayer = _editor?.DisplayLayer();
        _editableGeometry = new HashSet<Geometry>(
            _editLayer?.Visuals.Select(v => v.Geometry) ?? Enumerable.Empty<Geometry>()
        );
        if (_roiDrag && _editor?.IsEditing != true)
        {
            _roiDrag = false;
            _releaseCapture();
        }

        // 指针移动时只有编辑层变化：只清理不再显示的旧编辑几何，不重扫整个叠加层。
        foreach (var geometry in previous)
        {
            if (!_editableGeometry.Contains(geometry) && !_overlayGeometry.Contains(geometry))
            {
                Forget(geometry);
            }
        }

        _invalidate();
    }

    // 帧叠加变化后重建叠加几何集合，并清理不再显示的路径与Region标识。
    private void RebuildOverlayGeometry()
    {
        _overlayGeometry = new HashSet<Geometry>(
            (Frame?.Overlay?.Layers ?? (IReadOnlyList<CanvasLayer>)Array.Empty<CanvasLayer>())
                .SelectMany(l => l.Visuals)
                .Select(v => v.Geometry)
        );
        var stale = _paths
            .Keys.Concat(_regionIds.Keys)
            .Where(g => !_overlayGeometry.Contains(g) && !_editableGeometry.Contains(g))
            .Distinct()
            .ToArray();
        foreach (var geometry in stale)
        {
            Forget(geometry);
        }
    }

    private void Forget(Geometry geometry)
    {
        if (_paths.TryGetValue(geometry, out var item))
        {
            _disposePath(item.Path);
            _paths.Remove(geometry);
        }

        _regionIds.Remove(geometry);
    }

    public void Dispose()
    {
        if (_editor != null)
        {
            _editor.Changed -= EditorChanged;
            _editor.Cancel();
            _editor = null;
        }

        _editLayer = null;
        _editableGeometry.Clear();
        _mailbox.Dispose();
        Frame?.Dispose();
        Frame = null;
        ClearPaths();
        _regionIds.Clear();
    }
}
