using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>支持在原图上用精确像素区域限制读码的可选能力；不要求调用方先将斜ROI重采样。</summary>
public interface IMaskedBarcodeReader : IBarcodeReader
{
    /// <summary>在搜索矩形和原图掩膜的交集中读取；掩膜外像素不参与解码。具体实现须声明是否支持掩膜模式下的修复预处理。</summary>
    /// <param name="frame">借用的原始图像，调用结束前保持有效。</param>
    /// <param name="bounds">轴对齐的搜索范围，不是码的真实轮廓。</param>
    /// <param name="regionMask">原图坐标的精确包含区域；空区域不可读取。</param>
    /// <param name="token">协作式取消标记。</param>
    /// <returns>实际读出的码与显式完成状态。</returns>
    BarcodeReadResult Read(IImageSource frame, PixelBounds bounds, RegionGeometry regionMask, CancellationToken token = default);
}
