using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>测量模型；线拟合为正交最小二乘，圆拟合为代数最小二乘。</summary>
public enum EEdgeModel
{
    /// <summary>直线。</summary>
    Line = 0,
    /// <summary>圆。</summary>
    Circle = 1
}

/// <summary>边缘提取与拟合参数，不隐式删除离群点。</summary>
public sealed class EdgeMeasurementOptions
{
    /// <summary>创建Canny阈值与最小证据要求。</summary>
    /// <param name="model">拟合模型。</param>
    /// <param name="lowThreshold">Canny低阈值。</param>
    /// <param name="highThreshold">Canny高阈值，不低于低阈值。</param>
    /// <param name="minimumPoints">至少三个边缘点。</param>
    public EdgeMeasurementOptions(EEdgeModel model, double lowThreshold = 50, double highThreshold = 100, int minimumPoints = 6)
    {
        if (!Enum.IsDefined(typeof(EEdgeModel), model) || double.IsNaN(lowThreshold) || double.IsNaN(highThreshold)
            || lowThreshold < 0 || highThreshold < lowThreshold || double.IsInfinity(highThreshold) || minimumPoints < 3)
            throw new ArgumentException("Invalid edge fitting options.");
        Model = model; LowThreshold = lowThreshold; HighThreshold = highThreshold; MinimumPoints = minimumPoints;
    }
    /// <summary>拟合模型。</summary>
    public EEdgeModel Model { get; }
    /// <summary>低阈值。</summary>
    public double LowThreshold { get; }
    /// <summary>高阈值。</summary>
    public double HighThreshold { get; }
    /// <summary>最小证据数量。</summary>
    public int MinimumPoints { get; }
}

/// <summary>原图像素边界坐标下的拟合事实，RMS单位为原图像素。</summary>
public sealed class EdgeMeasurementResult
{
    /// <summary>创建测量结果；A/B为线段端点，圆模型时A为圆心、B为圆上+X点。</summary>
    /// <param name="frameId">输入身份。</param>
    /// <param name="model">模型。</param>
    /// <param name="a">端点或圆心。</param>
    /// <param name="b">另一端点或圆上点。</param>
    /// <param name="radius">圆半径；线模型为0。</param>
    /// <param name="rms">正交/径向残差RMS。</param>
    /// <param name="pointCount">参与拟合的边缘点数。</param>
    public EdgeMeasurementResult(string frameId, EEdgeModel model, PointD a, PointD b, double radius, double rms, int pointCount)
    {
        if (string.IsNullOrWhiteSpace(frameId) || !Enum.IsDefined(typeof(EEdgeModel), model) || pointCount < 3
            || double.IsNaN(radius) || radius < 0 || double.IsInfinity(radius) || double.IsNaN(rms) || rms < 0 || double.IsInfinity(rms))
            throw new ArgumentException("Invalid measurement facts.");
        FrameId = frameId; Model = model; A = a; B = b; Radius = radius; RmsError = rms; PointCount = pointCount;
    }
    /// <summary>测量范围的定位来源；既有点保持原图坐标。</summary>
    public LocatedCoordinateSystem? CoordinateSystem { get; private set; }
    /// <summary>第一点的双坐标。</summary>
    public LocatedPoint? LocatedA => CoordinateSystem?.Locate(A);
    /// <summary>第二点的双坐标。</summary>
    public LocatedPoint? LocatedB => CoordinateSystem?.Locate(B);
    /// <summary>局部像素半径。</summary>
    public double? LocalRadius => CoordinateSystem == null ? (double?)null : Radius / CoordinateSystem.Pose.Scale;
    /// <summary>局部像素残差。</summary>
    public double? LocalRmsError => CoordinateSystem == null ? (double?)null : RmsError / CoordinateSystem.Pose.Scale;
    /// <summary>附加同帧定位来源，不重复转换原图点。</summary><param name="system">坐标系。</param><returns>独立结果。</returns>
    public EdgeMeasurementResult InCoordinates(LocatedCoordinateSystem system)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (system.FrameId != FrameId) throw new InvalidOperationException("Measurement frame mismatch.");
        var copy = (EdgeMeasurementResult)MemberwiseClone(); copy.CoordinateSystem = system; return copy;
    }
    /// <summary>原图身份。</summary>
    public string FrameId { get; }
    /// <summary>测量模型。</summary>
    public EEdgeModel Model { get; }
    /// <summary>第一端点或圆心。</summary>
    public PointD A { get; }
    /// <summary>第二端点或圆上点。</summary>
    public PointD B { get; }
    /// <summary>圆半径；线模型为0。</summary>
    public double Radius { get; }
    /// <summary>原图像素残差。</summary>
    public double RmsError { get; }
    /// <summary>边缘证据数量。</summary>
    public int PointCount { get; }
    /// <summary>完成不等于产品合格。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
}

/// <summary>从原图矩形中的实际边缘拟合线/圆，证据不足或退化时抛出明确错误。</summary>
public interface IEdgeMeasurer
{
    /// <summary>借用原图完成测量。</summary>
    /// <param name="frame">输入帧。</param>
    /// <param name="bounds">原图范围。</param>
    /// <param name="options">提取/拟合参数。</param>
    /// <param name="token">协作取消。</param>
    /// <param name="regionMask">精确原图范围；先提取实际边缘再筛选点，不把掩码边界生成伪边缘。</param>
    /// <returns>不持有图像的测量事实。</returns>
    EdgeMeasurementResult Measure(ImageFrame frame, PixelBounds bounds, EdgeMeasurementOptions options, CancellationToken token = default, RegionGeometry? regionMask = null);
}
