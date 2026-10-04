using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>带坐标归属的基础几何测量。</summary>
[VisionCapability("measurement.geometry", "测量", "几何直线与距离")]
public interface IGeometryMeasurer
{
    /// <summary>用同帧、同坐标来源的两点生成直线。</summary>
    /// <param name="a">第一点。</param><param name="b">第二点。</param><param name="token">取消。</param><returns>非退化直线。</returns>
    VisionLine GenerateLine(VisionPoint a, VisionPoint b, CancellationToken token = default);
    /// <summary>测量两同帧、同坐标来源点的距离。</summary>
    /// <param name="a">第一点。</param><param name="b">第二点。</param><param name="space">空间。</param><param name="token">取消。</param><returns>带单位距离。</returns>
    GeometricDistanceResult PointToPoint(VisionPoint a, VisionPoint b, EVisionCoordinateSpace space = EVisionCoordinateSpace.Image, CancellationToken token = default);
    /// <summary>测量点到无限直线或有限线段，返回实际投影证据。</summary>
    /// <param name="point">点。</param><param name="line">直线。</param><param name="space">测量空间。</param><param name="mode">直线或线段。</param><param name="token">取消。</param><returns>距离与最近点。</returns>
    GeometricDistanceResult PointToLine(VisionPoint point, VisionLine line, EVisionCoordinateSpace space = EVisionCoordinateSpace.Image,
        EVisionLineDistanceMode mode = EVisionLineDistanceMode.InfiniteLines, CancellationToken token = default);
    /// <summary>测量两条无限直线或有限线段的最短距离。</summary>
    /// <param name="a">第一条。</param><param name="b">第二条。</param><param name="space">测量空间。</param><param name="mode">直线或线段。</param><param name="token">取消。</param><returns>距离与最近点。</returns>
    GeometricDistanceResult LineToLine(VisionLine a, VisionLine b, EVisionCoordinateSpace space = EVisionCoordinateSpace.Image,
        EVisionLineDistanceMode mode = EVisionLineDistanceMode.InfiniteLines, CancellationToken token = default);
}
