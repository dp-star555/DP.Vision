namespace DP.Vision.Algorithms.Tests;

/// <summary>测试用坐标系：原点在模板左上角、轴沿模板轴、单位为模板像素。</summary>
internal static class TestCoordinates
{
    internal static VisionCoordinateSystem FromPose(string id, string frameId, int width, int height, TemplatePoseTransform pose)
    {
        var origin = pose.ToImage(new Coordinate2D(0, 0));
        return new VisionCoordinateSystem(new VisionCoordinateDefinition(id, id), frameId, width, height,
            VisionCoordinateBuilder.PoseMatrix(new PointD(origin.X, origin.Y), pose.AngleRadians, pose.Scale), "test");
    }
}
