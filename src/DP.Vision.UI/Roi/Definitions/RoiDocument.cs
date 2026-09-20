using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>不可变的已提交ROI文档；可以安全共享几何，拖动预览不会修改此快照。</summary>
public sealed class RoiDocument
{
    /// <summary>复制并校验有界ROI集合，保留顺序和标识。</summary>
    /// <param name = "rois">待复制的ROI定义，标识必须唯一；最多512项，单轮廓4096顶点，总几何预算100000。</param>
    public RoiDocument(IEnumerable<RoiDefinition> rois)
    {
        var copy = rois?.ToArray() ?? throw new ArgumentNullException(nameof(rois));
        if (
            copy.Length > 512
            || copy.Any(r => r == null)
            || copy.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length
        )
        {
            throw new ArgumentException("ROI IDs must be unique and non-null.");
        }

        long points = 0;
        foreach (var roi in copy)
        {
            if (roi.Shape is ContourGeometry c)
            {
                if (c.Points.Count > 4096)
                {
                    throw new ArgumentException("Editable contour exceeds 4096 vertices.");
                }

                points += c.Points.Count;
            }
            else if (roi.Shape is RegionGeometry r)
            {
                points += r.Runs.Count;
            }
            else if (!(roi.Shape is RectangleGeometry) && !(roi.Shape is EllipseGeometry))
            {
                throw new ArgumentException("Unsupported editable geometry.");
            }
        }

        if (points > 100000)
        {
            throw new ArgumentException("ROI document exceeds editable geometry budget.");
        }

        Rois = Array.AsReadOnly(copy);
    }

    /// <summary>已提交的ROI定义集合，不包含尚未提交的拖动预览。</summary>
    public IReadOnlyList<RoiDefinition> Rois { get; }
}
