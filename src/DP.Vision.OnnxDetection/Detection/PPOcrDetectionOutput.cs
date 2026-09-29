using System;

namespace DP.Vision.OnnxDetection;

/// <summary>脱离推理会话的DB概率图快照；不含候选框，也不含业务阈值。</summary>
/// <remarks>概率图与模型身份一起构成检测证据；候选提取与筛选由业务侧负责。</remarks>
public sealed class PPOcrDetectionOutput
{
    /// <summary>概率图像素数上限，超出即拒绝，避免无界内存。</summary>
    public const long MaxProbabilityCount = 4_000_000;

    private readonly float[] _probabilities;

    /// <summary>复制概率图与几何映射，并校验数值范围。</summary>
    /// <param name = "modelSha256">实际加载模型字节的SHA256。</param>
    /// <param name = "width">概率图宽度，等于模型输入宽度。</param>
    /// <param name = "height">概率图高度，等于模型输入高度。</param>
    /// <param name = "imageWidth">缩放前原图宽度。</param>
    /// <param name = "imageHeight">缩放前原图高度。</param>
    /// <param name = "probabilities">行主序概率值，长度为width×height，取值[0,1]。</param>
    public PPOcrDetectionOutput(
        string modelSha256,
        int width,
        int height,
        int imageWidth,
        int imageHeight,
        float[] probabilities
    )
    {
        if (string.IsNullOrEmpty(modelSha256))
        {
            throw new ArgumentException("Model identity required.", nameof(modelSha256));
        }

        if (width < 1 || height < 1 || imageWidth < 1 || imageHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (probabilities == null)
        {
            throw new ArgumentNullException(nameof(probabilities));
        }

        if ((long)width * height > MaxProbabilityCount)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Probability map exceeds the supported budget.");
        }

        if (probabilities.Length != (long)width * height)
        {
            throw new ArgumentException("Probability map size mismatch.", nameof(probabilities));
        }

        for (int i = 0; i < probabilities.Length; i++)
        {
            float value = probabilities[i];
            if (float.IsNaN(value) || value < 0 || value > 1)
            {
                throw new ArgumentException("Invalid DB probability.", nameof(probabilities));
            }
        }

        ModelSha256 = modelSha256;
        Width = width;
        Height = height;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        _probabilities = (float[])probabilities.Clone();
    }

    /// <summary>实际加载模型字节的SHA256。</summary>
    public string ModelSha256 { get; }

    /// <summary>概率图宽度，等于模型输入宽度。</summary>
    public int Width { get; }

    /// <summary>概率图高度，等于模型输入高度。</summary>
    public int Height { get; }

    /// <summary>缩放前原图宽度。</summary>
    public int ImageWidth { get; }

    /// <summary>缩放前原图高度。</summary>
    public int ImageHeight { get; }

    /// <summary>导出独立的行主序概率副本。</summary>
    /// <returns>与推理会话无关的概率值副本。</returns>
    public float[] CopyValues()
    {
        return (float[])_probabilities.Clone();
    }
}
