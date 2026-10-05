using System;

namespace DP.Vision.UI;

/// <summary>
/// 弹出式“ROI与涂抹”编辑窗口的共用模型：持有样图租约和编辑器，在每次文档提交后重新合成有效区域，
/// 并生成带有效区域叠加的画布帧。WinForms与WPF窗口只负责布局和按钮接线。只在UI线程使用。
/// </summary>
public sealed class RoiMaskSession : IDisposable
{
    /// <summary>窗口画布使用的固定帧标识。</summary>
    public const string FrameId = "roi-mask-editor";

    /// <summary>说明几何ROI与涂抹层同时存在时的关系，供窗口显示。</summary>
    public const string RuleText =
        "有效区域 = 全部包含（几何ROI + 涂抹）的并集 − 全部排除（几何ROI + 涂抹）的并集；排除优先，与绘制先后无关。"
        + "画笔写入当前用途的涂抹层并覆盖另一涂抹层的同一位置；橡皮只擦涂抹层，不改几何ROI。";

    private const uint EffectiveColor = 0xFF22DD88;
    private IImageSource? _image;
    private long _sequence;
    private bool _showEffective = true;

    /// <summary>以样图和已有ROI文档开始编辑；默认工具为画笔。</summary>
    /// <param name="image">样图；内部Retain，调用方仍拥有原句柄。</param>
    /// <param name="document">已有ROI文档；null表示从空文档开始。</param>
    public RoiMaskSession(IImageSource image, RoiDocument? document = null)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image), "样图不能为空。");
        }

        Width = image.Info.Width;
        Height = image.Info.Height;
        Editor = new RoiEditor();
        Editor.Load(document ?? new RoiDocument(Array.Empty<RoiDefinition>()));
        Editor.SetPaintArea(Width, Height);
        Editor.Tool = ERoiTool.Brush;
        Editor.DocumentChanged += (_, __) => Recompose();
        Editor.Changed += (_, __) => Changed?.Invoke(this, EventArgs.Empty);
        _image = image.Retain();
        Recompose();
    }

    /// <summary>窗口画布使用的编辑器。</summary>
    public RoiEditor Editor { get; }

    /// <summary>样图宽度，单位为像素。</summary>
    public int Width { get; }

    /// <summary>样图高度，单位为像素。</summary>
    public int Height { get; }

    /// <summary>当前已提交的ROI文档（几何ROI与涂抹层）。</summary>
    public RoiDocument Document => Editor.Document;

    /// <summary>最近一次合成的有效区域；合成失败时为null，原因见<see cref="Summary"/>。</summary>
    public RegionGeometry? Effective { get; private set; }

    /// <summary>有效区域像素数，或合成失败的原因。</summary>
    public string Summary { get; private set; } = "";

    /// <summary>是否在画布上叠加显示有效区域；只影响显示。</summary>
    public bool ShowEffective
    {
        get => _showEffective;
        set
        {
            _showEffective = value;
            FrameChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>编辑器显示状态变化（含指针预览）。</summary>
    public event EventHandler? Changed;

    /// <summary>需要重新提交画布帧：有效区域重新合成或显示开关变化。</summary>
    public event EventHandler? FrameChanged;

    /// <summary>状态栏文字：编辑器拒绝原因优先，其次是有效区域摘要。</summary>
    public string Status => Editor.ValidationError is { } error ? "⚠ " + error : Summary;

    /// <summary>把选中ROI的用途在包含与排除之间切换；涂抹层请用<see cref="RoiEditor.SwapPaintPurpose"/>。</summary>
    public void ToggleSelectedPurpose()
    {
        foreach (var roi in Editor.Document.Rois)
        {
            if (roi.Id == Editor.SelectedId)
            {
                Editor.SetSelectedMetadata(
                    roi.Purpose == ERoiPurpose.Include ? ERoiPurpose.Exclude : ERoiPurpose.Include,
                    roi.Enabled
                );
                return;
            }
        }
    }

    /// <summary>创建当前画布帧：样图加可选的有效区域叠加。调用方负责释放返回的帧。</summary>
    /// <returns>新的预览帧，序号递增。</returns>
    public CanvasFrame CreateFrame()
    {
        var image = _image ?? throw new ObjectDisposedException(nameof(RoiMaskSession), "编辑会话已释放。");
        var visuals = _showEffective && Effective != null
            ? new[] { new Visual("effective", Effective, EffectiveColor) }
            : Array.Empty<Visual>();
        var overlay = new GeometryOverlay(
            FrameId,
            new[] { new CanvasLayer("effective", ELayerKind.Region, visuals, -10, name: "有效区域") }
        );
        return new CanvasFrame(FrameId, ++_sequence, image, overlay);
    }

    private void Recompose()
    {
        try
        {
            Effective = Editor.Document.ToRegion(Width, Height);
            Summary = $"有效区域 {Effective.AreaPixels} 像素（{Width}×{Height}）";
        }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
        {
            Effective = null;
            Summary = "⚠ 有效区域无法合成：" + error.Message;
        }

        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>释放样图租约。</summary>
    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }
}
