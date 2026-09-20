using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.Vision.Algorithms.Tests;

/// <summary>真实文件解码、Blob精确Region和颜色语义回归。</summary>
[TestClass]
public sealed class FileBlobColorTests
{
    /// <summary>真实PNG保持BGR颜色和奇数宽行跨度，源Mat释放不影响输出。</summary>
    [TestMethod]
    public async Task Png_FileToBlobAndColor()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            using (var pixels = new Mat(3, 3, MatType.CV_8UC3, new Scalar(0, 0, 255)))
            {
                pixels.Set(1, 1, new Vec3b(0, 0, 0));
                File.WriteAllBytes(path, pixels.ToBytes(".png"));
            }
            using var image = await new OpenCvImageFileReader().ReadAsync(path);
            using var frame = new ImageFrame("file", image);
            var blobs = new OpenCvBlobAnalyzer().Analyze(
                frame,
                new PixelBounds(0, 0, 3, 3),
                new BlobOptions(0, 0)
            );
            Assert.AreEqual(1, blobs.Count);
            Assert.AreEqual(1L, blobs.Blobs[0].Area);
            Assert.AreEqual(1.5, blobs.Blobs[0].Centroid.X);
            var color = new RgbColorAnalyzer().Analyze(frame, new PixelBounds(0, 0, 3, 3));
            Assert.AreEqual(255d * 8 / 9, color.Red, 1e-9);
            Assert.AreEqual(0d, color.Blue);
            Assert.AreEqual(blobs.FrameId, color.FrameId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Region保留孔洞和原图偏移，面积过滤不会变成产品失败。</summary>
    [TestMethod]
    public void Blob_PreservesHoleAndOriginalCoordinates()
    {
        var bytes = new byte[25];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = 255;
        for (int y = 1; y < 4; y++)
        for (int x = 1; x < 4; x++)
            bytes[y * 5 + x] = 0;
        bytes[12] = 255;
        using var image = VisionImage.CopyFrom(new ImageInfo(5, 5, EPixelLayout.Gray8), bytes);
        using var frame = new ImageFrame("ring", image);
        var analyzer = new OpenCvBlobAnalyzer();
        var result = analyzer.Analyze(frame, new PixelBounds(1, 1, 3, 3), new BlobOptions(0, 0));
        Assert.AreEqual(8L, result.Blobs[0].Area);
        Assert.IsFalse(result.Blobs[0].Region.Contains(new PointD(2.5, 2.5)));
        Assert.IsTrue(result.Blobs[0].Region.Contains(new PointD(1.5, 1.5)));
        var empty = analyzer.Analyze(frame, new PixelBounds(1, 1, 3, 3), new BlobOptions(0, 0, 9));
        Assert.AreEqual(0, empty.Count);
        Assert.AreEqual(EAlgorithmStatus.Completed, empty.Status);
    }

    /// <summary>四连通与八连通有显式区别，灰度区间两端均包含。</summary>
    [TestMethod]
    public void Blob_ConnectivityAndThresholdEndpoints()
    {
        using var image = VisionImage.CopyFrom(
            new ImageInfo(2, 2, EPixelLayout.Gray8),
            new byte[] { 10, 255, 255, 20 }
        );
        using var frame = new ImageFrame("diagonal", image);
        var analyzer = new OpenCvBlobAnalyzer();
        Assert.AreEqual(
            2,
            analyzer.Analyze(frame, new PixelBounds(0, 0, 2, 2), new BlobOptions(10, 20, 1, false)).Count
        );
        Assert.AreEqual(
            1,
            analyzer.Analyze(frame, new PixelBounds(0, 0, 2, 2), new BlobOptions(10, 20)).Count
        );
    }

    /// <summary>RGB/BGR/Alpha布局均产生明确RGB均值，Alpha不加权。</summary>
    /// <param name="layout">输入像素布局。</param>
    [TestMethod]
    [DataRow(EPixelLayout.Rgb24)]
    [DataRow(EPixelLayout.Bgr24)]
    [DataRow(EPixelLayout.Rgba32)]
    [DataRow(EPixelLayout.Bgra32)]
    public void Color_ExplicitChannelOrder(EPixelLayout layout)
    {
        var info = new ImageInfo(1, 1, layout);
        var bytes = new byte[info.ByteLength];
        bool rgb = layout == EPixelLayout.Rgb24 || layout == EPixelLayout.Rgba32;
        bytes[rgb ? 0 : 2] = 200;
        bytes[1] = 100;
        bytes[rgb ? 2 : 0] = 50;
        using var image = VisionImage.CopyFrom(info, bytes);
        using var frame = new ImageFrame("color", image);
        var result = new RgbColorAnalyzer().Analyze(frame, new PixelBounds(0, 0, 1, 1));
        Assert.AreEqual(200d, result.Red);
        Assert.AreEqual(100d, result.Green);
        Assert.AreEqual(50d, result.Blue);
    }

    /// <summary>不支持格式、越界和取消不会发布伪成功结果。</summary>
    [TestMethod]
    public void InvalidInputAndCancellation_AreRejected()
    {
        using var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray16), new byte[] { 0, 1 });
        using var frame = new ImageFrame("gray16", image);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            new RgbColorAnalyzer().Analyze(frame, new PixelBounds(0, 0, 1, 1))
        );
        Assert.ThrowsExactly<NotSupportedException>(() =>
            new OpenCvBlobAnalyzer().Analyze(frame, new PixelBounds(0, 0, 1, 1), new BlobOptions())
        );
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new RgbColorAnalyzer().Analyze(frame, new PixelBounds(0, 0, 2, 1))
        );
        using var gray8 = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[] { 10 });
        using var valid = new ImageFrame("gray8", gray8);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new OpenCvBlobAnalyzer().Analyze(
                valid,
                new PixelBounds(0, 0, 1, 1),
                new BlobOptions(),
                new CancellationToken(true)
            )
        );
    }

    /// <summary>16位PNG文件保留原始位深；坏文件和文件预算明确拒绝。</summary>
    [TestMethod]
    public async Task Reader_PreservesDepthAndRejectsCorruptionAndBudget()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            using (var pixels = new Mat(1, 1, MatType.CV_16UC1, new Scalar(1024)))
                File.WriteAllBytes(path, pixels.ToBytes(".png"));
            using var image = await new OpenCvImageFileReader().ReadAsync(path);
            Assert.AreEqual(EPixelLayout.Gray16, image.Info.Layout);
            var bytes = new byte[2];
            image.CopyTo(0, bytes, 0, 2);
            Assert.AreEqual((ushort)1024, BitConverter.ToUInt16(bytes, 0));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new OpenCvImageFileReader(1).ReadAsync(path)
            );
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                new OpenCvImageFileReader().ReadAsync(path)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>保留帧在源句柄释放后仍有效，最终释放后读操作失败。</summary>
    [TestMethod]
    public void Frame_LeaseOwnership()
    {
        var image = VisionImage.CopyFrom(new ImageInfo(1, 1, EPixelLayout.Gray8), new byte[] { 123 });
        var frame = new ImageFrame("lease", image);
        using var retained = frame.Retain();
        image.Dispose();
        frame.Dispose();
        var color = new RgbColorAnalyzer().Analyze(retained, new PixelBounds(0, 0, 1, 1));
        Assert.AreEqual(123d, color.Red);
        retained.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => retained.Retain());
    }
}
