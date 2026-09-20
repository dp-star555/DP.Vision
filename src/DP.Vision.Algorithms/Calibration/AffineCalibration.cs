using System;

namespace DP.Vision.Algorithms;

/// <summary>不可变二维仿射标定事实；RMS是目标坐标单位的点距离误差，不是产品合格判定。</summary>
public sealed class AffineCalibration
{
    internal AffineCalibration(double m11, double m12, double tx, double m21, double m22, double ty,
        double rmsError, Coordinate2D? rotationCenter)
    {
        // 同时拒绝求解溢出，不能发布NaN或Infinity标定结果。
        _ = new Coordinate2D(m11, m12);
        _ = new Coordinate2D(m21, m22);
        _ = new Coordinate2D(tx, ty);
        _ = new Coordinate2D(rmsError, 0);
        M11 = m11; M12 = m12; Tx = tx;
        M21 = m21; M22 = m22; Ty = ty;
        RmsError = rmsError;
        RotationCenter = rotationCenter;
    }

    /// <summary>目标X的源X系数。</summary>
    public double M11 { get; }
    /// <summary>目标X的源Y系数。</summary>
    public double M12 { get; }
    /// <summary>目标X平移。</summary>
    public double Tx { get; }
    /// <summary>目标Y的源X系数。</summary>
    public double M21 { get; }
    /// <summary>目标Y的源Y系数。</summary>
    public double M22 { get; }
    /// <summary>目标Y平移。</summary>
    public double Ty { get; }
    /// <summary>所有点二维残差的均方根，单位与目标坐标相同。</summary>
    public double RmsError { get; }
    /// <summary>目标坐标系中的旋转中心；没有请求拟合时为空。</summary>
    public Coordinate2D? RotationCenter { get; }

    /// <summary>将源坐标映射到目标坐标，不自动执行半像素修正。</summary>
    /// <param name="point">源坐标。</param>
    /// <returns>目标坐标。</returns>
    public Coordinate2D Transform(Coordinate2D point) =>
        new Coordinate2D(M11 * point.X + M12 * point.Y + Tx, M21 * point.X + M22 * point.Y + Ty);

    /// <summary>映射后绕目标旋转中心旋转；正角从目标+X向+Y，Y向上时为逆时针，Y向下时为顺时针。</summary>
    /// <param name="point">源坐标。</param>
    /// <param name="angleRadians">有限旋转弧度；非零角必须有旋转中心。</param>
    /// <returns>旋转后的目标坐标。</returns>
    public Coordinate2D TransformWithRotation(Coordinate2D point, double angleRadians)
    {
        _ = new Coordinate2D(angleRadians, 0);
        var mapped = Transform(point);
        if (angleRadians == 0) return mapped;
        if (!RotationCenter.HasValue) throw new InvalidOperationException("非零旋转需要目标坐标旋转中心。");
        var center = RotationCenter.Value;
        double dx = mapped.X - center.X, dy = mapped.Y - center.Y;
        double cos = Math.Cos(angleRadians), sin = Math.Sin(angleRadians);
        return new Coordinate2D(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }
}
