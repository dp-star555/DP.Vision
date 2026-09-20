using System;

namespace DP.Vision;

/// <summary>
/// 从原始Region游程生成显示掩码。第0级保持原始像素成员关系；较粗级别仅稀疏采样，不修改原始游程。
/// </summary>
public static class RegionMask
{
    /// <summary>
    /// 生成指定图块的0/255掩码，Region孔洞保持为0，不使用外接矩形代替实际区域。
    /// </summary>
    /// <param name = "region">按行排序、互不重叠的原始Region快照，只读使用。</param>
    /// <param name = "originX">图块左上角的原图列坐标，单位为原始像素。</param>
    /// <param name = "originY">图块左上角的原图行坐标，单位为原始像素。</param>
    /// <param name = "width">输出图块宽度，范围1–1024，单位为输出像素。</param>
    /// <param name = "height">输出图块高度，范围1–1024，单位为输出像素。</param>
    /// <param name = "level">采样级别，范围0–20；相邻输出像素对应的原图坐标间隔为2的level次方。不是面积平均或缺陷检测下采样。</param>
    /// <returns>按行排列、长度为width乘height的独立字节数组；成员像素为255，其余为0。</returns>
    /// <exception cref = "ArgumentNullException">Region为空。</exception>
    /// <exception cref = "ArgumentOutOfRangeException">图块尺寸或采样级别超出范围。</exception>
    public static byte[] Tile(
        RegionGeometry region,
        int originX,
        int originY,
        int width,
        int height,
        int level = 0
    )
    {
        if (region == null)
        {
            throw new ArgumentNullException(nameof(region));
        }

        if (width < 1 || height < 1 || width > 1024 || height > 1024 || level < 0 || level > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var mask = new byte[width * height];
        long factor = 1L << level;
        // 二分定位首个可能覆盖图块的游程，避免每次都从Region第一行开始遍历。
        int lower = 0;
        int upper = region.Runs.Count;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (region.Runs[middle].Row < originY)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        for (int i = lower; i < region.Runs.Count; i++)
        {
            var run = region.Runs[i];
            long relativeRow = (long)run.Row - originY;
            if (relativeRow >= height * factor)
            {
                break;
            }

            if (relativeRow % factor != 0)
            {
                continue;
            }

            int outputY = (int)(relativeRow / factor);
            long relativeStart = (long)run.Start - originX;
            long relativeEnd = (long)run.EndExclusive - originX;
            int left = (int)Math.Max(0, Math.Min(width, Math.Ceiling(relativeStart / (double)factor)));
            int right = (int)Math.Max(0, Math.Min(width, Math.Ceiling(relativeEnd / (double)factor)));
            // 游程右端为排他端点；向上取整后仍保持相同的采样成员关系。
            for (int x = left; x < right; x++)
            {
                mask[outputY * width + x] = 255;
            }
        }

        return mask;
    }
}
