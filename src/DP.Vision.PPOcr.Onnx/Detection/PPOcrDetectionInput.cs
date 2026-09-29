using System;
using System.Linq;

namespace DP.Vision.PPOcr.Onnx;

/// <summary>调用者准备好的DB检测模型输入；不隐含缩放，也不携带候选策略。</summary>
/// <remarks>维度、通道顺序与数值有限性由模型执行端校验；归一化与缩放由调用者负责。</remarks>
public sealed class PPOcrDetectionInput
{
    private readonly float[] _values;

    /// <summary>复制通道优先的连续输入及显式几何映射。</summary>
    /// <param name = "width">模型输入宽度，须与张量宽度一致。</param>
    /// <param name = "height">模型输入高度，须与张量高度一致。</param>
    /// <param name = "imageWidth">缩放前原图宽度，用于把模型坐标映射回原图。</param>
    /// <param name = "imageHeight">缩放前原图高度。</param>
    /// <param name = "values">通道优先的连续浮点值，长度为3×height×width。</param>
    public PPOcrDetectionInput(int width, int height, int imageWidth, int imageHeight, float[] values)
    {
        if (width < 1 || height < 1 || imageWidth < 1 || imageHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        if (values.Length != 3L * width * height || values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
        {
            throw new ArgumentException("Invalid detector input tensor.", nameof(values));
        }

        Width = width;
        Height = height;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        _values = (float[])values.Clone();
    }

    /// <summary>模型输入宽度。</summary>
    public int Width { get; }

    /// <summary>模型输入高度。</summary>
    public int Height { get; }

    /// <summary>缩放前原图宽度。</summary>
    public int ImageWidth { get; }

    /// <summary>缩放前原图高度。</summary>
    public int ImageHeight { get; }

    /// <summary>导出独立的NCHW缓冲区。</summary>
    /// <returns>归一化数值副本。</returns>
    public float[] CopyValues()
    {
        return (float[])_values.Clone();
    }
}
