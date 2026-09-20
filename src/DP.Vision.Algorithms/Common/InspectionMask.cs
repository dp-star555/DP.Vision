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
        var pixels = new byte[width * height];
        if (included.Length == 0) for (int i = 0; i < pixels.Length; i++) pixels[i] = 1;
        foreach (var item in included.Select(g => new { Shape = g, Value = (byte)1 }).Concat(excluded.Select(g => new { Shape = g, Value = (byte)0 })))
        {
            var region = RegionRasterizer.Rasterize(item.Shape, width, height, 16777216, token);
            foreach (var run in region.Runs)
            {
                token.ThrowIfCancellationRequested();
                for (int x = run.Start; x < run.EndExclusive; x++) pixels[run.Row * width + x] = item.Value;
            }
        }
        var runs = new List<RegionRun>();
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int x = 0;
            while (x < width)
            {
                if (pixels[y * width + x] == 0) { x++; continue; }
                int start = x++;
                while (x < width && pixels[y * width + x] != 0) x++;
                if (runs.Count == 2000000) throw new InvalidOperationException("Inspection mask exceeds run budget.");
                runs.Add(new RegionRun(y, start, x));
            }
        }
        return new RegionGeometry(runs);
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
}
