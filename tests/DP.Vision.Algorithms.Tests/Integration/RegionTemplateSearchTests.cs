using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.Vision.Algorithms.Tests;

/// <summary>带区域的模板搜索：模板整体须落在区域内；多个ROI合成为一个区域，跨越两块ROI之间空隙的位置不合法。</summary>
[TestClass]
public sealed class RegionTemplateSearchTests
{
    private static (IImageSource Image, byte[] Pixels) Noise(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height];
        random.NextBytes(pixels);
        return (VisionImage.CopyFrom(new ImageInfo(width, height, EPixelLayout.Gray8), pixels), pixels);
    }

    private static RegionGeometry Rects(int width, int height, params (int X, int Y, int W, int H)[] rects)
    {
        return RegionRasterizer.Compose(width, height,
            rects.Select(r => (Geometry)new RectangleGeometry(new PointD(r.X + r.W / 2d, r.Y + r.H / 2d), r.W, r.H)),
            Array.Empty<Geometry>());
    }

    /// <summary>与逐位置暴力判定（足迹每个像素都在区域内）的最小值位置完全一致，区域含孔洞和不连通块。</summary>
    [TestMethod]
    public void MatchesBruteForceWithHolesAndDisjointParts()
    {
        var random = new Random(3);
        for (int trial = 0; trial < 12; trial++)
        {
            var (image, pixels) = Noise(96, 72, trial);
            using (image)
            {
                using var frame = new ImageFrame("f" + trial, image);
                var include = Enumerable.Range(0, 3).Select(_ => (Geometry)new EllipseGeometry(
                    new PointD(10 + random.Next(76), 10 + random.Next(52)), 8 + random.Next(20), 6 + random.Next(16))).ToArray();
                var hole = new RectangleGeometry(new PointD(20 + random.Next(56), 15 + random.Next(42)), 6, 6);
                var region = RegionRasterizer.Compose(96, 72, include.Select(g => ClipEllipse((EllipseGeometry)g)), new Geometry[] { hole });
                var search = new PixelBounds(0, 0, 96, 72);
                var templateBounds = new PixelBounds(30, 20, 9, 7);
                var result = new OpenCvTemplateLocator().Locate(frame, search, frame, templateBounds, 0, regionMask: region);

                using var mat = new Mat(72, 96, MatType.CV_8UC1); System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
                using var pattern = new Mat(mat, new Rect(30, 20, 9, 7));
                using var scores = new Mat(); Cv2.MatchTemplate(mat, pattern, scores, TemplateMatchModes.SqDiff);
                double best = double.PositiveInfinity; (int X, int Y)? at = null;
                for (int y = 0; y < scores.Rows; y++) for (int x = 0; x < scores.Cols; x++)
                {
                    bool inside = true;
                    for (int dy = 0; dy < 7 && inside; dy++) for (int dx = 0; dx < 9 && inside; dx++)
                        inside = region.Contains(new PointD(x + dx + .5, y + dy + .5));
                    float value = scores.At<float>(y, x);
                    if (inside && value < best) { best = value; at = (x, y); }
                }

                if (at == null) { Assert.AreEqual(0, result.Score, "trial " + trial); continue; }
                double expected = Math.Max(0, Math.Min(1, 1 - best / (65025d * 63)));
                Assert.AreEqual(expected, result.Score, 1e-6, "trial " + trial); // 全图与裁剪窗口的SqDiff浮点噪声不同，位置必须相同。
                Assert.IsTrue(result.Found, "trial " + trial);
                Assert.AreEqual(at.Value.X + 4.5, result.CenterX, "trial " + trial);
                Assert.AreEqual(at.Value.Y + 3.5, result.CenterY, "trial " + trial);
            }
        }
    }

    // 椭圆可能超出图像，裁到图像内再参与合成（合成本身拒绝越界形状）。
    private static Geometry ClipEllipse(EllipseGeometry e)
    {
        var region = RegionRasterizer.Rasterize(new RectangleGeometry(new PointD(48, 36), 96, 72), 96, 72);
        var runs = new List<RegionRun>();
        foreach (var run in region.Runs)
            for (int x = run.Start; x < run.EndExclusive; x++)
                if (e.Contains(new PointD(x + .5, run.Row + .5))) runs.Add(new RegionRun(run.Row, x, x + 1));
        return new RegionGeometry(runs).Union(new RegionGeometry(Array.Empty<RegionRun>()));
    }

    /// <summary>两块不相连的ROI：目标在第二块里能找到；目标跨在两块中间时不能被选中。</summary>
    [TestMethod]
    public void MultipleRoisRequireTemplateInsideOneOfThem()
    {
        var (image, _) = Noise(120, 60, 9);
        using (image)
        {
            using var frame = new ImageFrame("two", image);
            var template = new PixelBounds(80, 20, 12, 12);
            var locator = new OpenCvTemplateLocator();

            var both = Rects(120, 60, (0, 0, 40, 60), (70, 10, 40, 40));
            var found = locator.Locate(frame, new PixelBounds(0, 0, 120, 60), frame, template, .99, regionMask: both);
            Assert.IsTrue(found.Found);
            Assert.AreEqual(86, found.CenterX);

            // 目标(80..92)跨过两块之间的空隙(30..86)：区域外的像素让该位置不合法。
            var split = Rects(120, 60, (0, 0, 86, 60), (90, 0, 30, 60));
            var rejected = locator.Locate(frame, new PixelBounds(0, 0, 120, 60), frame, template, .99, regionMask: split);
            Assert.IsFalse(rejected.Found);
        }
    }

    /// <summary>区域与搜索矩形不相交或小于模板时直接返回未找到，不抛异常。</summary>
    [TestMethod]
    public void RegionOutsideSearchFindsNothing()
    {
        var (image, _) = Noise(64, 48, 1);
        using (image)
        {
            using var frame = new ImageFrame("none", image);
            var region = Rects(64, 48, (50, 30, 10, 10));
            var result = new OpenCvTemplateLocator().Locate(frame, new PixelBounds(0, 0, 40, 30), frame, new PixelBounds(0, 0, 8, 8), .5, regionMask: region);
            Assert.IsFalse(result.Found);
            var pose = new OpenCvTemplatePoseLocator().Locate(frame, frame, new PixelBounds(0, 0, 40, 30), new TemplatePoseOptions(0, 0), regionMask: region);
            Assert.IsFalse(pose.Found);
        }
    }
}
