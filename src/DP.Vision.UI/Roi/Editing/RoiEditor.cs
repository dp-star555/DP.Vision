using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>只在UI线程使用、与厂商无关的ROI编辑器；一次拖动对应一次撤销事务，编辑预览与检测证据分离。</summary>
public sealed class RoiEditor
{
    // 选中/预览/草稿的显示颜色，只影响编辑层外观。
    private const uint SelectedColor = VisionColors.Cyan;
    private const uint DisabledColor = 0xFF888888;
    private const uint ExcludeColor = 0xFFFFAA33;
    private const uint IncludeColor = 0xFF3399FF;
    private const uint DraftColor = VisionColors.Yellow;

    private static void ValidateTolerance(double value, string parameter)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 10000000)
        {
            throw new ArgumentOutOfRangeException(parameter, "容差必须是0～10000000之间的有限值。");
        }
    }

    private readonly List<RoiDocument> _undo = new List<RoiDocument>(),
        _redo = new List<RoiDocument>();
    private readonly int _historyLimit;
    private ERoiTool _tool;
    private PointD? _start;
    private RoiDefinition? _original;
    private RoiHandle? _handle;
    private Geometry? _preview;
    private readonly List<PointD> _vertices = new List<PointD>();
    private PointD? _cursor;
    private string? _selected;
    private int _nextId;
    private bool _changedGesture;

    /// <summary>创建具有有界撤销历史的编辑器。</summary>
    /// <param name = "historyLimit">最大撤销步数，范围1–100，默认100。</param>
    public RoiEditor(int historyLimit = 100)
    {
        if (historyLimit < 1 || historyLimit > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(historyLimit), "撤销步数必须在1～100之间。");
        }

        _historyLimit = historyLimit;
    }

    /// <summary>已提交文档；Load会整体替换此状态并清空历史。</summary>
    public RoiDocument Document { get; private set; } = new RoiDocument(Array.Empty<RoiDefinition>());

    /// <summary>当前工具；切换工具会取消未提交的手势。</summary>
    public ERoiTool Tool
    {
        get => _tool;
        set
        {
            if (!Enum.IsDefined(typeof(ERoiTool), value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "未定义的ROI工具。");
            }

            Cancel();
            _tool = value;
            Notify();
        }
    }

    /// <summary>选中ROI的稳定配置标识，不是检测证据编号。</summary>
    public string? SelectedId => _selected;

    /// <summary>是否存在尚未提交的绘制或编辑手势。</summary>
    public bool IsEditing => _start.HasValue || _vertices.Count > 0;

    /// <summary>最近一次编辑被拒绝的原因；拒绝不会修改已提交文档。</summary>
    public string? ValidationError { get; private set; }

    /// <summary>是否存在可撤销事务。</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>是否存在可重做事务。</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>显示更新事件；选中状态或预览变化也可触发，不代表文档已提交。</summary>
    public event EventHandler? Changed;

    /// <summary>只在配置提交时触发，单纯指针移动不触发。</summary>
    public event EventHandler<RoiDocumentChangedEventArgs>? DocumentChanged;

    private void Notify()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>原子加载经过校验的快照，取消预览并清空撤销/重做历史。</summary>
    /// <param name = "document">待加载的不可变ROI文档。</param>
    public void Load(RoiDocument document)
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document), "ROI文档不能为空。");
        }

        var before = Document;
        Cancel();
        Document = document;
        _selected = null;
        _undo.Clear();
        _redo.Clear();
        Publish(before, "load");
    }

    /// <summary>选中已有ROI；禁用或空几何配置也可以选中。</summary>
    /// <param name = "id">已有ROI标识；null表示取消选择，未知标识会报错。</param>
    public void Select(string? id)
    {
        if (id != null && !Document.Rois.Any(r => r.Id == id))
        {
            throw new ArgumentException("ROI文档中不存在此标识。", nameof(id));
        }

        Cancel();
        _selected = id;
        Notify();
    }

    private RoiDefinition? Selected => Document.Rois.FirstOrDefault(r => r.Id == _selected);

    private void Publish(RoiDocument before, string operation)
    {
        Notify();
        DocumentChanged?.Invoke(this, new RoiDocumentChangedEventArgs(before, Document, operation));
    }

    private void Commit(RoiDocument next, string operation)
    {
        var before = Document;
        _undo.Add(before);
        if (_undo.Count > _historyLimit)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        Document = next;
        ResetGesture();
        Publish(before, operation);
    }

    private void Replace(RoiDefinition value, string operation)
    {
        Commit(new RoiDocument(Document.Rois.Select(r => r.Id == value.Id ? value : r)), operation);
    }

    /// <summary>修改选中ROI的包含/排除及启用状态，不修改原始几何。</summary>
    /// <param name = "purpose">包含或排除意图，由宿主映射到后台业务。</param>
    /// <param name = "enabled">是否启用此ROI配置。</param>
    public void SetSelectedMetadata(ERoiPurpose purpose, bool enabled)
    {
        var selected = Selected;
        if (selected == null)
        {
            return;
        }

        Cancel();
        if (selected.Purpose == purpose && selected.Enabled == enabled)
        {
            return;
        }

        Replace(
            new RoiDefinition(selected.Id, selected.Shape, purpose, enabled, selected.Constraint),
            "metadata"
        );
    }

    /// <summary>只删除选中的ROI配置，检测证据不属于此文档。</summary>
    public void DeleteSelected()
    {
        var id = _selected;
        Cancel();
        if (id == null)
        {
            return;
        }

        _selected = null;
        Commit(new RoiDocument(Document.Rois.Where(r => r.Id != id)), "delete");
    }

    /// <summary>在选中轮廓最近边上插入投影顶点，成功时形成一个撤销事务；不会选择检测证据。</summary>
    /// <param name = "point">鼠标或宿主输入的原图坐标。</param>
    /// <param name = "tolerance">允许命中的最大距离，单位为原图像素；屏幕距离应先除以缩放比例。</param>
    /// <returns>是否成功插入；未命中或超出顶点预算时返回false。</returns>
    public bool InsertVertex(PointD point, double tolerance)
    {
        return EditVertex(point, tolerance, true);
    }

    /// <summary>删除选中轮廓最近的顶点；开放折线至少保留2点，闭合轮廓至少保留3点。</summary>
    /// <param name = "point">待命中位置的原图坐标。</param>
    /// <param name = "tolerance">非负命中距离，单位为原图像素。</param>
    /// <returns>是否成功删除并提交事务。</returns>
    public bool DeleteVertex(PointD point, double tolerance)
    {
        return EditVertex(point, tolerance, false);
    }

    private bool EditVertex(PointD point, double tolerance, bool insert)
    {
        ValidateTolerance(tolerance, nameof(tolerance));
        Cancel();
        var selected = Selected;
        if (!(selected?.Shape is ContourGeometry contour))
        {
            return false;
        }

        var points = contour.Points.ToList();
        bool repeated = contour.RepeatsFirstPoint;
        if (repeated)
        {
            points.RemoveAt(points.Count - 1);
        }

        int index = -1;
        double best = tolerance * tolerance;
        PointD projected = point;
        if (insert)
        {
            int edges = contour.Closed ? points.Count : points.Count - 1;
            for (int i = 0; i < edges; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                double length = DistanceSquared(a, b);
                if (length == 0)
                {
                    continue;
                }

                double t = Math.Max(
                    0,
                    Math.Min(1, ((point.X - a.X) * (b.X - a.X) + (point.Y - a.Y) * (b.Y - a.Y)) / length)
                );
                var candidate =
                    t == 0 ? a
                    : t == 1 ? b
                    : new PointD(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                double distance = DistanceSquared(point, candidate);
                if (distance <= best && (index < 0 || distance < best))
                {
                    best = distance;
                    index = i;
                    projected = candidate;
                }
            }

            if (
                index < 0
                || Same(projected, points[index])
                || Same(projected, points[(index + 1) % points.Count])
            )
            {
                return false;
            }

            points.Insert(index + 1, projected);
        }
        else
        {
            for (int i = 0; i < points.Count; i++)
            {
                double distance = DistanceSquared(point, points[i]);
                if (distance <= best && (index < 0 || distance < best))
                {
                    best = distance;
                    index = i;
                }
            }

            if (index < 0)
            {
                return false;
            }

            if (points.Count <= (contour.Closed ? 3 : 2))
            {
                ValidationError = "顶点数已达下限，不能继续删除（开放折线至少2点，闭合轮廓至少3点）。";
                Notify();
                return false;
            }

            points.RemoveAt(index);
        }

        if (repeated)
        {
            points.Add(points[0]);
        }

        RoiDocument next;
        try
        {
            var replacement = new RoiDefinition(
                selected!.Id,
                new ContourGeometry(points, contour.Closed, contour.Filled),
                selected.Purpose,
                selected.Enabled,
                selected.Constraint
            );
            next = new RoiDocument(Document.Rois.Select(r => r.Id == selected.Id ? replacement : r));
        }
        catch (ArgumentException error)
        {
            ValidationError = error.Message;
            Notify();
            return false;
        }

        Commit(next, insert ? "insert-vertex" : "delete-vertex");
        return true;
    }

    /// <summary>取消未提交预览，不写入历史，也不触发文档提交事件。</summary>
    public void Cancel()
    {
        ResetGesture();
        ValidationError = null;
        Notify();
    }

    /// <summary>
    /// 视口平移或缩放前调用：只取消进行中的拖动手势（移动、缩放、旋转或拖框创建）。
    /// 逐点绘制中的多边形/折线顶点使用原图坐标，不受视口变化影响，予以保留。
    /// </summary>
    public void CancelDrag()
    {
        if (_start.HasValue)
        {
            Cancel();
        }
    }

    private void ResetGesture()
    {
        _start = null;
        _original = null;
        _handle = null;
        _preview = null;
        _vertices.Clear();
        _cursor = null;
        _changedGesture = false;
    }

    /// <summary>撤销一次已提交事务。</summary>
    public void Undo()
    {
        Step(_undo, _redo, "undo");
    }

    /// <summary>重做一次被撤销的事务。</summary>
    public void Redo()
    {
        Step(_redo, _undo, "redo");
    }

    // 从from栈顶取出文档替换当前文档，当前文档压入to栈；撤销与重做互为镜像。
    private void Step(List<RoiDocument> from, List<RoiDocument> to, string operation)
    {
        Cancel();
        if (from.Count == 0)
        {
            return;
        }

        var before = Document;
        to.Add(before);
        Document = from[from.Count - 1];
        from.RemoveAt(from.Count - 1);
        if (!Document.Rois.Any(r => r.Id == _selected))
        {
            _selected = null;
        }

        Publish(before, operation);
    }

    /// <summary>开始或延续一次编辑手势；旋转控制点位于形状外5倍容差处。</summary>
    /// <param name = "point">按下位置的原图坐标，不是控件坐标。</param>
    /// <param name = "tolerance">命中容差，单位为原图像素。</param>
    /// <returns>本次输入是否被编辑器处理。</returns>
    public bool PointerDown(PointD point, double tolerance)
    {
        ValidateTolerance(tolerance, nameof(tolerance));
        if (Tool == ERoiTool.InsertVertex)
        {
            return InsertVertex(point, tolerance);
        }

        if (Tool == ERoiTool.DeleteVertex)
        {
            return DeleteVertex(point, tolerance);
        }

        if (Tool == ERoiTool.Polygon || Tool == ERoiTool.Polyline)
        {
            if (_vertices.Count >= 4096)
            {
                return false;
            }

            if (_vertices.Count == 0 || !Same(_vertices[_vertices.Count - 1], point))
            {
                _vertices.Add(point);
            }

            _cursor = point;
            Notify();
            return true;
        }

        if (Tool == ERoiTool.Point)
        {
            return Create(new ContourGeometry(new[] { point }), ERoiConstraint.None);
        }

        if (Tool != ERoiTool.Select)
        {
            ResetGesture();
            _start = point;
            Notify();
            return true;
        }

        Cancel();
        var selected = Selected;
        if (selected != null)
        {
            var handles = Handles(tolerance * 5);
            foreach (var handle in handles)
            {
                if (DistanceSquared(point, handle.Position) <= tolerance * tolerance)
                {
                    _start = point;
                    _original = selected;
                    _handle = handle;
                    Notify();
                    return true;
                }
            }
        }

        var hit = Document.Rois.Reverse().FirstOrDefault(r => r.Shape.Contains(point, tolerance));
        _selected = hit?.Id;
        if (hit != null)
        {
            _start = point;
            _original = hit;
        }

        Notify();
        return hit != null;
    }

    /// <summary>更新拖动预览，不原地修改输入几何，也不提前提交文档。</summary>
    /// <param name = "point">移动到的原图坐标。</param>
    public void PointerMove(PointD point)
    {
        if (_vertices.Count > 0)
        {
            _cursor = point;
            Notify();
            return;
        }

        if (!_start.HasValue)
        {
            return;
        }

        try
        {
            if (_original != null)
            {
                _preview = _handle.HasValue
                    ? ChangeHandle(_original, _handle.Value, point)
                    : _original.Shape.Translate(point.X - _start.Value.X, point.Y - _start.Value.Y);
                _changedGesture = !Same(_start.Value, point);
            }
            else
            {
                _preview = Creation(_start.Value, point);
            }

            ValidationError = null;
        }
        catch (ArgumentException error)
        {
            _preview = null;
            _changedGesture = false;
            ValidationError = error.Message;
        }
        catch (OverflowException error)
        {
            _preview = null;
            _changedGesture = false;
            ValidationError = error.Message;
        }

        Notify();
    }

    /// <summary>结束并提交一次拖动；没有位移的单击或空形状不产生历史。</summary>
    /// <param name = "point">释放位置的原图坐标。</param>
    public void PointerUp(PointD point)
    {
        if (!_start.HasValue)
        {
            return;
        }

        PointerMove(point);
        var preview = _preview;
        var original = _original;
        bool changed = _changedGesture;
        if (original != null && preview != null && changed)
        {
            Replace(
                new RoiDefinition(
                    original.Id,
                    preview,
                    original.Purpose,
                    original.Enabled,
                    original.Constraint
                ),
                "edit"
            );
            return;
        }

        if (original == null && preview != null)
        {
            var constraint =
                Tool == ERoiTool.Circle ? ERoiConstraint.Circle
                : Tool == ERoiTool.Rectangle ? ERoiConstraint.AxisAligned
                : ERoiConstraint.None;
            Create(preview, constraint);
            return;
        }

        ResetGesture();
        Notify();
    }

    /// <summary>结束多次单击创建的多边形/折线，供Enter或双击调用；顶点不足时继续保留预览。</summary>
    /// <returns>是否成功结束并提交配置。</returns>
    public bool Finish()
    {
        if (_vertices.Count == 0)
        {
            return false;
        }

        var points = _vertices.ToList();
        if (Tool == ERoiTool.Polygon && points.Count > 1 && Same(points[0], points[points.Count - 1]))
        {
            points.RemoveAt(points.Count - 1);
        }

        int minimum = Tool == ERoiTool.Polygon ? 3 : 2;
        if (points.Count < minimum)
        {
            return false;
        }

        return Create(
            new ContourGeometry(points, Tool == ERoiTool.Polygon, Tool == ERoiTool.Polygon),
            ERoiConstraint.None
        );
    }

    /// <summary>删除最后一个待提交顶点，不修改已提交文档。</summary>
    public void Backspace()
    {
        if (_vertices.Count > 0)
        {
            _vertices.RemoveAt(_vertices.Count - 1);
            Notify();
        }
    }

    private bool Create(Geometry shape, ERoiConstraint constraint)
    {
        string id;
        do
        {
            id = "ROI-" + (++_nextId);
        } while (Document.Rois.Any(r => r.Id == id));
        RoiDocument next;
        try
        {
            var roi = new RoiDefinition(id, shape, ERoiPurpose.Include, true, constraint);
            next = new RoiDocument(Document.Rois.Concat(new[] { roi }));
        }
        catch (ArgumentException error)
        {
            ResetGesture();
            ValidationError = error.Message;
            Notify();
            return false;
        }

        ValidationError = null;
        _selected = id;
        Commit(next, "create");
        _tool = ERoiTool.Select;
        Notify();
        return true;
    }

    private Geometry? Creation(PointD a, PointD b)
    {
        double width = Math.Abs(b.X - a.X),
            height = Math.Abs(b.Y - a.Y);
        if (Tool == ERoiTool.Circle)
        {
            double radius = Math.Sqrt(DistanceSquared(a, b));
            return radius < .01 ? null : new EllipseGeometry(a, radius, radius);
        }

        if (width < .01 || height < .01)
        {
            return null;
        }

        var center = new PointD((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        return Tool == ERoiTool.Ellipse
            ? (Geometry)new EllipseGeometry(center, width / 2, height / 2)
            : new RectangleGeometry(center, width, height);
    }

    /// <summary>生成仅用于显示的ROI图层，可包含临时预览；不能作为后台已确认输入。</summary>
    /// <returns>本次显示所需的只读图层快照。</returns>
    public CanvasLayer DisplayLayer()
    {
        var visuals = Document
            .Rois.Select(r => new Visual(
                r.Id,
                r.Id == _original?.Id && _preview != null ? _preview : r.Shape,
                r.Id == _selected ? SelectedColor
                    : !r.Enabled ? DisabledColor
                    : r.Purpose == ERoiPurpose.Exclude ? ExcludeColor
                    : IncludeColor,
                r.Id
            ))
            .ToList();
        if (_original == null && _preview != null)
        {
            visuals.Add(new Visual("draft", _preview, DraftColor));
        }

        if (_vertices.Count > 0)
        {
            var points = _vertices.ToList();
            if (_cursor.HasValue && !Same(points[points.Count - 1], _cursor.Value))
            {
                points.Add(_cursor.Value);
            }

            visuals.Add(new Visual("draft", new ContourGeometry(points), DraftColor));
        }

        return new CanvasLayer("__roi_editor", ELayerKind.Interaction, visuals, int.MaxValue);
    }

    /// <summary>获取选中几何的编辑控制点，位置使用原图坐标。</summary>
    /// <param name = "spacing">控制点间距，单位为原图像素，通常使用25除以屏幕缩放比例。</param>
    /// <returns>当前选中几何的控制点集合；没有选择时为空。</returns>
    public IReadOnlyList<RoiHandle> Handles(double spacing)
    {
        ValidateTolerance(spacing, nameof(spacing));
        var selected = Selected;
        if (selected == null)
        {
            return Array.Empty<RoiHandle>();
        }

        var shape = _original != null && _preview != null ? _preview : selected.Shape;
        if (shape is ContourGeometry contour)
        {
            return Array.AsReadOnly(
                contour.Points.Select((p, i) => new RoiHandle(p, ERoiHandleKind.Vertex, i)).ToArray()
            );
        }

        if (!TryFrame(shape, out var center, out double width, out double height, out double angle))
        {
            return Array.Empty<RoiHandle>();
        }

        if (shape is EllipseGeometry && selected.Constraint == ERoiConstraint.Circle)
        {
            return new[]
            {
                new RoiHandle(new PointD(center.X + width / 2, center.Y), ERoiHandleKind.Radius, 0),
            };
        }

        var offsets = new[]
        {
            new PointD(-width / 2, -height / 2),
            new PointD(0, -height / 2),
            new PointD(width / 2, -height / 2),
            new PointD(width / 2, 0),
            new PointD(width / 2, height / 2),
            new PointD(0, height / 2),
            new PointD(-width / 2, height / 2),
            new PointD(-width / 2, 0),
        };
        var handles = offsets
            .Select((p, i) => new RoiHandle(World(center, angle, p.X, p.Y), ERoiHandleKind.Size, i))
            .ToList();
        if (selected.Constraint != ERoiConstraint.AxisAligned)
        {
            handles.Add(
                new RoiHandle(World(center, angle, 0, -height / 2 - spacing), ERoiHandleKind.Rotation, 8)
            );
        }

        return handles;
    }

    private static Geometry ChangeHandle(RoiDefinition roi, RoiHandle handle, PointD point)
    {
        if (roi.Shape is ContourGeometry contour)
        {
            var points = contour.Points.ToArray();
            points[handle.Index] = point;
            // 显式重复首点的闭合轮廓：首尾是同一个顶点，拖动任一端都同步移动另一端，保持闭合表示。
            if (contour.RepeatsFirstPoint)
            {
                int last = points.Length - 1;
                if (handle.Index == 0)
                {
                    points[last] = point;
                }
                else if (handle.Index == last)
                {
                    points[0] = point;
                }
            }

            return new ContourGeometry(points, contour.Closed, contour.Filled);
        }

        if (!TryFrame(roi.Shape, out var center, out double width, out double height, out double angle))
        {
            return roi.Shape;
        }

        if (handle.Kind == ERoiHandleKind.Radius)
        {
            double radius = Math.Max(.01, Math.Sqrt(DistanceSquared(center, point)));
            return new EllipseGeometry(center, radius, radius);
        }

        if (handle.Kind == ERoiHandleKind.Rotation)
        {
            if (DistanceSquared(center, point) < 1e-12)
            {
                return roi.Shape;
            }

            angle = Math.Atan2(point.Y - center.Y, point.X - center.X) + Math.PI / 2;
        }
        else
        {
            double dx = point.X - center.X,
                dy = point.Y - center.Y,
                x = dx * Math.Cos(angle) + dy * Math.Sin(angle),
                y = -dx * Math.Sin(angle) + dy * Math.Cos(angle),
                left = -width / 2,
                right = width / 2,
                top = -height / 2,
                bottom = height / 2;
            int i = handle.Index;
            if (i == 0 || i == 6 || i == 7)
            {
                left = Math.Min(x, right - .01);
            }

            if (i == 2 || i == 3 || i == 4)
            {
                right = Math.Max(x, left + .01);
            }

            if (i == 0 || i == 1 || i == 2)
            {
                top = Math.Min(y, bottom - .01);
            }

            if (i == 4 || i == 5 || i == 6)
            {
                bottom = Math.Max(y, top + .01);
            }

            center = World(center, angle, (left + right) / 2, (top + bottom) / 2);
            width = right - left;
            height = bottom - top;
        }

        return roi.Shape is RectangleGeometry
            ? (Geometry)new RectangleGeometry(center, width, height, angle)
            : new EllipseGeometry(center, width / 2, height / 2, angle);
    }

    // 把矩形/椭圆统一成中心、宽、高（椭圆为直径）与角度，供控制点计算和拖动共用。
    private static bool TryFrame(
        Geometry shape,
        out PointD center,
        out double width,
        out double height,
        out double angle
    )
    {
        if (shape is RectangleGeometry rectangle)
        {
            center = rectangle.Center;
            width = rectangle.Width;
            height = rectangle.Height;
            angle = rectangle.Angle;
            return true;
        }

        if (shape is EllipseGeometry ellipse)
        {
            center = ellipse.Center;
            width = ellipse.RadiusX * 2;
            height = ellipse.RadiusY * 2;
            angle = ellipse.Angle;
            return true;
        }

        center = default;
        width = height = angle = 0;
        return false;
    }

    private static PointD World(PointD center, double angle, double x, double y)
    {
        return new PointD(
            center.X + x * Math.Cos(angle) - y * Math.Sin(angle),
            center.Y + x * Math.Sin(angle) + y * Math.Cos(angle)
        );
    }

    private static bool Same(PointD a, PointD b)
    {
        return DistanceSquared(a, b) < 1e-20;
    }

    private static double DistanceSquared(PointD a, PointD b)
    {
        double x = a.X - b.X,
            y = a.Y - b.Y;
        return x * x + y * y;
    }
}
