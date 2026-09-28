using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>在定位坐标系中定义读码区域，同时保留原图像素及原图坐标证据。</summary>
public static class LocatedBarcodeReading
{
    /// <summary>将局部包含/排除形状准确映射到当前帧，以精确原图掩膜限定读码；不预先旋转或插值原图。</summary>
    /// <param name="reader">支持精确区域的读码器。</param>
    /// <param name="frame">带身份的当前原图租约。</param>
    /// <param name="coordinates">同一帧成功定位得到的坐标系。</param>
    /// <param name="include">在模板局部坐标中的包含形状。</param>
    /// <param name="exclude">在模板局部坐标中的排除形状。</param>
    /// <param name="token">协作式取消标记。</param>
    /// <returns>原图坐标的读取证据；空区域返回未取得证据，不回退到整图。</returns>
    public static BarcodeReadResult ReadLocated(
        this IMaskedBarcodeReader reader,
        ImageFrame frame,
        LocatedCoordinateSystem coordinates,
        IEnumerable<Geometry> include,
        IEnumerable<Geometry> exclude,
        CancellationToken token = default)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (coordinates == null) throw new ArgumentNullException(nameof(coordinates));
        var region = coordinates.ResolveRegion(frame, include, exclude, token);
        if (region.AreaPixels == 0) return new BarcodeReadResult(Array.Empty<BarcodeObservation>());
        var rect = region.Bounds;
        var bounds = new PixelBounds(checked((int)rect.X), checked((int)rect.Y),
            checked((int)rect.Width), checked((int)rect.Height));
        return reader.Read(frame.Image, bounds, region, token);
    }
}
