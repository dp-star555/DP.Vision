using System;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.Vision.Algorithms.Tests;

/// <summary>受限平移配准与掩码辅助：已知平移被恢复，忽略区不参与，纹理不足与超限平移不采用。</summary>
[TestClass]
public sealed class TranslationRegistrationTests
{
    private static Mat Pattern()
    {
        var image = new Mat(160, 240, MatType.CV_8UC1, Scalar.All(230));
        var random = new Random(3);
        for (int k = 0; k < 20; k++)
        {
            Cv2.PutText(
                image,
                ((char)('A' + random.Next(26))).ToString(),
                new Point(random.Next(10, 220), random.Next(20, 150)),
                HersheyFonts.HersheySimplex,
                .9,
                Scalar.All(random.Next(0, 60)),
                2
            );
        }

        return image;
    }

    private static Mat Shift(Mat source, double dx, double dy)
    {
        using var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0);
        m.Set(0, 1, 0.0);
        m.Set(0, 2, dx);
        m.Set(1, 0, 0.0);
        m.Set(1, 1, 1.0);
        m.Set(1, 2, dy);
        var output = new Mat();
        Cv2.WarpAffine(
            source,
            output,
            m,
            source.Size(),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.All(230)
        );
        return output;
    }

    private static IImageSource Source(Mat m)
    {
        var bytes = new byte[m.Rows * m.Cols];
        using var packed = m.Clone();
        System.Runtime.InteropServices.Marshal.Copy(packed.Data, bytes, 0, bytes.Length);
        return VisionImage.CopyFrom(new ImageInfo(m.Cols, m.Rows, EPixelLayout.Gray8), bytes);
    }

    /// <summary>已知平移（含忽略区）被恢复；空白区纹理不足、超限平移均不采用。</summary>
    [TestMethod]
    public void RecoversShiftAndRejectsUntrustworthyResults()
    {
        using var pattern = Pattern();
        using var moved = Shift(pattern, 4, -3);
        using var reference = Source(pattern);
        using var actual = Source(moved);
        var registrar = new OpenCvTranslationRegistrar();
        var bounds = new PixelBounds(30, 25, 170, 110);
        var mask = InspectionMask.Compose(
            reference,
            new Geometry[] { bounds.ToGeometry() },
            new Geometry[] { new PixelBounds(40, 30, 40, 30).ToGeometry() }
        );
        var result = registrar.Register(actual, reference, bounds, mask);
        Assert.IsTrue(result.Found, result.Reason);
        Assert.AreEqual(4, Math.Round(result.OffsetX));
        Assert.AreEqual(-3, Math.Round(result.OffsetY));

        using var blank = new Mat(160, 240, MatType.CV_8UC1, Scalar.All(230));
        using var blankSource = Source(blank);
        Assert.AreEqual("low_texture", registrar.Register(blankSource, blankSource, bounds).Reason);

        var strict = new TranslationRegistrationOptions(maximumShift: 2);
        var limited = registrar.Register(actual, reference, bounds, mask, strict);
        Assert.IsFalse(limited.Found);
        Assert.AreEqual("shift_limit", limited.Reason);
    }

    /// <summary>掩码图：范围内属于Region的像素为255；矩形几何恰好覆盖整数范围；交集为空时为null。</summary>
    [TestMethod]
    public void RendersMaskImage()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(20, 10, EPixelLayout.Gray8), new byte[200]);
        var region = InspectionMask.Compose(
            image,
            Array.Empty<Geometry>(),
            new Geometry[] { new PixelBounds(2, 1, 3, 2).ToGeometry() }
        );
        Assert.AreEqual(200 - 6, region.AreaPixels);
        using var mask = InspectionMask.ToImage(region, new PixelBounds(1, 1, 5, 3));
        var bytes = new byte[15];
        mask.CopyTo(0, bytes, 0, bytes.Length);
        CollectionAssert.AreEqual(
            new byte[] { 255, 0, 0, 0, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255, 255 },
            bytes
        );
        Assert.IsNull(new PixelBounds(0, 0, 2, 2).Intersect(new PixelBounds(2, 0, 2, 2)));
        Assert.AreEqual(
            new PixelBounds(1, 1, 1, 1),
            new PixelBounds(0, 0, 2, 2).Intersect(new PixelBounds(1, 1, 5, 5))
        );
    }
}
