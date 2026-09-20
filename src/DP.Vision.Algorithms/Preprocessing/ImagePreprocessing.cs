using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>显式整图预处理；除Grayscale外不隐式把彩色转换为灰度。</summary>
public enum EImagePreprocessing
{
    /// <summary>8位RGB/BGR/Alpha转Gray8，Alpha不加权。</summary>
    Grayscale,
    /// <summary>Gray8反相。</summary>
    Invert,
    /// <summary>Gray8 Gaussian，边界Reflect101。</summary>
    Gaussian,
    /// <summary>Gray8中值，边界Replicate。</summary>
    Median,
    /// <summary>Gray8固定增益/偏置，饱和到0..255。</summary>
    GainOffset,
    /// <summary>Gray16固定增益/偏置到Gray8，非自动拉伸。</summary>
    Gray16ToGray8
}

/// <summary>不可变预处理配置。</summary>
public sealed class ImagePreprocessingOptions
{
    /// <summary>创建参数；核大小为3..63奇数，Gaussian sigma显式为正。</summary>
    /// <param name="operation">操作。</param><param name="kernelSize">核边长。</param>
    /// <param name="sigma">Gaussian标准差，像素。</param><param name="gain">非负固定增益。</param>
    /// <param name="offset">固定偏置。</param>
    public ImagePreprocessingOptions(EImagePreprocessing operation, int kernelSize = 3, double sigma = 1, double gain = 1, double offset = 0)
    {
        if (!Enum.IsDefined(typeof(EImagePreprocessing), operation) || kernelSize < 3 || kernelSize > 63 || kernelSize % 2 == 0
            || !Finite(sigma) || sigma <= 0 || !Finite(gain) || gain < 0 || gain > 65536 || !Finite(offset) || Math.Abs(offset) > 65536)
            throw new ArgumentOutOfRangeException(nameof(operation));
        Operation = operation; KernelSize = kernelSize; Sigma = sigma; Gain = gain; Offset = offset;
    }
    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    /// <summary>操作。</summary>
    public EImagePreprocessing Operation { get; }
    /// <summary>核边长。</summary>
    public int KernelSize { get; }
    /// <summary>Gaussian sigma。</summary>
    public double Sigma { get; }
    /// <summary>固定增益。</summary>
    public double Gain { get; }
    /// <summary>固定偏置。</summary>
    public double Offset { get; }
}

/// <summary>不改变尺寸/坐标系的显式像素操作。</summary>
public interface IImagePreprocessor
{
    /// <summary>借用输入，返回调用者拥有的新像素源；包装帧时必须产生新FrameId。</summary>
    /// <param name="image">借用图像。</param><param name="options">配置。</param><param name="token">取消。</param>
    /// <returns>独立Gray8图像。</returns>
    IImageSource Process(IImageSource image, ImagePreprocessingOptions options, CancellationToken token = default);
}
