using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.UI;

/// <summary>不可变的已提交ROI文档；可以安全共享几何，拖动预览不会修改此快照。</summary>
public sealed class RoiDocument
{
    /// <summary>复制并校验有界ROI集合，保留顺序和标识。</summary>
    /// <param name = "rois">待复制的ROI定义，标识必须唯一；最多512项，单轮廓4096顶点，总几何预算100000。</param>
    public RoiDocument(IEnumerable<RoiDefinition> rois)
    {
        var copy = rois?.ToArray() ?? throw new ArgumentNullException(nameof(rois), "ROI集合不能为空。");
        if (copy.Length > 512)
        {
            throw new ArgumentException("ROI数量不得超过512个。", nameof(rois));
        }

        if (copy.Any(r => r == null))
        {
            throw new ArgumentException("ROI集合不能包含空项。", nameof(rois));
        }

        if (copy.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("ROI标识必须唯一。", nameof(rois));
        }

        long points = 0;
        foreach (var roi in copy)
        {
            if (roi.Shape is ContourGeometry c)
            {
                if (c.Points.Count > 4096)
                {
                    throw new ArgumentException("可编辑轮廓的顶点不得超过4096个。", nameof(rois));
                }

                points += c.Points.Count;
            }
            else if (roi.Shape is RegionGeometry r)
            {
                points += r.Runs.Count;
            }
            else if (!(roi.Shape is RectangleGeometry) && !(roi.Shape is EllipseGeometry))
            {
                throw new ArgumentException("不支持的可编辑几何类型。", nameof(rois));
            }
        }

        if (points > 100000)
        {
            throw new ArgumentException("ROI文档的轮廓顶点与Region游程总数超过100000的预算。", nameof(rois));
        }

        Rois = Array.AsReadOnly(copy);
    }

    /// <summary>已提交的ROI定义集合，不包含尚未提交的拖动预览。</summary>
    public IReadOnlyList<RoiDefinition> Rois { get; }

    /// <summary>
    /// 把已启用的ROI合成为后台有效区域：全部包含（几何ROI与涂抹层）的并集减去全部排除的并集，排除优先、与顺序无关；
    /// 没有任何ROI时为整幅图。全部禁用时报错而不回退全图；开放折线、点或超出原图的ROI报错并指出其标识。
    /// </summary>
    /// <param name="imageWidth">原图宽度，单位为像素。</param>
    /// <param name="imageHeight">原图高度，单位为像素。</param>
    /// <param name="maximumWork">单个ROI的栅格化工作量上限，含义同<see cref="RegionRasterizer.Rasterize"/>。</param>
    /// <param name="token">协作式取消标记。</param>
    /// <returns>独立Region，可为空。</returns>
    public RegionGeometry ToRegion(
        int imageWidth,
        int imageHeight,
        long maximumWork = 16777216,
        CancellationToken token = default
    )
    {
        var enabled = Rois.Where(r => r.Enabled).ToArray();
        if (Rois.Count > 0 && enabled.Length == 0)
        {
            throw new InvalidOperationException("全部ROI已禁用，有效区域不会回退为整幅图。");
        }

        foreach (var roi in enabled)
        {
            if (roi.Shape is ContourGeometry c && (!c.Closed || !c.Filled))
            {
                throw new ArgumentException($"ROI“{roi.Id}”是开放折线或点，不能参与有效区域。");
            }

            var b = roi.Shape.Bounds;
            if (b.X < 0 || b.Y < 0 || b.Right > imageWidth || b.Bottom > imageHeight)
            {
                throw new ArgumentException($"ROI“{roi.Id}”超出原图范围（{imageWidth}×{imageHeight}）。");
            }
        }

        return RegionRasterizer.Compose(
            imageWidth,
            imageHeight,
            enabled.Where(r => r.Purpose == ERoiPurpose.Include).Select(r => r.Shape),
            enabled.Where(r => r.Purpose == ERoiPurpose.Exclude).Select(r => r.Shape),
            maximumWork,
            token
        );
    }
}
