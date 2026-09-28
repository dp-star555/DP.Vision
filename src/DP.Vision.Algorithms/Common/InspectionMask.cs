using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>有界的精确包含/排除掩码组合，不依赖UI编辑定义。</summary>
public static class InspectionMask
{
    /// <summary>包含集合并集减排除集合；没有包含形状时以全图为基底。越界形状拒绝。</summary>
    /// <param name="image">只读取布局。</param>
    /// <param name="include">包含几何。</param>
    /// <param name="exclude">排除几何。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>独立精确Region，可为空。</returns>
    public static RegionGeometry Compose(IImageSource image, IEnumerable<Geometry> include, IEnumerable<Geometry> exclude, CancellationToken token = default)
    {
        if (image == null || include == null || exclude == null) throw new ArgumentNullException(nameof(image));
        int width = image.Info.Width, height = image.Info.Height;
        if ((long)width * height > 16777216) throw new InvalidOperationException("Inspection mask exceeds 16M pixel budget.");
        var included = include.ToArray(); var excluded = exclude.ToArray();
        if (included.Length + excluded.Length > 512) throw new ArgumentException("Too many inspection shapes.");
        // 先求全部包含形状的并集（没有包含形状时以整幅图为基底），再逐个扣除排除形状；
        // 直接在游程上运算，不分配整幅图大小的临时掩码。
        RegionGeometry? result = null;
        if (included.Length == 0)
        {
            result = new RegionGeometry(FullRows(width, height));
        }

        foreach (var shape in included)
        {
            var region = RegionRasterizer.Rasterize(shape, width, height, 16777216, token);
            result = result == null ? region : result.Union(region, token);
        }

        foreach (var shape in excluded)
        {
            var hole = RegionRasterizer.Rasterize(shape, width, height, 16777216, token);
            result = result!.Subtract(hole, token);
        }

        return result!;
    }

    private static IEnumerable<RegionRun> FullRows(int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            yield return new RegionRun(y, 0, width);
        }
    }

    /// <summary>验证外部Region的每条游程都在原图内，不静默裁剪。</summary>
    /// <param name="mask">可空掩码。</param>
    /// <param name="image">原图。</param>
    public static void Validate(RegionGeometry? mask, IImageSource image)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (mask != null && mask.Runs.Any(r => r.Row < 0 || r.Row >= image.Info.Height || r.Start < 0 || r.EndExclusive > image.Info.Width))
            throw new ArgumentOutOfRangeException(nameof(mask));
    }

    /// <summary>把Region在指定原图范围内渲染为Gray8掩码图（范围内属于Region的像素为255，其余为0），供以掩码图为参数的算子使用。</summary>
    /// <param name="mask">原图坐标的Region。</param>
    /// <param name="bounds">输出掩码图对应的原图范围。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>与范围同尺寸的独立Gray8图像。</returns>
    public static IImageSource ToImage(RegionGeometry mask, PixelBounds bounds, CancellationToken token = default)
    {
        if (mask == null) throw new ArgumentNullException(nameof(mask));
        var info = new ImageInfo(bounds.Width, bounds.Height, EPixelLayout.Gray8);
        var bytes = new byte[info.ByteLength];
        foreach (var run in mask.Runs)
        {
            token.ThrowIfCancellationRequested();
            int y = run.Row - bounds.Y;
            int start = Math.Max(run.Start, bounds.X), end = Math.Min(run.EndExclusive, bounds.X + bounds.Width);
            if (y < 0 || y >= bounds.Height || end <= start) continue;
            for (int x = start; x < end; x++) bytes[y * info.Stride + x - bounds.X] = 255;
        }

        return VisionImage.CopyFrom(info, bytes);
    }
}
