using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>通用坐标构建入口；隐藏矩阵组合、同帧校验和退化拒绝。</summary>
public static class VisionCoordinateBuilder
{
    /// <summary>由原点、方向和原图像素/局部单位尺度构建。</summary>
    /// <param name="definition">定义。</param><param name="frame">本帧。</param><param name="origin">原图原点。</param>
    /// <param name="angleRadians">顺时针方向。</param><param name="scale">每局部单位对应的原图像素。</param><returns>本帧映射。</returns>
    public static VisionCoordinateSystem FromPose(VisionCoordinateDefinition definition, ImageFrame frame, PointD origin, double angleRadians = 0, double scale = 1) =>
        FromMatrix(definition, frame, PoseMatrix(origin, angleRadians, scale), "pose");
    /// <summary>建立姿态矩阵，也可作为局部到父坐标的固定关系。</summary>
    /// <param name="origin">父坐标中的局部原点。</param><param name="angleRadians">方向。</param><param name="scale">正尺度。</param><returns>矩阵。</returns>
    public static CoordinateMatrix2D PoseMatrix(PointD origin, double angleRadians, double scale)
    {
        VisionPoint.ValidatePosition(origin);
        if (double.IsNaN(angleRadians) || double.IsInfinity(angleRadians) || double.IsNaN(scale) || double.IsInfinity(scale) || scale < 1e-6 || scale > 1e6)
            throw new ArgumentException("方向必须有限，尺度须在1e-6..1e6。");
        double a = scale * Math.Cos(angleRadians), b = scale * Math.Sin(angleRadians);
        return CoordinateMatrix2D.FromAffine(a, -b, origin.X, b, a, origin.Y);
    }
    /// <summary>直接使用可逆矩阵，明确其局部到原图方向。</summary>
    /// <param name="definition">定义。</param><param name="frame">本帧。</param><param name="matrix">局部到原图。</param><param name="sourceIdentity">来源。</param><returns>本帧映射。</returns>
    public static VisionCoordinateSystem FromMatrix(VisionCoordinateDefinition definition, ImageFrame frame, CoordinateMatrix2D matrix, string sourceIdentity = "matrix")
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        return new VisionCoordinateSystem(definition, frame.FrameId, frame.Image.Info.Width, frame.Image.Info.Height, matrix, sourceIdentity);
    }
    /// <summary>原点及正X方向点构建；给定局部参考长度决定尺度。</summary>
    /// <param name="definition">定义。</param><param name="frame">本帧。</param><param name="origin">原点。</param><param name="xDirection">正X方向点。</param>
    /// <param name="referenceLength">两点在局部坐标中的已知距离。</param><returns>映射。</returns>
    public static VisionCoordinateSystem FromTwoPoints(VisionCoordinateDefinition definition, ImageFrame frame, VisionPoint origin, VisionPoint xDirection, double referenceLength)
    {
        ValidatePoint(origin, frame); ValidatePoint(xDirection, frame);
        if (double.IsNaN(referenceLength) || double.IsInfinity(referenceLength) || referenceLength <= 0) throw new ArgumentException("必须明确正参考长度。");
        double dx = xDirection.ImagePosition.X - origin.ImagePosition.X, dy = xDirection.ImagePosition.Y - origin.ImagePosition.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) throw new ArgumentException("重合点不能确定坐标轴。");
        return FromMatrix(definition, frame, PoseMatrix(origin.ImagePosition, Math.Atan2(dy, dx), length / referenceLength), "two-points");
    }
    /// <summary>用非平行线交点作原点；第一线A→B定义正X方向，尺度明确指定。</summary>
    /// <param name="definition">定义。</param><param name="frame">本帧。</param><param name="xAxis">正X线。</param><param name="crossLine">交线。</param><param name="scale">像素/局部单位。</param><returns>映射。</returns>
    public static VisionCoordinateSystem FromLines(VisionCoordinateDefinition definition, ImageFrame frame, VisionLine xAxis, VisionLine crossLine, double scale = 1)
    {
        if (xAxis == null || crossLine == null) throw new ArgumentNullException(nameof(xAxis));
        ValidatePoint(xAxis.A, frame); ValidatePoint(xAxis.B, frame); ValidatePoint(crossLine.A, frame); ValidatePoint(crossLine.B, frame);
        var p = xAxis.A.ImagePosition; var q = crossLine.A.ImagePosition;
        double rx = xAxis.B.ImagePosition.X - p.X, ry = xAxis.B.ImagePosition.Y - p.Y;
        double sx = crossLine.B.ImagePosition.X - q.X, sy = crossLine.B.ImagePosition.Y - q.Y;
        double cross = rx * sy - ry * sx;
        if (Math.Abs(cross) <= 1e-10 * Math.Sqrt((rx * rx + ry * ry) * (sx * sx + sy * sy))) throw new ArgumentException("平行或近乎平行直线不能稳定构建原点。");
        double t = ((q.X - p.X) * sy - (q.Y - p.Y) * sx) / cross;
        return FromMatrix(definition, frame, PoseMatrix(new PointD(p.X + t * rx, p.Y + t * ry), Math.Atan2(ry, rx), scale), "line-intersection");
    }
    /// <summary>业务局部到父坐标的固定映射组合到本帧；不重复乘定位矩阵。</summary>
    /// <param name="definition">业务定义。</param><param name="frame">本帧。</param><param name="parent">父坐标本帧映射。</param><param name="localToParent">业务到父。</param><returns>业务到原图。</returns>
    public static VisionCoordinateSystem FromParent(VisionCoordinateDefinition definition, ImageFrame frame, VisionCoordinateSystem parent, CoordinateMatrix2D localToParent)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent)); parent.ValidateFrame(frame);
        return FromMatrix(definition, frame, parent.LocalToImage.Multiply(localToParent), "parent:" + parent.Definition.Id + ":" + parent.SourceIdentity);
    }
    /// <summary>局部→原图对应点求解仿射；输出完整残差供构建节点阈值检查。</summary>
    /// <param name="definition">定义。</param><param name="frame">本帧。</param><param name="samples">局部源点/原图目标点。</param><param name="rms">原图像素RMS。</param><param name="token">取消。</param><returns>本帧映射。</returns>
    public static VisionCoordinateSystem FromCorrespondences(VisionCoordinateDefinition definition, ImageFrame frame, IEnumerable<CalibrationSample> samples, out double rms, CancellationToken token = default)
    {
        var points = (samples ?? throw new ArgumentNullException(nameof(samples))).Take(1025).ToArray();
        if (points.Length > 1024) throw new ArgumentException("标定点预算超限。");
        var calibration = CalibrationSolver.SolveAffine(points, token: token); rms = calibration.RmsError;
        return FromMatrix(definition, frame, CoordinateMatrix2D.FromAffine(calibration.M11, calibration.M12, calibration.Tx, calibration.M21, calibration.M22, calibration.Ty), "correspondences");
    }
    private static void ValidatePoint(VisionPoint point, ImageFrame frame)
    {
        if (point == null || frame == null) throw new ArgumentNullException(nameof(point));
        if (point.FrameId != frame.FrameId) throw new InvalidOperationException("构建坐标的点必须来自本帧。");
        point.CoordinateSystem?.ValidateFrame(frame);
    }
}
