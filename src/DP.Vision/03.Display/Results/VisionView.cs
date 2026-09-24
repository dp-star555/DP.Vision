using System;

namespace DP.Vision;

/// <summary>一张底图及严格属于该图坐标系的图层集合；描述不拥有图像租约。</summary>
public sealed class VisionView
{
    /// <summary>创建视图描述，不复制像素；输入源须保持有效直到SetViews返回。</summary>
    /// <param name="id">视图集合内唯一的稳定键，用于选择和图层偏好，不使用列表位置代替。</param>
    /// <param name="name">视图下拉框显示名称。</param>
    /// <param name="frameId">不可变底图的内容身份，像素改变必须更换。</param>
    /// <param name="image">借用的图像源，调用方只管理自己的句柄。</param>
    /// <param name="overlay">可选同帧叠加；局部坐标或变换后的几何须由调用者显式转换。</param>
    public VisionView(
        string id,
        string name,
        string frameId,
        IImageSource image,
        GeometryOverlay? overlay = null
    )
    {
        ValidateText(id, nameof(id));
        ValidateText(name, nameof(name));
        ValidateText(frameId, nameof(frameId));
        if (overlay != null && overlay.FrameId != frameId)
            throw new ArgumentException("视图底图与叠加内容的帧身份不一致。", nameof(overlay));
        Id = id;
        Name = name;
        FrameId = frameId;
        Image = image ?? throw new ArgumentNullException(nameof(image), "视图底图不能为空。");
        Overlay = overlay ?? new GeometryOverlay(frameId, Array.Empty<CanvasLayer>());
    }

    /// <summary>集合内唯一的稳定视图键。</summary>
    public string Id { get; }

    /// <summary>显示名称。</summary>
    public string Name { get; }

    /// <summary>底图内容身份。</summary>
    public string FrameId { get; }

    /// <summary>提交期间借用的输入源，不转移客户原租约。</summary>
    public IImageSource Image { get; }

    /// <summary>同一底图坐标中的叠加快照。</summary>
    public GeometryOverlay Overlay { get; }

    private static void ValidateText(string text, string parameter)
    {
        if (!Identity.IsValid(text))
            throw new ArgumentException("标识或名称不能为空，且不得超过256个字符。", parameter);
    }
}
