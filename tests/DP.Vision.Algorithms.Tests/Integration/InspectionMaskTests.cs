using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>检测掩码改为游程运算后，与旧版整幅字节掩码算法逐游程一致。</summary>
[TestClass]
public sealed class InspectionMaskTests
{
    /// <summary>随机包含/排除形状组合（含无包含形状时以整幅图为基底）与旧算法结果相同。</summary>
    [TestMethod]
    public void ComposeMatchesByteMaskReference()
    {
        var random = new Random(11);
        using var image = VisionImage.CopyFrom(new ImageInfo(64, 48, EPixelLayout.Gray8), new byte[64 * 48]);
        for (int i = 0; i < 300; i++)
        {
            var include = RandomShapes(random, random.Next(0, 4)).ToArray();
            var exclude = RandomShapes(random, random.Next(0, 4)).ToArray();
            var expected = Reference(image, include, exclude);
            var actual = InspectionMask.Compose(image, include, exclude);
            CollectionAssert.AreEqual(expected.Runs.ToArray(), actual.Runs.ToArray());
        }
    }

    /// <summary>超过一百万行的长图也能组合掩码（原先游程行号上限低于原图高度上限）。</summary>
    [TestMethod]
    public void ComposeWorksOnImagesTallerThanOneMillionRows()
    {
        const int height = 1040010;
        var info = new ImageInfo(10, height, EPixelLayout.Gray8);
        using var image = VisionImage.CopyFrom(info, new byte[info.ByteLength]);
        var mask = InspectionMask.Compose(
            image,
            new Geometry[] { new RectangleGeometry(new PointD(5, height - 5), 10, 10) },
            Array.Empty<Geometry>()
        );
        Assert.AreEqual(100L, mask.AreaPixels);
        Assert.AreEqual(height - 10, mask.Runs[0].Row);
    }

    private static IEnumerable<Geometry> RandomShapes(Random random, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var center = new PointD(12 + random.NextDouble() * 40, 12 + random.NextDouble() * 24);
            double a = 1 + random.NextDouble() * 10,
                b = 1 + random.NextDouble() * 10,
                angle = random.NextDouble() * Math.PI;
            switch (random.Next(3))
            {
                case 0:
                    yield return new RectangleGeometry(center, a, b, angle);
                    break;
                case 1:
                    yield return new EllipseGeometry(center, a, b, angle);
                    break;
                default:
                    yield return new ContourGeometry(
                        Enumerable
                            .Range(0, random.Next(3, 8))
                            .Select(_ => new PointD(
                                1 + random.NextDouble() * 62,
                                1 + random.NextDouble() * 46
                            )),
                        closed: true,
                        filled: true
                    );
                    break;
            }
        }
    }

    // 旧版算法：整幅字节掩码，先画包含形状再擦除排除形状，最后逐行扫描为游程。
    private static RegionGeometry Reference(IImageSource image, Geometry[] include, Geometry[] exclude)
    {
        int width = image.Info.Width,
            height = image.Info.Height;
        var pixels = new byte[width * height];
        if (include.Length == 0)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = 1;
            }
        }

        var painted = include
            .Select(g => (Shape: g, Value: (byte)1))
            .Concat(exclude.Select(g => (Shape: g, Value: (byte)0)));
        foreach (var item in painted)
        {
            foreach (var run in RegionRasterizer.Rasterize(item.Shape, width, height).Runs)
            {
                for (int x = run.Start; x < run.EndExclusive; x++)
                {
                    pixels[run.Row * width + x] = item.Value;
                }
            }
        }

        var runs = new List<RegionRun>();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                if (pixels[y * width + x] == 0)
                {
                    x++;
                    continue;
                }

                int start = x++;
                while (x < width && pixels[y * width + x] != 0)
                {
                    x++;
                }

                runs.Add(new RegionRun(y, start, x));
            }
        }

        return new RegionGeometry(runs);
    }
}
