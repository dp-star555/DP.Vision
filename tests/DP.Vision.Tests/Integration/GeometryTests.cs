using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>几何限值、逐行栅格化、Region集合运算、平移与值相等。</summary>
[TestClass]
public sealed class GeometryTests
{
    private const int Size = 48;

    /// <summary>填充轮廓的逐行扫描与逐像素中心调用Contains的旧算法逐游程一致，包括中心恰好落在边或顶点上的情形。</summary>
    [TestMethod]
    public void ContourRasterizationMatchesPixelCenterContains()
    {
        var random = new Random(20260924);
        int compared = 0;
        foreach (var contour in RandomContours(random).Take(3000))
        {
            var expected = ReferenceRasterize(contour);
            var actual = RegionRasterizer.Rasterize(contour, Size, Size);
            CollectionAssert.AreEqual(expected.Runs.ToArray(), actual.Runs.ToArray());
            compared++;
        }

        Assert.AreEqual(3000, compared);
    }

    /// <summary>矩形与椭圆仍按像素中心成员关系栅格化。</summary>
    [TestMethod]
    public void RectangleAndEllipseRasterizationMatchesPixelCenterContains()
    {
        var random = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            var center = new PointD(16 + random.NextDouble() * 16, 16 + random.NextDouble() * 16);
            double angle = random.NextDouble() * Math.PI,
                a = 1 + random.NextDouble() * 10,
                b = 1 + random.NextDouble() * 10;
            foreach (Geometry shape in new Geometry[]
            {
                new RectangleGeometry(center, a, b, angle),
                new EllipseGeometry(center, a, b, angle),
            })
            {
                CollectionAssert.AreEqual(
                    ReferenceRasterize(shape).Runs.ToArray(),
                    RegionRasterizer.Rasterize(shape, Size, Size).Runs.ToArray()
                );
            }
        }
    }

    /// <summary>并、交、差与逐像素布尔运算一致；并与差的结果不含相接游程。</summary>
    [TestMethod]
    public void RegionSetOperationsMatchPixelwiseLogic()
    {
        var random = new Random(3);
        for (int i = 0; i < 400; i++)
        {
            var a = RandomMask(random);
            var b = RandomMask(random);
            var ra = ToRegion(a);
            var rb = ToRegion(b);
            AssertMembership(ra.Union(rb), (x, y) => a[y, x] || b[y, x]);
            AssertMembership(ra.Intersect(rb), (x, y) => a[y, x] && b[y, x]);
            AssertMembership(ra.Subtract(rb), (x, y) => a[y, x] && !b[y, x]);
            AssertNoTouchingRuns(ra.Union(rb));
            AssertNoTouchingRuns(ra.Subtract(rb));
        }
    }

    /// <summary>空Region参与运算得到正确结果。</summary>
    [TestMethod]
    public void RegionSetOperationsHandleEmptyRegions()
    {
        var empty = new RegionGeometry(Array.Empty<RegionRun>());
        var region = new RegionGeometry(new[] { new RegionRun(1, 2, 5) });
        Assert.AreEqual(3L, empty.Union(region).AreaPixels);
        Assert.AreEqual(0L, empty.Intersect(region).AreaPixels);
        Assert.AreEqual(3L, region.Subtract(empty).AreaPixels);
        Assert.AreEqual(0L, empty.Subtract(region).AreaPixels);
    }

    /// <summary>Region坐标上限与原图尺寸上限一致：超过一百万行的线扫长图仍能表示游程。</summary>
    [TestMethod]
    public void RegionRunsCoverTheWholeImageCoordinateRange()
    {
        var tall = new RegionGeometry(
            new[] { new RegionRun(1040000, 0, 5), new RegionRun(ImageInfo.MaxDimension - 1, 3, 4) }
        );
        Assert.IsTrue(tall.Contains(new PointD(2.5, 1040000.5)));
        Assert.AreEqual(6L, tall.AreaPixels);
        var bottom = RegionRasterizer.Rasterize(
            new RectangleGeometry(new PointD(5, ImageInfo.MaxDimension - 2), 10, 4),
            10,
            ImageInfo.MaxDimension
        );
        Assert.AreEqual(40L, bottom.AreaPixels);
        var tooFar = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RegionRun(ImageInfo.MaxDimension + 1, 0, 1)
        );
        Assert.AreEqual("row", tooFar.ParamName);
    }

    /// <summary>平移保持形状属性；Region按整像素平移；元素数用于几何预算。</summary>
    [TestMethod]
    public void TranslateAndElementCountArePerShape()
    {
        var original = new RectangleGeometry(new PointD(10, 10), 4, 2, .5);
        var rectangle = (RectangleGeometry)original.Translate(1.5, -2);
        Assert.AreEqual(new PointD(11.5, 8), rectangle.Center);
        Assert.AreEqual(.5, rectangle.Angle);
        var ellipse = (EllipseGeometry)new EllipseGeometry(new PointD(3, 3), 2, 1).Translate(1, 1);
        Assert.AreEqual(new PointD(4, 4), ellipse.Center);
        var triangle = new[] { new PointD(1, 1), new PointD(5, 1), new PointD(5, 5) };
        var contour = new ContourGeometry(triangle, closed: true, filled: true);
        var moved = (ContourGeometry)contour.Translate(1, 2);
        Assert.AreEqual(new PointD(2, 3), moved.Points[0]);
        Assert.IsTrue(moved.Closed && moved.Filled);
        var region = (RegionGeometry)new RegionGeometry(new[] { new RegionRun(2, 3, 6) }).Translate(1.4, 2.6);
        Assert.AreEqual(new RegionRun(5, 4, 7), region.Runs[0]);

        Assert.AreEqual(4L, rectangle.ElementCount);
        Assert.AreEqual(4L, ellipse.ElementCount);
        Assert.AreEqual(3L, contour.ElementCount);
        Assert.AreEqual(1L, region.ElementCount);
    }

    /// <summary>闭合轮廓首点重复只是写法差异，RepeatsFirstPoint如实报告。</summary>
    [TestMethod]
    public void RepeatsFirstPointReportsExplicitClosure()
    {
        var open = new[] { new PointD(0, 0), new PointD(4, 0), new PointD(4, 4) };
        var repeated = open.Concat(new[] { new PointD(0, 0) }).ToArray();
        Assert.IsFalse(new ContourGeometry(open, closed: true).RepeatsFirstPoint);
        Assert.IsTrue(new ContourGeometry(repeated, closed: true).RepeatsFirstPoint);
        Assert.IsFalse(new ContourGeometry(repeated, closed: false).RepeatsFirstPoint);
    }

    /// <summary>坐标、范围与游程按值比较。</summary>
    [TestMethod]
    public void GeometryValuesHaveValueEquality()
    {
        Assert.IsTrue(new PointD(1, 2) == new PointD(1, 2));
        Assert.IsTrue(new PointD(1, 2) != new PointD(2, 1));
        Assert.IsTrue(new RectD(0, 0, 3, 4) == new RectD(0, 0, 3, 4));
        Assert.IsTrue(new RectD(0, 0, 3, 4) != new RectD(0, 0, 4, 3));
        Assert.IsTrue(new RegionRun(1, 2, 3) == new RegionRun(1, 2, 3));
        Assert.AreEqual(new RegionRun(1, 2, 3).GetHashCode(), new RegionRun(1, 2, 3).GetHashCode());
    }

    /// <summary>非法参数报告对应的参数名。</summary>
    [TestMethod]
    public void InvalidArgumentsReportTheirOwnName()
    {
        Assert.AreEqual("y", ParamName(() => new PointD(0, double.NaN)));
        Assert.AreEqual("height", ParamName(() => new RectD(0, 0, 1, -1)));
        Assert.AreEqual("angle", ParamName(() => new RectangleGeometry(new PointD(0, 0), 1, 1, double.NaN)));
        Assert.AreEqual("radiusY", ParamName(() => new EllipseGeometry(new PointD(0, 0), 1, 0)));
        Assert.AreEqual("start", ParamName(() => new RegionRun(0, -ImageInfo.MaxDimension - 1, 0)));

        static string? ParamName(Action create)
        {
            return Assert.ThrowsExactly<ArgumentOutOfRangeException>(create).ParamName;
        }
    }

    // 旧版栅格化算法：在外接框内逐像素调用Contains判定像素中心。
    private static RegionGeometry ReferenceRasterize(Geometry shape)
    {
        var bounds = shape.Bounds;
        int left = (int)Math.Floor(bounds.X),
            top = (int)Math.Floor(bounds.Y),
            right = (int)Math.Ceiling(bounds.Right),
            bottom = (int)Math.Ceiling(bounds.Bottom);
        var runs = new List<RegionRun>();
        for (int y = top; y < bottom; y++)
        {
            int start = -1;
            for (int x = left; x < right; x++)
            {
                bool inside = shape.Contains(new PointD(x + .5, y + .5));
                if (inside && start < 0)
                {
                    start = x;
                }

                if (!inside && start >= 0)
                {
                    runs.Add(new RegionRun(y, start, x));
                    start = -1;
                }
            }

            if (start >= 0)
            {
                runs.Add(new RegionRun(y, start, right));
            }
        }

        return new RegionGeometry(runs);
    }

    // 覆盖一般实数坐标、整数与半整数坐标（使像素中心恰好落在边或顶点上）、水平边及自相交轮廓。
    private static IEnumerable<ContourGeometry> RandomContours(Random random)
    {
        yield return Polygon(
            (10.5, 10.5),
            (30.5, 10.5),
            (30.5, 20.5),
            (20.5, 20.5),
            (20.5, 30.5),
            (10.5, 30.5)
        );
        yield return Polygon((5, 5), (40, 40), (40, 5), (5, 40));
        yield return Polygon((24, 2), (29, 40), (4, 14), (44, 14), (19, 40));
        while (true)
        {
            int count = random.Next(3, 13);
            int mode = random.Next(4);
            var points = new PointD[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = new PointD(Coordinate(random, mode), Coordinate(random, mode));
            }

            if (random.Next(5) == 0)
            {
                points = points.Concat(new[] { points[0] }).ToArray();
            }

            yield return new ContourGeometry(points, closed: true, filled: true);
        }
    }

    private static double Coordinate(Random random, int mode)
    {
        return mode switch
        {
            0 => 1 + random.NextDouble() * 45,
            1 => random.Next(1, 46),
            2 => random.Next(1, 46) + .5,
            _ => random.Next(2) == 0 ? random.Next(1, 46) + .5 : 1 + random.NextDouble() * 45,
        };
    }

    private static ContourGeometry Polygon(params (double X, double Y)[] points)
    {
        return new ContourGeometry(points.Select(p => new PointD(p.X, p.Y)), closed: true, filled: true);
    }

    private static bool[,] RandomMask(Random random)
    {
        var mask = new bool[20, 20];
        double density = random.NextDouble();
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                mask[y, x] = random.NextDouble() < density;
            }
        }

        return mask;
    }

    // 允许相接游程，以覆盖由调用方手工构造的非合并输入。
    private static RegionGeometry ToRegion(bool[,] mask)
    {
        var runs = new List<RegionRun>();
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                if (mask[y, x])
                {
                    runs.Add(new RegionRun(y, x, x + 1));
                }
            }
        }

        return new RegionGeometry(runs);
    }

    private static void AssertMembership(RegionGeometry region, Func<int, int, bool> expected)
    {
        long area = 0;
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                bool inside = expected(x, y);
                area += inside ? 1 : 0;
                Assert.AreEqual(inside, region.Contains(new PointD(x + .5, y + .5)), $"pixel ({x},{y})");
            }
        }

        Assert.AreEqual(area, region.AreaPixels);
    }

    private static void AssertNoTouchingRuns(RegionGeometry region)
    {
        for (int i = 1; i < region.Runs.Count; i++)
        {
            var previous = region.Runs[i - 1];
            var run = region.Runs[i];
            Assert.IsFalse(run.Row == previous.Row && run.Start == previous.EndExclusive, "touching runs");
        }
    }
}
