using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>图块按整数设备边界绘制：任意小数缩放和平移下，相邻图块首尾相接，不留背景细缝。</summary>
[TestClass]
public sealed class CanvasTileSeamTests
{
    /// <summary>适配窗口后再缩放、平移到小数位置，同行同列相邻图块的设备边界必须相等，且整体覆盖连续。</summary>
    [TestMethod]
    [DataRow(1.0, 0.0, 0.0)]
    [DataRow(1.37, 0.31, 0.77)]
    [DataRow(0.73, -12.4, 5.6)]
    [DataRow(2.91, 3.49, -0.51)]
    [DataRow(0.6131, 0.5, 0.5)]
    public void AdjacentTilesShareDeviceEdges(double zoom, double panX, double panY)
    {
        var image = new ImageInfo(2448, 2048, EPixelLayout.Gray8);
        var view = new CanvasViewport();
        view.Fit(image.Width, image.Height, 1433.7, 487.3);
        view.Zoom(zoom, new PointD(611.3, 233.9));
        view.Pan(panX, panY);
        var tiles = CanvasPlanning.Tiles(image, view, 1433.7, 487.3, 256);
        Assert.IsTrue(tiles.Count > 1);
        var bounds = tiles.ToDictionary(t => (t.X, t.Y), t => CanvasPlanning.DeviceBounds(t.Bounds, view));
        foreach (var tile in tiles)
        {
            var current = bounds[(tile.X, tile.Y)];
            if (bounds.TryGetValue((tile.X + 1, tile.Y), out var right))
            {
                Assert.AreEqual(current.Right, right.Left);
                Assert.AreEqual(current.Top, right.Top); Assert.AreEqual(current.Bottom, right.Bottom);
            }
            if (bounds.TryGetValue((tile.X, tile.Y + 1), out var below))
            {
                Assert.AreEqual(current.Bottom, below.Top);
                Assert.AreEqual(current.Left, below.Left); Assert.AreEqual(current.Right, below.Right);
            }
        }
    }

    /// <summary>设备边界与小数屏幕位置相差不超过半个像素。</summary>
    [TestMethod]
    public void DeviceBoundsStayWithinHalfPixel()
    {
        var view = new CanvasViewport();
        view.Fit(1000, 800, 733.3, 611.7);
        var tile = new RectD(256, 512, 256, 256);
        var (left, top, right, bottom) = CanvasPlanning.DeviceBounds(tile, view);
        Assert.IsTrue(Math.Abs(left - (view.Origin.X + tile.X * view.Scale)) <= .5);
        Assert.IsTrue(Math.Abs(top - (view.Origin.Y + tile.Y * view.Scale)) <= .5);
        Assert.IsTrue(Math.Abs(right - (view.Origin.X + tile.Right * view.Scale)) <= .5);
        Assert.IsTrue(Math.Abs(bottom - (view.Origin.Y + tile.Bottom * view.Scale)) <= .5);
        Assert.ThrowsExactly<ArgumentNullException>(() => CanvasPlanning.DeviceBounds(tile, null!));
    }
}
