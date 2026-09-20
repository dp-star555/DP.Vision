using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>通用的可移植印刷检查操作；实现通过专用接口声明支持的码族。</summary>
public interface IBarcodeQualityInspector
{
    /// <summary>该实现是否要求在质量分析前成功读取码结构。</summary>
    bool RequiresDecodedStructure { get; }

    /// <summary>检查原始像素，可使用解码器结构；不根据解码内容重新生成标准码图。</summary>
    /// <param name = "frame">借用的原始图像，不修改或接管像素。</param>
    /// <param name = "bounds">原图整数检查范围。</param>
    /// <param name = "symbols">已有解码观测；不依赖解码的实现可接收空集合。</param>
    /// <param name = "options">局部印刷阈值及可选检查开关。</param>
    /// <param name = "token">协作式取消标记。</param>
    BarcodeQualityResult Inspect(
        IImageSource frame,
        PixelBounds bounds,
        IReadOnlyList<BarcodeObservation> symbols,
        BarcodePrintOptions options,
        CancellationToken token = default
    );
}
