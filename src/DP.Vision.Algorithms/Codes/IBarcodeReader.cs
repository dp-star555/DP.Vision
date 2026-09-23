using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>独立的码数据读取接口。</summary>
public interface IBarcodeReader
{
    /// <summary>借用原始像素进行读取；返回前调用方必须保持输入有效。</summary>
    /// <param name = "frame">借用的只读原始图像，返回前必须保持租约有效。</param>
    /// <param name = "bounds">位于原图内的整数检查范围。</param>
    /// <param name = "token">协作式取消标记。</param>
    BarcodeReadResult Read(IImageSource frame, PixelBounds bounds, CancellationToken token = default);
}
