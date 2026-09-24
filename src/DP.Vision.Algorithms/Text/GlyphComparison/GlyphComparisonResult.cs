using System;

namespace DP.Vision.Algorithms;

/// <summary>拥有独立图像租约的归一化证据；需释放本结果，直接取得的图像属性只作借用。</summary>
public sealed class GlyphComparisonResult : IDisposable
{
    /// <summary>保留所传图像的独立租约。完成结果必须有三张证据图；未完成必须给原因，不改变调用方原有租约。</summary>
    /// <param name = "status">显式完成状态，不能用空缺陷列表代替。</param>
    /// <param name = "reasonCode">未完成原因码；成功时可为空字符串。</param>
    /// <param name = "difference">归一化缺墨与多墨之和除以参考墨迹面积，必须是有限非负数。</param>
    /// <param name = "missing">缺墨的归一化像素数，非负。</param>
    /// <param name = "extra">多墨的归一化像素数，非负。</param>
    /// <param name = "actual">已对齐的实际字证据；构造时Retain。</param>
    /// <param name = "reference">归一化参考字证据；构造时Retain。</param>
    /// <param name = "delta">容差过滤后的差异证据图；构造时Retain。</param>
    public GlyphComparisonResult(
        EAlgorithmStatus status,
        string reasonCode,
        double difference,
        int missing,
        int extra,
        IImageSource? actual = null,
        IImageSource? reference = null,
        IImageSource? delta = null
    )
    {
        if (!Enum.IsDefined(typeof(EAlgorithmStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (reasonCode == null)
        {
            throw new ArgumentNullException(nameof(reasonCode));
        }

        if (
            double.IsNaN(difference)
            || double.IsInfinity(difference)
            || difference < 0
            || missing < 0
            || extra < 0
        )
        {
            throw new ArgumentException("Invalid normalized measurement.");
        }

        if (status == EAlgorithmStatus.Completed && (actual == null || reference == null || delta == null))
        {
            throw new ArgumentException("Completed comparison requires all evidence images.");
        }

        if (status != EAlgorithmStatus.Completed && string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new ArgumentException("Missing failure reason.");
        }

        Status = status;
        ReasonCode = reasonCode;
        Difference = difference;
        Missing = missing;
        Extra = extra;
        try
        {
            Actual = actual?.Retain();
            Reference = reference?.Retain();
            Delta = delta?.Retain();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>显式的算法完成状态。</summary>
    public EAlgorithmStatus Status { get; }

    /// <summary>稳定的阻断原因码，成功时为空。</summary>
    public string ReasonCode { get; }

    /// <summary>缺墨与多墨之和除以参考墨迹面积；不是原图尺寸误差。</summary>
    public double Difference { get; }

    /// <summary>缺墨的归一化像素数。</summary>
    public int Missing { get; }

    /// <summary>多墨的归一化像素数。</summary>
    public int Extra { get; }

    /// <summary>借用的实际字对齐证据；如需超出本结果生命周期，必须另行Retain。</summary>
    public IImageSource? Actual { get; }

    /// <summary>借用的归一化参考证据。</summary>
    public IImageSource? Reference { get; }

    /// <summary>借用的容差过滤彩色差异图，不是未经处理的直接像素差分。</summary>
    public IImageSource? Delta { get; }

    /// <summary>幂等释放本结果拥有的证据租约。</summary>
    public void Dispose()
    {
        Actual?.Dispose();
        Reference?.Dispose();
        Delta?.Dispose();
    }
}
