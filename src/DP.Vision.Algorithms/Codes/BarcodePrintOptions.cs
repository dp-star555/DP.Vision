using System;

namespace DP.Vision.Algorithms;

/// <summary>局部一维码及QR印刷检查，与内容解码和ISO等级验证分离。</summary>
public sealed class BarcodePrintOptions
{
    /// <summary>创建原图像素及条、空隙、QR模块级缺陷阈值；具体值需要按印刷工艺标定。</summary>
    /// <param name = "enabled">是否要求独立印刷检查。</param>
    /// <param name = "minimumArea">最小缺陷面积，范围1–1000000，单位为原图平方像素。</param>
    /// <param name = "minimumFraction">缺陷占局部条、空隙或模块内部的最小比例，范围0–1。</param>
    /// <param name = "edgeTolerance">忽略的边缘宽度，范围0–8，单位为原图像素，算法会限制实际使用宽度。</param>
    /// <param name = "checkQrQuietZone">是否检查QR四模块静区，默认关闭。</param>
    /// <param name = "detectInkLoss">是否检测一维墨迹灰度衰减，null使用true。</param>
    /// <param name = "minimumInkLoss">相对可用对比度的最小灰度衰减比例，大于0且不超过1，null使用0.25。</param>
    public BarcodePrintOptions(
        bool enabled = true,
        int minimumArea = 4,
        double minimumFraction = .01,
        int edgeTolerance = 1,
        bool checkQrQuietZone = false,
        bool? detectInkLoss = null,
        double? minimumInkLoss = null
    )
    {
        if (
            minimumArea < 1
            || minimumArea > 1000000
            || double.IsNaN(minimumFraction)
            || minimumFraction < 0
            || minimumFraction > 1
            || edgeTolerance < 0
            || edgeTolerance > 8
        )
        {
            throw new ArgumentException("Invalid barcode print thresholds.");
        }

        double loss = minimumInkLoss ?? .25;
        if (double.IsNaN(loss) || loss <= 0 || loss > 1)
        {
            throw new ArgumentException("Invalid ink-loss contrast fraction.");
        }

        DetectInkLoss = detectInkLoss ?? true;
        MinimumInkLoss = loss;
        Enabled = enabled;
        MinimumArea = minimumArea;
        MinimumFraction = minimumFraction;
        EdgeTolerance = edgeTolerance;
        CheckQrQuietZone = checkQrQuietZone;
    }

    /// <summary>是否在二值孔洞检查之外检测一维条纹的局部灰度衰减。</summary>
    public bool DetectInkLoss { get; }

    /// <summary>相对于每列深色参考的最小灰度增量除以ROI可用对比度；需要工艺标定。</summary>
    public double MinimumInkLoss { get; }

    /// <summary>是否启用QR四模块静区检查；ROI必须包含完整静区。</summary>
    public bool CheckQrQuietZone { get; }

    /// <summary>是否独立于解码要求执行印刷检查。</summary>
    public bool Enabled { get; }

    /// <summary>最小原图缺陷面积：一维码按连通域，QR按单模块内部差异总面积计算。</summary>
    public int MinimumArea { get; }

    /// <summary>最小缺陷面积占所检查条、空隙或QR模块内部的比例，不是占整个ROI的比例。</summary>
    public double MinimumFraction { get; }

    /// <summary>忽略的边缘宽度；内部会限制该值，以保留窄条、窄空隙及模块内部。</summary>
    public int EdgeTolerance { get; }
}
