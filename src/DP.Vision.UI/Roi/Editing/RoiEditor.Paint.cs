using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>
/// 涂抹层：每种用途最多一个固定标识的Region ROI，与几何ROI放在同一文档里，按同一条规则合成——
/// 全部包含（几何+涂抹）的并集减去全部排除（几何+涂抹）的并集，排除始终优先，与先后顺序无关。
/// 画笔、橡皮、取反、互换和拍平只修改涂抹层，从不改动几何ROI，因此几何ROI仍可编辑、可跟随坐标系。
/// </summary>
public sealed partial class RoiEditor
{
    /// <summary>包含涂抹层的固定ROI标识。</summary>
    public const string PaintIncludeId = "涂抹-包含";

    /// <summary>排除涂抹层的固定ROI标识。</summary>
    public const string PaintExcludeId = "涂抹-排除";

    private static readonly RegionGeometry EmptyRegion = new RegionGeometry(Array.Empty<RegionRun>());
    private List<PointD>? _stroke;
    private RegionGeometry? _strokeRegion;
    private double _brushRadius = 10;
    private ERoiPurpose _paintPurpose = ERoiPurpose.Include;

    /// <summary>笔刷半径，单位为原图像素，0.5～1000，默认10。</summary>
    public double BrushRadius
    {
        get => _brushRadius;
        set
        {
            if (double.IsNaN(value) || value < .5 || value > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "笔刷半径必须在0.5～1000像素之间。");
            }

            _brushRadius = value;
            Notify();
        }
    }

    /// <summary>画笔写入的涂抹层：包含或排除。橡皮不受此设置影响，总是同时擦两层。</summary>
    public ERoiPurpose PaintPurpose
    {
        get => _paintPurpose;
        set
        {
            if (!Enum.IsDefined(typeof(ERoiPurpose), value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "未定义的涂抹用途。");
            }

            _paintPurpose = value;
            Notify();
        }
    }

    /// <summary>涂抹使用的原图宽度；0表示尚未设置，此时画笔、橡皮和取反都会被拒绝。</summary>
    public int PaintWidth { get; private set; }

    /// <summary>涂抹使用的原图高度；0表示尚未设置。</summary>
    public int PaintHeight { get; private set; }

    private bool IsPaintTool => Tool == ERoiTool.Brush || Tool == ERoiTool.Eraser;

    /// <summary>设置涂抹所对应的原图尺寸；画布显示图像时会自动设置。笔画超出原图的部分被裁去。</summary>
    /// <param name="width">原图宽度，单位为像素。</param>
    /// <param name="height">原图高度，单位为像素。</param>
    public void SetPaintArea(int width, int height)
    {
        if (width < 1 || height < 1 || width > ImageInfo.MaxDimension || height > ImageInfo.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "涂抹范围必须是有效的原图尺寸。");
        }

        PaintWidth = width;
        PaintHeight = height;
    }

    /// <summary>判断ROI标识是否为涂抹层。</summary>
    /// <param name="id">ROI标识。</param>
    /// <returns>是包含或排除涂抹层时为true。</returns>
    public static bool IsPaintId(string? id)
    {
        return id == PaintIncludeId || id == PaintExcludeId;
    }

    /// <summary>当前已提交的涂抹层像素；没有该层时为null。</summary>
    /// <param name="purpose">包含或排除涂抹层。</param>
    /// <returns>涂抹层Region快照。</returns>
    public RegionGeometry? PaintRegion(ERoiPurpose purpose)
    {
        return PaintRoi(Document, purpose)?.Shape as RegionGeometry;
    }

    /// <summary>在原图范围内对一个涂抹层取反（没有该层时视为空，取反后为全图）；形成一个撤销事务。</summary>
    /// <param name="purpose">要取反的涂抹层。</param>
    /// <returns>是否提交了修改。</returns>
    public bool InvertPaint(ERoiPurpose purpose)
    {
        Cancel();
        if (!RequirePaintArea())
        {
            return false;
        }

        var region = PaintRegion(purpose) ?? EmptyRegion;
        return TryCommit(
            () => WithPaint(Document, purpose, region.Complement(PaintWidth, PaintHeight)),
            "invert-paint"
        );
    }

    /// <summary>交换包含与排除两个涂抹层（连同各自的启用状态）；形成一个撤销事务。</summary>
    /// <returns>是否提交了修改；两层都不存在时返回false。</returns>
    public bool SwapPaintPurpose()
    {
        Cancel();
        var include = PaintRoi(Document, ERoiPurpose.Include);
        var exclude = PaintRoi(Document, ERoiPurpose.Exclude);
        if (include == null && exclude == null)
        {
            return false;
        }

        return TryCommit(
            () =>
            {
                var rois = Document.Rois.Where(r => !IsPaintId(r.Id)).ToList();
                if (exclude != null)
                {
                    rois.Add(new RoiDefinition(PaintIncludeId, exclude.Shape, ERoiPurpose.Include, exclude.Enabled));
                }

                if (include != null)
                {
                    rois.Add(new RoiDefinition(PaintExcludeId, include.Shape, ERoiPurpose.Exclude, include.Enabled));
                }

                return new RoiDocument(rois);
            },
            "swap-paint"
        );
    }

    /// <summary>删除两个涂抹层，几何ROI保持不变；形成一个撤销事务。</summary>
    /// <returns>是否提交了修改。</returns>
    public bool ClearPaint()
    {
        Cancel();
        if (!Document.Rois.Any(r => IsPaintId(r.Id)))
        {
            return false;
        }

        return TryCommit(() => new RoiDocument(Document.Rois.Where(r => !IsPaintId(r.Id))), "clear-paint");
    }

    /// <summary>
    /// 把选中的几何ROI按像素“拍平”进与其用途相同的涂抹层并删除原ROI，之后可以用画笔和橡皮修改；
    /// 拍平后的区域不再能跟随坐标系。禁用、开放折线、点或超出原图的ROI会被拒绝。
    /// </summary>
    /// <returns>是否提交了修改。</returns>
    public bool FlattenSelected()
    {
        var selected = Selected;
        Cancel();
        if (selected == null || IsPaintId(selected.Id) || !RequirePaintArea())
        {
            return false;
        }

        if (!selected.Enabled)
        {
            ValidationError = "已禁用的ROI不能拍平为涂抹。";
            Notify();
            return false;
        }

        return TryCommit(
            () =>
            {
                var pixels = RegionRasterizer.Rasterize(selected.Shape, PaintWidth, PaintHeight);
                var rest = new RoiDocument(Document.Rois.Where(r => r.Id != selected.Id));
                return Painted(rest, pixels, selected.Purpose);
            },
            "flatten"
        );
    }

    private bool BeginStroke(PointD point)
    {
        ResetGesture();
        if (!RequirePaintArea())
        {
            return false;
        }

        _start = point;
        _cursor = point;
        _stroke = new List<PointD> { point };
        _strokeRegion = RegionRasterizer.Stroke(_stroke, BrushRadius, PaintWidth, PaintHeight);
        _preview = _strokeRegion;
        ValidationError = null;
        Notify();
        return true;
    }

    private void ContinueStroke(PointD point)
    {
        var last = _stroke![_stroke.Count - 1];
        _cursor = point;
        // 轨迹点数达到上限后不再延长笔画，松开鼠标即提交已涂部分。
        if (!Same(last, point) && _stroke.Count < 4096)
        {
            _stroke.Add(point);
            var segment = RegionRasterizer.Stroke(new[] { last, point }, BrushRadius, PaintWidth, PaintHeight);
            _strokeRegion = _strokeRegion!.Union(segment);
            _preview = _strokeRegion;
        }

        Notify();
    }

    private void EndStroke(PointD point)
    {
        ContinueStroke(point);
        var stroke = _strokeRegion!;
        bool erase = Tool == ERoiTool.Eraser;
        var purpose = PaintPurpose;
        if (
            !TryCommit(
                () => erase ? Erased(Document, stroke) : Painted(Document, stroke, purpose),
                erase ? "erase" : "paint"
            )
        )
        {
            ResetGesture();
            Notify();
        }
    }

    // 画笔：写入目标层，同时从另一层去掉同一批像素，后涂的覆盖先涂的。
    private static RoiDocument Painted(RoiDocument document, RegionGeometry pixels, ERoiPurpose purpose)
    {
        var other = purpose == ERoiPurpose.Include ? ERoiPurpose.Exclude : ERoiPurpose.Include;
        var target = (PaintRoi(document, purpose)?.Shape as RegionGeometry ?? EmptyRegion).Union(pixels);
        var rest = (PaintRoi(document, other)?.Shape as RegionGeometry ?? EmptyRegion).Subtract(pixels);
        return WithPaint(WithPaint(document, purpose, target), other, rest);
    }

    private static RoiDocument Erased(RoiDocument document, RegionGeometry pixels)
    {
        foreach (var purpose in new[] { ERoiPurpose.Include, ERoiPurpose.Exclude })
        {
            if (PaintRoi(document, purpose)?.Shape is RegionGeometry region)
            {
                document = WithPaint(document, purpose, region.Subtract(pixels));
            }
        }

        return document;
    }

    // 替换、追加或删除（空Region）一个涂抹层，保留它在文档中的位置与启用状态。
    private static RoiDocument WithPaint(RoiDocument document, ERoiPurpose purpose, RegionGeometry region)
    {
        string id = purpose == ERoiPurpose.Include ? PaintIncludeId : PaintExcludeId;
        var existing = document.Rois.FirstOrDefault(r => r.Id == id);
        if (region.AreaPixels == 0)
        {
            return existing == null ? document : new RoiDocument(document.Rois.Where(r => r.Id != id));
        }

        var roi = new RoiDefinition(id, region, purpose, existing?.Enabled ?? true);
        return existing == null
            ? new RoiDocument(document.Rois.Concat(new[] { roi }))
            : new RoiDocument(document.Rois.Select(r => r.Id == id ? roi : r));
    }

    private static RoiDefinition? PaintRoi(RoiDocument document, ERoiPurpose purpose)
    {
        string id = purpose == ERoiPurpose.Include ? PaintIncludeId : PaintExcludeId;
        return document.Rois.FirstOrDefault(r => r.Id == id);
    }

    private bool RequirePaintArea()
    {
        if (PaintWidth > 0)
        {
            return true;
        }

        ValidationError = "涂抹前需要先显示图像（尚未设置原图尺寸）。";
        Notify();
        return false;
    }

    // 生成下一份文档并提交；内容没有变化时不产生历史，预算或几何错误写入ValidationError而不修改文档。
    private bool TryCommit(Func<RoiDocument> next, string operation)
    {
        RoiDocument document;
        try
        {
            document = next();
        }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
        {
            ValidationError = error.Message;
            Notify();
            return false;
        }

        if (SameDocument(Document, document))
        {
            ResetGesture();
            Notify();
            return false;
        }

        ValidationError = null;
        if (_selected != null && !document.Rois.Any(r => r.Id == _selected))
        {
            _selected = null;
        }

        Commit(document, operation);
        return true;
    }

    private static bool SameDocument(RoiDocument a, RoiDocument b)
    {
        if (a.Rois.Count != b.Rois.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Rois.Count; i++)
        {
            var x = a.Rois[i];
            var y = b.Rois[i];
            if (x.Id != y.Id || x.Purpose != y.Purpose || x.Enabled != y.Enabled)
            {
                return false;
            }

            if (!ReferenceEquals(x.Shape, y.Shape)
                && !(x.Shape is RegionGeometry p && y.Shape is RegionGeometry q && p.Runs.SequenceEqual(q.Runs)))
            {
                return false;
            }
        }

        return true;
    }
}
