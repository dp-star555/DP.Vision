using System.Collections.Generic;

namespace DP.Vision.UI;

/// <summary>一次原子捕获的视图浏览界面状态，不包含图像资源或业务执行状态。</summary>
public sealed class ResultBrowserSnapshot
{
    internal ResultBrowserSnapshot(
        long version,
        object collection,
        string? viewId,
        IReadOnlyList<BrowserChoice> views,
        IReadOnlyList<BrowserLayerChoice> layers,
        string status,
        bool hasImage,
        long retainedPixelBytes
    )
    {
        Version = version;
        Collection = collection;
        ViewId = viewId;
        Views = views;
        Layers = layers;
        Status = status;
        HasImage = hasImage;
        RetainedPixelBytes = retainedPixelBytes;
    }

    internal object Collection { get; }

    /// <summary>界面状态版本，供UI合并刷新，不是输入结果的排序依据。</summary>
    public long Version { get; }

    /// <summary>当前视图键，无视图时为null。</summary>
    public string? ViewId { get; }

    /// <summary>按提交顺序排列的视图，包括未保留像素的视图。</summary>
    public IReadOnlyList<BrowserChoice> Views { get; }

    /// <summary>当前视图的可选图层。</summary>
    public IReadOnlyList<BrowserLayerChoice> Layers { get; }

    /// <summary>视图名称或预览不可用原因，不表达业务判定。</summary>
    public string Status { get; }

    /// <summary>当前视图是否保留底图。</summary>
    public bool HasImage { get; }

    /// <summary>会话保留的像素字节记账，不包括画布、临时租约及客户源。</summary>
    public long RetainedPixelBytes { get; }
}
